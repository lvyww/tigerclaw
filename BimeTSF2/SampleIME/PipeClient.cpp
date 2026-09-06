#include "Private.h"
#include "Globals.h"
#include "PipeClient.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static LONG ExtractSeqFromJson(const char *jsonMessage)
{
    if (jsonMessage == nullptr)
    {
        return -1;
    }

    const char *pos = strstr(jsonMessage, "\"seq\":");
    if (pos == nullptr)
    {
        return -1;
    }

    pos += 6;
    return static_cast<LONG>(strtol(pos, nullptr, 10));
}

static void LogPipeFailure(const char *stage, DWORD errorCode, LONG seq)
{
    if (seq >= 0)
    {
        Global::LogToFile("CPipeClient: %s failed seq=%ld err=%lu", stage, seq, errorCode);
    }
    else
    {
        Global::LogToFile("CPipeClient: %s failed err=%lu", stage, errorCode);
    }
}

// Opt-in physical-message capture for the Core differential suite. The
// environment variable defaults off, so normal releases never record input.
// Each TSF host thread gets a separate JSONL file to avoid line interleaving.
static void CaptureDifferentialMessage(const char *jsonMessage)
{
    if (jsonMessage == nullptr || jsonMessage[0] == '\0')
    {
        return;
    }

    WCHAR directory[MAX_PATH] = {};
    DWORD directoryLength = GetEnvironmentVariableW(
        L"BIME_TSF_TRACE_DIR", directory, ARRAYSIZE(directory));
    if (directoryLength == 0 || directoryLength >= ARRAYSIZE(directory))
    {
        return;
    }

    CreateDirectoryW(directory, nullptr);
    WCHAR path[MAX_PATH] = {};
    int written = swprintf_s(
        path,
        ARRAYSIZE(path),
        L"%ls\\core-trace-%lu-%lu.jsonl",
        directory,
        GetCurrentProcessId(),
        GetCurrentThreadId());
    if (written <= 0)
    {
        return;
    }

    HANDLE file = CreateFileW(
        path,
        FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr,
        OPEN_ALWAYS,
        FILE_ATTRIBUTE_NORMAL,
        nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        return;
    }

    DWORD ignored = 0;
    DWORD length = static_cast<DWORD>(strlen(jsonMessage));
    WriteFile(file, jsonMessage, length, &ignored, nullptr);
    if (length == 0 || jsonMessage[length - 1] != '\n')
    {
        static const char newline = '\n';
        WriteFile(file, &newline, 1, &ignored, nullptr);
    }
    CloseHandle(file);
}

static void PumpCurrentThreadNonInputMessages()
{
    MSG msg = {};

    // Keep the wait loop responsive without dispatching keyboard/mouse input.
    while (PeekMessage(&msg, nullptr, WM_PAINT, WM_PAINT, PM_REMOVE))
    {
        TranslateMessage(&msg);
        DispatchMessage(&msg);
    }

    while (PeekMessage(&msg, nullptr, WM_TIMER, WM_TIMER, PM_REMOVE))
    {
        TranslateMessage(&msg);
        DispatchMessage(&msg);
    }

    while (PeekMessage(&msg, nullptr, WM_QUIT, WM_QUIT, PM_REMOVE))
    {
        PostQuitMessage(static_cast<int>(msg.wParam));
    }
}

CPipeClient::CPipeClient() : _hPipe(INVALID_HANDLE_VALUE), _isConnected(FALSE), _seq(0), _keyEventSeq(0), _helloDone(FALSE)
{
    sprintf_s(_clientSession,
              sizeof(_clientSession),
              "%lu-%lu-%llu",
              GetCurrentProcessId(),
              GetCurrentThreadId(),
              GetTickCount64());
    Global::LogToFileVerbose("CPipeClient: ctor");
}

CPipeClient::~CPipeClient()
{
    Global::LogToFileVerbose("CPipeClient: dtor");
    Disconnect();
}

BOOL CPipeClient::Connect()
{
    if (_isConnected)
    {
        return TRUE;
    }
    return TryConnect();
}

void CPipeClient::Disconnect()
{
    if (_hPipe != INVALID_HANDLE_VALUE)
    {
        CloseHandle(_hPipe);
        _hPipe = INVALID_HANDLE_VALUE;
    }
    _isConnected = FALSE;
    _helloDone = FALSE;
}

BOOL CPipeClient::IsConnected() const
{
    return _isConnected;
}

BOOL CPipeClient::GetConnectedServerProcessPath(_Out_writes_(pathCount) WCHAR *path, size_t pathCount) const
{
    if (path == nullptr || pathCount < 2)
    {
        return FALSE;
    }

    path[0] = L'\0';

    if (!_isConnected || _hPipe == INVALID_HANDLE_VALUE)
    {
        return FALSE;
    }

    ULONG serverProcessId = 0;
    if (!GetNamedPipeServerProcessId(_hPipe, &serverProcessId) || serverProcessId == 0)
    {
        Global::LogToFile("CPipeClient: GetNamedPipeServerProcessId failed err=%lu", GetLastError());
        return FALSE;
    }

    HANDLE processHandle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, serverProcessId);
    if (processHandle == nullptr)
    {
        Global::LogToFile("CPipeClient: OpenProcess failed pid=%lu err=%lu", serverProcessId, GetLastError());
        return FALSE;
    }

    DWORD imagePathCount = static_cast<DWORD>(pathCount);
    BOOL ok = QueryFullProcessImageNameW(processHandle, 0, path, &imagePathCount);
    DWORD err = ok ? ERROR_SUCCESS : GetLastError();
    CloseHandle(processHandle);

    if (!ok || imagePathCount == 0)
    {
        path[0] = L'\0';
        Global::LogToFile("CPipeClient: QueryFullProcessImageNameW failed pid=%lu err=%lu", serverProcessId, err);
        return FALSE;
    }

    Global::LogToFileVerbose("CPipeClient: connected_server pid=%lu path=%ls", serverProcessId, path);
    return TRUE;
}

BOOL CPipeClient::TryConnect()
{
    if (!WaitNamedPipe(BIME_PIPE_NAME, 0))
    {
        LogPipeFailure("WaitNamedPipe", GetLastError(), -1);
        return FALSE;
    }

    _hPipe = CreateFile(BIME_PIPE_NAME,
                        GENERIC_READ | GENERIC_WRITE,
                        0,
                        nullptr,
                        OPEN_EXISTING,
                        FILE_FLAG_OVERLAPPED,
                        nullptr);
    if (_hPipe == INVALID_HANDLE_VALUE)
    {
        LogPipeFailure("CreateFile", GetLastError(), -1);
        return FALSE;
    }

    DWORD mode = PIPE_READMODE_MESSAGE;
    if (!SetNamedPipeHandleState(_hPipe, &mode, nullptr, nullptr))
    {
        LogPipeFailure("SetNamedPipeHandleState", GetLastError(), -1);
        Disconnect();
        return FALSE;
    }

    _isConnected = TRUE;
    _helloDone = FALSE;
    Global::LogToFileVerbose("CPipeClient: connected");
    return TRUE;
}

// The pipe handle is opened with FILE_FLAG_OVERLAPPED, so writes MUST pass an OVERLAPPED;
// otherwise the write may not complete synchronously and bytesWritten can be 0 while the I/O is
// still pending, causing false failures. Mirrors ReadResponse but uses WaitForSingleObject (no msg pump).
BOOL CPipeClient::WriteMessageOverlapped(const char *data, size_t len, DWORD timeoutMs)
{
    if (data == nullptr || _hPipe == INVALID_HANDLE_VALUE)
    {
        return FALSE;
    }

    OVERLAPPED ov = {};
    ov.hEvent = CreateEvent(nullptr, TRUE, FALSE, nullptr);
    if (ov.hEvent == nullptr)
    {
        LogPipeFailure("WriteMessageOverlapped.CreateEvent", GetLastError(), -1);
        return FALSE;
    }

    DWORD bytesWritten = 0;
    BOOL ok = WriteFile(_hPipe, data, static_cast<DWORD>(len), &bytesWritten, &ov);
    if (!ok)
    {
        DWORD err = GetLastError();
        if (err == ERROR_IO_PENDING)
        {
            DWORD waitResult = WaitForSingleObject(ov.hEvent, timeoutMs);
            if (waitResult != WAIT_OBJECT_0)
            {
                CancelIo(_hPipe);
                SetLastError(waitResult == WAIT_TIMEOUT ? ERROR_TIMEOUT : ERROR_GEN_FAILURE);
                CloseHandle(ov.hEvent);
                return FALSE;
            }

            if (!GetOverlappedResult(_hPipe, &ov, &bytesWritten, FALSE))
            {
                DWORD ge = GetLastError();
                CloseHandle(ov.hEvent);
                SetLastError(ge);
                return FALSE;
            }
        }
        else
        {
            CloseHandle(ov.hEvent);
            SetLastError(err);
            return FALSE;
        }
    }

    CloseHandle(ov.hEvent);
    return (bytesWritten == static_cast<DWORD>(len));
}

BOOL CPipeClient::SendMessage(const char *jsonMessage)
{
    if (jsonMessage == nullptr)
    {
        return FALSE;
    }

    CaptureDifferentialMessage(jsonMessage);

    if (!_isConnected && !Connect())
    {
        LogPipeFailure("SendMessage.Connect", GetLastError(), ExtractSeqFromJson(jsonMessage));
        return FALSE;
    }

    size_t len = strlen(jsonMessage);
    if (!WriteMessageOverlapped(jsonMessage, len, BIME_PIPE_WRITE_TIMEOUT_MS))
    {
        LogPipeFailure("SendMessage.WriteFile", GetLastError(), ExtractSeqFromJson(jsonMessage));
        Disconnect();
        return FALSE;
    }
    return TRUE;
}

HRESULT CPipeClient::SendMessageAndWait(const char *jsonMessage, _Out_ BimeResponse *pResponse, DWORD timeoutMs)
{
    if (jsonMessage == nullptr || pResponse == nullptr)
    {
        return E_INVALIDARG;
    }

    CaptureDifferentialMessage(jsonMessage);

    pResponse->seq = -1;
    pResponse->success = FALSE;
    pResponse->handled = FALSE;
    pResponse->textToOutput.clear();
    pResponse->inputBuffer.clear();
    pResponse->hasProtocolVersion = FALSE;
    pResponse->protocolVersion = 0;
    pResponse->coreBuild.clear();
    pResponse->coreCommit.clear();
    pResponse->coreBranch.clear();
    pResponse->corePath.clear();
    pResponse->hasKeyboardOpen = FALSE;
    pResponse->keyboardOpen = FALSE;
    pResponse->compositionTracking = FALSE;
    pResponse->compositionPending = FALSE;
    pResponse->cancelComposition = FALSE;

    LONG seq = ExtractSeqFromJson(jsonMessage);

    if (!_isConnected && !Connect())
    {
        LogPipeFailure("SendMessageAndWait.Connect", GetLastError(), seq);
        return E_FAIL;
    }

    size_t len = strlen(jsonMessage);
    BOOL writeOk = WriteMessageOverlapped(jsonMessage, len, BIME_PIPE_WRITE_TIMEOUT_MS);
    if (!writeOk)
    {
        DWORD writeError = GetLastError();
        BOOL canReconnect = (writeError == ERROR_BROKEN_PIPE ||
                             writeError == ERROR_PIPE_NOT_CONNECTED ||
                             writeError == ERROR_NO_DATA ||
                             writeError == ERROR_INVALID_HANDLE);

        if (canReconnect)
        {
            Global::LogToFileVerbose("CPipeClient: write failed retry_connect seq=%ld err=%lu", seq, writeError);
            Disconnect();
            if (Connect())
            {
                writeOk = WriteMessageOverlapped(jsonMessage, len, BIME_PIPE_WRITE_TIMEOUT_MS);
                if (!writeOk)
                {
                    writeError = GetLastError();
                }
            }
        }

        if (!writeOk)
        {
            LogPipeFailure("SendMessageAndWait.WriteFile", writeError, seq);
            Disconnect();
            return E_FAIL;
        }
    }

    ULONGLONG deadline = GetTickCount64() + timeoutMs;

    while (true)
    {
        DWORD remaining = timeoutMs;
        if (timeoutMs != INFINITE)
        {
            ULONGLONG now = GetTickCount64();
            if (now >= deadline)
            {
                SetLastError(ERROR_TIMEOUT);
                LogPipeFailure("SendMessageAndWait.ReadResponse.Timeout", ERROR_TIMEOUT, seq);
                return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
            }

            remaining = static_cast<DWORD>(deadline - now);
        }

        char responseBuffer[BIME_PIPE_BUFFER_SIZE];
        if (!ReadResponse(responseBuffer, sizeof(responseBuffer), remaining))
        {
            DWORD readError = GetLastError();
            LogPipeFailure("SendMessageAndWait.ReadResponse", readError, seq);
            if (readError == ERROR_BROKEN_PIPE ||
                readError == ERROR_PIPE_NOT_CONNECTED ||
                readError == ERROR_NO_DATA ||
                readError == ERROR_INVALID_HANDLE)
            {
                Disconnect();
            }
            return (readError == ERROR_TIMEOUT) ? HRESULT_FROM_WIN32(ERROR_TIMEOUT) : E_FAIL;
        }

        LONG responseSeq = ExtractSeqFromJson(responseBuffer);
        if (seq >= 0 && responseSeq >= 0 && responseSeq != seq)
        {
            Global::LogToFileVerbose("CPipeClient: response seq mismatch req=%ld rsp=%ld payload=%s", seq, responseSeq, responseBuffer);
            continue;
        }

        if (!ParseResponse(responseBuffer, pResponse))
        {
            Global::LogToFile("CPipeClient: ParseResponse failed seq=%ld payload=%s", seq, responseBuffer);
            return E_FAIL;
        }

        if (seq >= 0)
        {
            Global::LogToFileVerbose("CPipeClient: response seq=%ld handled=%d success=%d text_len=%u",
                                     seq,
                                     pResponse->handled,
                                     pResponse->success,
                                     static_cast<unsigned>(pResponse->textToOutput.length()));
        }

        return S_OK;
    }
}

HRESULT CPipeClient::SendKeyAndWait(UINT vkCode,
                                    UINT scanCode,
                                    BOOL isKeyDown,
                                    _In_opt_z_ const char *tsfStage,
                                    BOOL shift,
                                    BOOL ctrl,
                                    BOOL alt,
                                    BOOL win,
                                    BOOL capsLock,
                                    BOOL numLock,
                                    UINT repeat,
                                    BOOL extended,
                                    BOOL caretValid,
                                    LONG caretX,
                                    LONG caretY,
                                    _Out_ BimeResponse *pResponse,
                                    DWORD timeoutMs,
                                    ULONGLONG eventId)
{
    if (pResponse == nullptr)
    {
        return E_INVALIDARG;
    }

    LONG currentSeq = InterlockedIncrement(&_seq);
    if (eventId == 0)
    {
        eventId = NextKeyEventId();
    }

    char message[768];
    int length = 0;
    if (caretValid)
    {
        length = sprintf_s(message,
                           sizeof(message),
                           "{\"type\":\"key\",\"seq\":%ld,\"client_session\":\"%s\",\"event_id\":\"%llu\",\"action\":\"%s\",\"vk\":%u,\"scan\":%u,"
                           "\"shift\":%s,\"ctrl\":%s,\"alt\":%s,\"win\":%s,\"capsLock\":%s,\"numLock\":%s,"
                           "\"repeat\":%u,\"extended\":%s,\"tsf_stage\":\"%s\",\"caret_x\":%ld,\"caret_y\":%ld}\n",
                           currentSeq,
                           _clientSession,
                           eventId,
                           isKeyDown ? "down" : "up",
                           vkCode,
                           scanCode,
                           shift ? "true" : "false",
                           ctrl ? "true" : "false",
                           alt ? "true" : "false",
                           win ? "true" : "false",
                           capsLock ? "true" : "false",
                           numLock ? "true" : "false",
                           repeat,
                           extended ? "true" : "false",
                           tsfStage != nullptr ? tsfStage : "",
                           caretX,
                           caretY);
    }
    else
    {
        length = sprintf_s(message,
                           sizeof(message),
                           "{\"type\":\"key\",\"seq\":%ld,\"client_session\":\"%s\",\"event_id\":\"%llu\",\"action\":\"%s\",\"vk\":%u,\"scan\":%u,"
                           "\"shift\":%s,\"ctrl\":%s,\"alt\":%s,\"win\":%s,\"capsLock\":%s,\"numLock\":%s,"
                           "\"repeat\":%u,\"extended\":%s,\"tsf_stage\":\"%s\"}\n",
                           currentSeq,
                           _clientSession,
                           eventId,
                           isKeyDown ? "down" : "up",
                           vkCode,
                           scanCode,
                           shift ? "true" : "false",
                           ctrl ? "true" : "false",
                           alt ? "true" : "false",
                           win ? "true" : "false",
                           capsLock ? "true" : "false",
                           numLock ? "true" : "false",
                           repeat,
                           extended ? "true" : "false",
                           tsfStage != nullptr ? tsfStage : "");
    }

    if (length <= 0)
    {
        Global::LogToFile("CPipeClient: SendKeyAndWait format failed seq=%ld", currentSeq);
        return E_FAIL;
    }

    Global::LogToFileVerbose("CPipeClient: send key seq=%ld action=%s vk=%u scan=%u repeat=%u ext=%d caret=%d",
                      currentSeq,
                      isKeyDown ? "down" : "up",
                      vkCode,
                      scanCode,
                      repeat,
                      extended,
                      caretValid);

    return SendMessageAndWait(message, pResponse, timeoutMs);
}

ULONGLONG CPipeClient::NextKeyEventId()
{
    return static_cast<ULONGLONG>(InterlockedIncrement64(&_keyEventSeq));
}

HRESULT CPipeClient::SendCtrlSpaceAndWait(_Out_ BimeResponse *pResponse, DWORD timeoutMs)
{
    if (pResponse == nullptr)
    {
        return E_INVALIDARG;
    }

    LONG currentSeq = InterlockedIncrement(&_seq);
    char message[128];
    int length = sprintf_s(message, sizeof(message), "{\"type\":\"ctrl_space\",\"seq\":%ld}\n", currentSeq);
    if (length <= 0)
    {
        Global::LogToFile("CPipeClient: SendCtrlSpaceAndWait format failed seq=%ld", currentSeq);
        return E_FAIL;
    }

    Global::LogToFileVerbose("CPipeClient: send ctrl_space seq=%ld", currentSeq);
    return SendMessageAndWait(message, pResponse, timeoutMs);
}

HRESULT CPipeClient::SendShowMenuAndWait(_Out_ BimeResponse *pResponse, DWORD timeoutMs)
{
    if (pResponse == nullptr)
    {
        return E_INVALIDARG;
    }

    LONG currentSeq = InterlockedIncrement(&_seq);
    char message[128];
    int length = sprintf_s(message, sizeof(message), "{\"type\":\"show_menu\",\"seq\":%ld}\n", currentSeq);
    if (length <= 0)
    {
        Global::LogToFile("CPipeClient: SendShowMenuAndWait format failed seq=%ld", currentSeq);
        return E_FAIL;
    }

    Global::LogToFileVerbose("CPipeClient: send show_menu seq=%ld", currentSeq);
    return SendMessageAndWait(message, pResponse, timeoutMs);
}

HRESULT CPipeClient::SendQueryStateAndWait(_Out_ BimeResponse *pResponse, DWORD timeoutMs)
{
    if (pResponse == nullptr)
    {
        return E_INVALIDARG;
    }

    LONG currentSeq = InterlockedIncrement(&_seq);
    char message[128];
    int length = sprintf_s(message, sizeof(message), "{\"type\":\"query_state\",\"seq\":%ld}\n", currentSeq);
    if (length <= 0)
    {
        Global::LogToFile("CPipeClient: SendQueryStateAndWait format failed seq=%ld", currentSeq);
        return E_FAIL;
    }

    Global::LogToFileVerbose("CPipeClient: send query_state seq=%ld", currentSeq);
    return SendMessageAndWait(message, pResponse, timeoutMs);
}

HRESULT CPipeClient::SendHelloAndWait(_Out_ BimeResponse *pResponse, DWORD timeoutMs)
{
    if (pResponse == nullptr)
    {
        return E_INVALIDARG;
    }

    LONG currentSeq = InterlockedIncrement(&_seq);
    char message[256];
    int length = sprintf_s(message,
                           sizeof(message),
                           "{\"type\":\"hello\",\"seq\":%ld,\"protocol_version\":%d}\n",
                           currentSeq,
                           BIME_PROTOCOL_VERSION);
    if (length <= 0)
    {
        Global::LogToFile("CPipeClient: SendHelloAndWait format failed seq=%ld", currentSeq);
        return E_FAIL;
    }

    Global::LogToFileVerbose("CPipeClient: send hello seq=%ld protocol=%d", currentSeq, BIME_PROTOCOL_VERSION);
    return SendMessageAndWait(message, pResponse, timeoutMs);
}

HRESULT CPipeClient::EnsureHelloHandshake(DWORD timeoutMs)
{
    if (_helloDone)
    {
        return S_OK;
    }

    if (!_isConnected && !Connect())
    {
        return E_FAIL;
    }

    BimeResponse response;
    HRESULT hr = SendHelloAndWait(&response, timeoutMs);
    if (FAILED(hr))
    {
        Global::LogToFileVerbose("CPipeClient: hello failed hr=0x%08X", static_cast<unsigned>(hr));
        return hr;
    }

    _helloDone = TRUE;
    if (response.hasProtocolVersion)
    {
        Global::LogToFileVerbose("CPipeClient: hello ok core_protocol=%ld core_build=%ls core_commit=%ls",
                                 response.protocolVersion,
                                 response.coreBuild.c_str(),
                                 response.coreCommit.c_str());
    }
    else
    {
        Global::LogToFileVerbose("CPipeClient: hello ok without protocol field");
    }

    return S_OK;
}

BOOL CPipeClient::SendFocusMessage(LONGLONG hwnd, DWORD processId)
{
    char message[256];
    int length = sprintf_s(message,
                           sizeof(message),
                           "{\"type\":\"focus\",\"hwnd\":%lld,\"processId\":%lu}\n",
                           hwnd,
                           processId);
    if (length <= 0)
    {
        Global::LogToFile("CPipeClient: SendFocusMessage format failed");
        return FALSE;
    }

    BOOL sent = SendMessage(message);
    Global::LogToFileVerbose("CPipeClient: focus sent=%d hwnd=%lld pid=%lu", sent, hwnd, processId);
    return sent;
}


BOOL CPipeClient::SendCaretMessage(LONG x, LONG y, LONG width, LONG height)
{
    char message[256];
    int length = sprintf_s(message,
                           sizeof(message),
                           "{\"type\":\"caret\",\"x\":%ld,\"y\":%ld,\"width\":%ld,\"height\":%ld}\n",
                           x,
                           y,
                           width,
                           height);
    if (length <= 0)
    {
        Global::LogToFile("CPipeClient: SendCaretMessage format failed");
        return FALSE;
    }

    BOOL sent = SendMessage(message);
    Global::LogToFileVerbose("CPipeClient: caret sent=%d x=%ld y=%ld w=%ld h=%ld", sent, x, y, width, height);
    return sent;
}

BOOL CPipeClient::SendCompositionCanceledMessage()
{
    static const char kMessage[] = "{\"type\":\"composition_canceled\"}\n";
    BOOL sent = SendMessage(kMessage);
    Global::LogToFileVerbose("CPipeClient: composition_canceled sent=%d", sent);
    return sent;
}

BOOL CPipeClient::SendImeActiveMessage(BOOL active)
{
    char message[64];
    int length = sprintf_s(message,
                           sizeof(message),
                           "{\"type\":\"ime_active\",\"active\":%s}\n",
                           active ? "true" : "false");
    if (length <= 0)
    {
        Global::LogToFile("CPipeClient: SendImeActiveMessage format failed");
        return FALSE;
    }

    BOOL sent = SendMessage(message);
    Global::LogToFileVerbose("CPipeClient: ime_active sent=%d active=%d", sent, active);
    return sent;
}

BOOL CPipeClient::ReadResponse(_Out_writes_bytes_(bufferSize) char *buffer, DWORD bufferSize, DWORD timeoutMs)
{
    if (buffer == nullptr || bufferSize < 2)
    {
        return FALSE;
    }

    memset(buffer, 0, bufferSize);

    OVERLAPPED overlapped = {};
    overlapped.hEvent = CreateEvent(nullptr, TRUE, FALSE, nullptr);
    if (overlapped.hEvent == nullptr)
    {
        LogPipeFailure("ReadResponse.CreateEvent", GetLastError(), -1);
        return FALSE;
    }

    DWORD bytesRead = 0;
    BOOL ok = ReadFile(_hPipe, buffer, bufferSize - 1, &bytesRead, &overlapped);
    if (!ok)
    {
        DWORD err = GetLastError();
        if (err == ERROR_IO_PENDING)
        {
            const ULONGLONG start = GetTickCount64();
            for (;;)
            {
                ULONGLONG elapsed = GetTickCount64() - start;
                DWORD remaining = (elapsed >= timeoutMs) ? 0 : static_cast<DWORD>(timeoutMs - elapsed);

                const DWORD waitMask = QS_POSTMESSAGE | QS_SENDMESSAGE | QS_TIMER | QS_PAINT;
                DWORD waitResult = MsgWaitForMultipleObjects(1, &overlapped.hEvent, FALSE, remaining, waitMask);
                if (waitResult == WAIT_OBJECT_0)
                {
                    break;
                }

                if (waitResult == WAIT_OBJECT_0 + 1)
                {
                    PumpCurrentThreadNonInputMessages();
                    continue;
                }

                CancelIo(_hPipe);
                if (waitResult == WAIT_TIMEOUT)
                {
                    SetLastError(ERROR_TIMEOUT);
                }
                else
                {
                    SetLastError(ERROR_GEN_FAILURE);
                }
                LogPipeFailure("ReadResponse.MsgWaitForMultipleObjects", waitResult, -1);
                CloseHandle(overlapped.hEvent);
                return FALSE;
            }

            if (!GetOverlappedResult(_hPipe, &overlapped, &bytesRead, FALSE))
            {
                LogPipeFailure("ReadResponse.GetOverlappedResult", GetLastError(), -1);
                CloseHandle(overlapped.hEvent);
                return FALSE;
            }
        }
        else
        {
            LogPipeFailure("ReadResponse.ReadFile", err, -1);
            CloseHandle(overlapped.hEvent);
            return FALSE;
        }
    }

    CloseHandle(overlapped.hEvent);
    if (bytesRead == 0)
    {
        SetLastError(ERROR_BROKEN_PIPE);
        return FALSE;
    }
    buffer[bytesRead] = '\0';
    return TRUE;
}

BOOL CPipeClient::ParseResponse(const char *json, _Out_ BimeResponse *pResponse)
{
    if (json == nullptr || pResponse == nullptr)
    {
        return FALSE;
    }

    auto parseBool = [](const char *source, const char *key) -> BOOL
    {
        char search[64];
        sprintf_s(search, sizeof(search), "\"%s\":", key);
        const char *pos = strstr(source, search);
        if (pos == nullptr)
        {
            return FALSE;
        }
        pos += strlen(search);
        while (*pos == ' ' || *pos == '\t')
        {
            ++pos;
        }
        return (strncmp(pos, "true", 4) == 0) ? TRUE : FALSE;
    };

    auto tryParseBool = [](const char *source, const char *key, _Out_ BOOL *pValue) -> BOOL
    {
        if (pValue == nullptr)
        {
            return FALSE;
        }

        char search[64];
        sprintf_s(search, sizeof(search), "\"%s\":", key);
        const char *pos = strstr(source, search);
        if (pos == nullptr)
        {
            return FALSE;
        }
        pos += strlen(search);
        while (*pos == ' ' || *pos == '\t')
        {
            ++pos;
        }

        if (strncmp(pos, "true", 4) == 0)
        {
            *pValue = TRUE;
            return TRUE;
        }
        if (strncmp(pos, "false", 5) == 0)
        {
            *pValue = FALSE;
            return TRUE;
        }

        return FALSE;
    };

    auto parseString = [](const char *source, const char *key, _Out_ std::wstring &outValue) -> BOOL
    {
        char search[64];
        sprintf_s(search, sizeof(search), "\"%s\":", key);
        const char *pos = strstr(source, search);
        if (pos == nullptr)
        {
            return FALSE;
        }
        pos += strlen(search);
        while (*pos == ' ' || *pos == '\t')
        {
            ++pos;
        }
        if (*pos != '"')
        {
            return FALSE;
        }
        ++pos;

        const char *end = pos;
        while (*end != '\0')
        {
            if (*end == '\\' && *(end + 1) != '\0')
            {
                end += 2;
                continue;
            }
            if (*end == '"')
            {
                break;
            }
            ++end;
        }
        if (*end != '"')
        {
            return FALSE;
        }

        std::string rawUtf8(pos, end - pos);
        if (rawUtf8.empty())
        {
            outValue.clear();
            return TRUE;
        }

        std::string utf8;
        utf8.reserve(rawUtf8.size());
        for (size_t i = 0; i < rawUtf8.size(); ++i)
        {
            char ch = rawUtf8[i];
            if (ch == '\\' && (i + 1) < rawUtf8.size())
            {
                char next = rawUtf8[i + 1];
                switch (next)
                {
                case '"':
                    utf8.push_back('"');
                    ++i;
                    continue;
                case '\\':
                    utf8.push_back('\\');
                    ++i;
                    continue;
                case '/':
                    utf8.push_back('/');
                    ++i;
                    continue;
                case 'n':
                    utf8.push_back('\n');
                    ++i;
                    continue;
                case 'r':
                    utf8.push_back('\r');
                    ++i;
                    continue;
                case 't':
                    utf8.push_back('\t');
                    ++i;
                    continue;
                case 'b':
                    utf8.push_back('\b');
                    ++i;
                    continue;
                case 'f':
                    utf8.push_back('\f');
                    ++i;
                    continue;
                default:
                    break;
                }
            }

            utf8.push_back(ch);
        }

        int required = MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), static_cast<int>(utf8.size()), nullptr, 0);
        if (required <= 0)
        {
            return FALSE;
        }

        outValue.resize(required);
        int converted = MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), static_cast<int>(utf8.size()), &outValue[0], required);
        if (converted <= 0)
        {
            outValue.clear();
            return FALSE;
        }
        return TRUE;
    };

    auto tryParseLong = [](const char *source, const char *key, _Out_ LONG *pValue) -> BOOL
    {
        if (pValue == nullptr)
        {
            return FALSE;
        }

        char search[64];
        sprintf_s(search, sizeof(search), "\"%s\":", key);
        const char *pos = strstr(source, search);
        if (pos == nullptr)
        {
            return FALSE;
        }
        pos += strlen(search);
        while (*pos == ' ' || *pos == '\t')
        {
            ++pos;
        }

        char *end = nullptr;
        long value = strtol(pos, &end, 10);
        if (end == pos)
        {
            return FALSE;
        }

        *pValue = static_cast<LONG>(value);
        return TRUE;
    };

    pResponse->seq = ExtractSeqFromJson(json);
    pResponse->success = parseBool(json, "success");
    pResponse->handled = parseBool(json, "handled");
    pResponse->expectKeyUp = TRUE;
    pResponse->hasProtocolVersion = FALSE;
    pResponse->protocolVersion = 0;
    pResponse->coreBuild.clear();
    pResponse->coreCommit.clear();
    pResponse->coreBranch.clear();
    pResponse->corePath.clear();
    pResponse->hasKeyboardOpen = FALSE;
    pResponse->keyboardOpen = FALSE;

    LONG protocolVersion = 0;
    if (tryParseLong(json, "protocol_version", &protocolVersion))
    {
        pResponse->hasProtocolVersion = TRUE;
        pResponse->protocolVersion = protocolVersion;
    }

    BOOL keyboardOpen = FALSE;
    if (tryParseBool(json, "keyboard_open", &keyboardOpen))
    {
        pResponse->hasKeyboardOpen = TRUE;
        pResponse->keyboardOpen = keyboardOpen;
    }
    tryParseBool(json, "expect_keyup", &pResponse->expectKeyUp);
    pResponse->cancelComposition = parseBool(json, "cancel_composition");
    pResponse->compositionTracking = parseBool(json, "composition_tracking");
    pResponse->compositionPending = parseBool(json, "composition_pending");
    parseString(json, "commit_text", pResponse->textToOutput);
    parseString(json, "input_buffer", pResponse->inputBuffer);
    parseString(json, "core_build", pResponse->coreBuild);
    parseString(json, "core_commit", pResponse->coreCommit);
    parseString(json, "core_branch", pResponse->coreBranch);
    parseString(json, "core_path", pResponse->corePath);
    return TRUE;
}
