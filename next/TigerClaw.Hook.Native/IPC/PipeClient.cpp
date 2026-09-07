#include "PipeClient.h"

#include "..\Common\Logger.h"
#include "..\Common\NativeHelpers.h"

#include <algorithm>
#include <atomic>
#include <sstream>
#include <string>

namespace
{
    const wchar_t* PipeName = L"\\\\.\\pipe\\BimeIPC";

    bool Transfer(HANDLE pipe, void* buffer, DWORD length, DWORD& transferred, bool write, DWORD timeout)
    {
        OVERLAPPED operation = {};
        operation.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!operation.hEvent) return false;
        BOOL ok = write ? WriteFile(pipe, buffer, length, &transferred, &operation)
                        : ReadFile(pipe, buffer, length, &transferred, &operation);
        DWORD failure = ok ? ERROR_SUCCESS : GetLastError();
        if (!ok && failure == ERROR_IO_PENDING)
        {
            DWORD wait = WaitForSingleObject(operation.hEvent, timeout);
            if (wait == WAIT_OBJECT_0)
            {
                ok = GetOverlappedResult(pipe, &operation, &transferred, FALSE);
                failure = ok ? ERROR_SUCCESS : GetLastError();
            }
            else
            {
                CancelIoEx(pipe, &operation);
                GetOverlappedResult(pipe, &operation, &transferred, TRUE);
                failure = wait == WAIT_TIMEOUT ? ERROR_TIMEOUT : ERROR_GEN_FAILURE;
            }
        }
        CloseHandle(operation.hEvent);
        SetLastError(failure);
        return ok != FALSE;
    }
}

namespace TigerClawHookNative
{
    PipeClient::PipeClient()
    {
        _clientSession = "hook-" + std::to_string(GetCurrentProcessId()) + "-" + std::to_string(GetTickCount64());
    }

    std::string PipeClient::PrepareKey(const KeyboardHookEvent& keyEvent, const HookState& state, const CaretSnapshot& caret)
    {
        return BuildKeyJson(keyEvent, state, caret);
    }

    bool PipeClient::TrySendPreparedKey(const std::string& request, const FocusSnapshot& focus, CoreResponse& response, std::wstring& error)
    {
        if (!EnsureRequestPipe(error)) return false;
        // Order focus before this key on the same stream, including reconnects.
        if ((!_requestFocusKnown || !_requestFocus.Equals(focus)) &&
            !TryWriteLine(_requestPipe, BuildFocusJson(focus), error))
        {
            DisconnectRequestPipe();
            return false;
        }
        _requestFocus = focus;
        _requestFocusKnown = true;
        return TrySendRequest(request, response, error);
    }

    bool PipeClient::TryCancelForRecovery(std::wstring& error)
    {
        if (!EnsureRequestPipe(error)) return false;
        if (TryWriteLine(_requestPipe, BuildCompositionCanceledJson(), error)) return true;
        DisconnectRequestPipe();
        return false;
    }

    PipeClient::~PipeClient()
    {
        DisconnectRequestPipe();
        DisconnectNotifyPipe();
    }

    bool PipeClient::TryHello(CoreResponse& response, std::wstring& error)
    {
        if (!EnsureRequestPipe(error))
        {
            return false;
        }

        return TrySendRequest("{\"type\":\"hello\",\"seq\":1,\"frontend\":\"hook_native\"}", response, error);
    }

    bool PipeClient::TrySendKey(const KeyboardHookEvent& keyEvent, const HookState& state, const FocusSnapshot& focus, const CaretSnapshot& caret, CoreResponse& response, std::wstring& error)
    {
        return TrySendPreparedKey(PrepareKey(keyEvent, state, caret), focus, response, error);
    }

    bool PipeClient::TrySendFocus(const FocusSnapshot& focus, std::wstring& error)
    {
        bool sent = TrySendNotification(BuildFocusJson(focus), error);
        _focusSyncRequired = !sent;
        return sent;
    }

    bool PipeClient::TrySendCaret(const CaretSnapshot& caret, std::wstring& error)
    {
        return TrySendNotification(BuildCaretJson(caret), error);
    }

    bool PipeClient::TrySendCompositionCanceled(std::wstring& error)
    {
        return TrySendNotification(BuildCompositionCanceledJson(), error);
    }

    bool PipeClient::TrySendHookDisabled(bool disabled, std::wstring& error)
    {
        return TrySendNotification(BuildHookDisabledJson(disabled), error);
    }

    bool PipeClient::IsCommunicationBlocked() const
    {
        return _communicationBlocked;
    }

    bool PipeClient::EnsureRequestPipe(std::wstring& error)
    {
        if (_communicationBlocked)
        {
            error = L"Core communication blocked by integrity verification.";
            return false;
        }

        if (_requestPipe != INVALID_HANDLE_VALUE)
        {
            return true;
        }

        if (!WaitNamedPipeW(PipeName, ConnectTimeoutMs))
        {
            error = L"Request pipe connect failed: " + GetLastErrorMessage(GetLastError());
            return false;
        }

        _requestPipe = CreateFileW(PipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
        if (_requestPipe == INVALID_HANDLE_VALUE)
        {
            error = L"Request pipe connect failed: " + GetLastErrorMessage(GetLastError());
            return false;
        }

        std::wstring serverPath;
        if (!_coreIntegrity.VerifyPipeServerExecutable(_requestPipe, serverPath, error))
        {
            DisconnectRequestPipe();
            _communicationBlocked = true;
            Logger::Info(L"integrity", std::wstring(L"blocked request pipe server=") + serverPath);
            return false;
        }

        return true;
    }

    bool PipeClient::EnsureNotifyPipe(std::wstring& error)
    {
        if (_communicationBlocked)
        {
            error = L"Core communication blocked by integrity verification.";
            return false;
        }

        if (_notifyPipe != INVALID_HANDLE_VALUE)
        {
            return true;
        }

        if (!WaitNamedPipeW(PipeName, NotifyTimeoutMs))
        {
            error = L"Notify pipe connect failed: " + GetLastErrorMessage(GetLastError());
            return false;
        }

        _notifyPipe = CreateFileW(PipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
        if (_notifyPipe == INVALID_HANDLE_VALUE)
        {
            error = L"Notify pipe connect failed: " + GetLastErrorMessage(GetLastError());
            return false;
        }

        std::wstring serverPath;
        if (!_coreIntegrity.VerifyPipeServerExecutable(_notifyPipe, serverPath, error))
        {
            DisconnectNotifyPipe();
            _communicationBlocked = true;
            Logger::Info(L"integrity", std::wstring(L"blocked notify pipe server=") + serverPath);
            return false;
        }

        return true;
    }

    void PipeClient::DisconnectRequestPipe()
    {
        _requestFocusKnown = false;
        _focusSyncRequired = true;
        if (_requestPipe != INVALID_HANDLE_VALUE)
        {
            CloseHandle(_requestPipe);
            _requestPipe = INVALID_HANDLE_VALUE;
        }
    }

    void PipeClient::DisconnectNotifyPipe()
    {
        _focusSyncRequired = true;
        if (_notifyPipe != INVALID_HANDLE_VALUE)
        {
            CloseHandle(_notifyPipe);
            _notifyPipe = INVALID_HANDLE_VALUE;
        }
    }

    bool PipeClient::TryWriteLine(HANDLE pipe, const std::string& line, std::wstring& error) const
    {
        std::string payload = line + "\n";
        DWORD written = 0;
        if (!Transfer(pipe, &payload[0], static_cast<DWORD>(payload.size()), written, true, ResponseTimeoutMs))
        {
            error = L"Pipe write failed: " + GetLastErrorMessage(GetLastError());
            return false;
        }

        return written == payload.size();
    }

    bool PipeClient::TryReadLine(HANDLE pipe, std::string& line, DWORD timeoutMs, std::wstring& error) const
    {
        line.clear();
        std::string pending;
        const ULONGLONG deadline = GetTickCount64() + timeoutMs;

        while (true)
        {
            if (GetTickCount64() >= deadline)
            {
                error = L"Core response timeout.";
                return false;
            }
            DWORD available = 0;
            if (!PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr))
            {
                error = L"Pipe read failed: " + GetLastErrorMessage(GetLastError());
                return false;
            }

            if (available == 0)
            {
                if (GetTickCount64() >= deadline)
                {
                    error = L"Core response timeout.";
                    return false;
                }

                Sleep(5);
                continue;
            }

            char buffer[256] = {};
            DWORD toRead = std::min<DWORD>(available, static_cast<DWORD>(sizeof(buffer)));
            DWORD read = 0;
            if (!Transfer(pipe, buffer, toRead, read, false, timeoutMs))
            {
                error = L"Pipe read failed: " + GetLastErrorMessage(GetLastError());
                return false;
            }

            if (read == 0)
            {
                error = L"Pipe disconnected.";
                return false;
            }

            pending.append(buffer, buffer + read);
            const size_t newlineIndex = pending.find('\n');
            if (newlineIndex != std::string::npos)
            {
                line = pending.substr(0, newlineIndex);
                while (!line.empty() && line.back() == '\r')
                {
                    line.pop_back();
                }

                return true;
            }
        }
    }

    bool PipeClient::TrySendRequest(const std::string& line, CoreResponse& response, std::wstring& error)
    {
        if (!EnsureRequestPipe(error))
        {
            return false;
        }

        std::string responseLine;
        if (!TryWriteLine(_requestPipe, line, error) || !TryReadLine(_requestPipe, responseLine, ResponseTimeoutMs, error))
        {
            DisconnectRequestPipe();
            return false;
        }

        response = ParseResponse(responseLine);
        const auto requestSeq = line.find("\"seq\":");
        const auto responseSeq = responseLine.find("\"seq\":");
        if (requestSeq == std::string::npos || responseSeq == std::string::npos ||
            strtol(line.c_str() + requestSeq + 6, nullptr, 10) !=
            strtol(responseLine.c_str() + responseSeq + 6, nullptr, 10) || !response.Success)
        {
            error = L"Invalid Core response.";
            DisconnectRequestPipe();
            return false;
        }
        return true;
    }

    bool PipeClient::TrySendNotification(const std::string& line, std::wstring& error)
    {
        if (!EnsureNotifyPipe(error))
        {
            return false;
        }

        if (!TryWriteLine(_notifyPipe, line, error))
        {
            DisconnectNotifyPipe();
            return false;
        }

        return true;
    }

    std::string PipeClient::BuildKeyJson(const KeyboardHookEvent& keyEvent, const HookState& state, const CaretSnapshot& caret)
    {
        std::ostringstream stream;
        const long seq = InterlockedIncrement(&_nextSeq);
        stream << "{\"type\":\"key\",\"seq\":" << seq
               << ",\"client_session\":\"" << _clientSession << "\""
               << ",\"event_id\":\"" << ++_nextEventId << "\""
               << ",\"vk\":" << keyEvent.VirtualKey
               << ",\"scan\":" << keyEvent.ScanCode
               << ",\"action\":\"" << (keyEvent.IsKeyDown ? "down" : "up") << "\""
               << ",\"shift\":" << (state.ShiftDown() ? "true" : "false")
               << ",\"ctrl\":" << (state.CtrlDown() ? "true" : "false")
               << ",\"alt\":" << (state.AltDown() ? "true" : "false")
               << ",\"win\":" << (state.WinDown() ? "true" : "false")
               << ",\"capsLock\":" << (state.CapsLockOn() ? "true" : "false")
               << ",\"numLock\":" << (state.NumLockOn() ? "true" : "false")
               << ",\"repeat\":1"
               << ",\"extended\":" << (keyEvent.IsExtended ? "true" : "false")
               << ",\"caret_x\":" << caret.X
               << ",\"caret_y\":" << caret.Y
               << ",\"width\":" << caret.Width
               << ",\"height\":" << caret.Height
               << ",\"frontend\":\"hook_native\"}";
        return stream.str();
    }

    std::string PipeClient::BuildFocusJson(const FocusSnapshot& focus)
    {
        std::ostringstream stream;
        const long seq = InterlockedIncrement(&_nextSeq);
        stream << "{\"type\":\"focus\",\"seq\":" << seq
               << ",\"hwnd\":" << static_cast<long long>(reinterpret_cast<UINT_PTR>(focus.Window))
               << ",\"processId\":" << focus.ProcessId
               << ",\"processName\":\"" << EscapeJson(focus.ProcessName) << "\""
               << ",\"className\":\"" << EscapeJson(focus.ClassName) << "\""
               << ",\"windowTitle\":\"" << EscapeJson(focus.WindowTitle) << "\""
               << ",\"frontend\":\"hook_native\"}";
        return stream.str();
    }

    std::string PipeClient::BuildCaretJson(const CaretSnapshot& caret)
    {
        std::ostringstream stream;
        const long seq = InterlockedIncrement(&_nextSeq);
        stream << "{\"type\":\"caret\",\"seq\":" << seq
               << ",\"x\":" << caret.X
               << ",\"y\":" << caret.Y
               << ",\"width\":" << caret.Width
               << ",\"height\":" << caret.Height
               << ",\"frontend\":\"hook_native\"}";
        return stream.str();
    }

    std::string PipeClient::BuildCompositionCanceledJson()
    {
        std::ostringstream stream;
        const long seq = InterlockedIncrement(&_nextSeq);
        stream << "{\"type\":\"composition_canceled\",\"seq\":" << seq
               << ",\"frontend\":\"hook_native\"}";
        return stream.str();
    }

    std::string PipeClient::BuildHookDisabledJson(bool disabled)
    {
        std::ostringstream stream;
        const long seq = InterlockedIncrement(&_nextSeq);
        stream << "{\"type\":\"hook_native_disabled\",\"seq\":" << seq
               << ",\"disabled\":" << (disabled ? "true" : "false")
               << ",\"frontend\":\"hook_native\"}";
        return stream.str();
    }

    CoreResponse PipeClient::ParseResponse(const std::string& json)
    {
        CoreResponse response = {};
        response.Success = ExtractJsonBool(json, "success", false);
        response.Handled = ExtractJsonBool(json, "handled", false);
        response.CommitText = WideFromUtf8(ExtractJsonString(json, "commit_text"));
        response.InputBuffer = WideFromUtf8(ExtractJsonString(json, "input_buffer"));
        response.KeyboardOpen = ExtractJsonBool(json, "keyboard_open", false);
        response.CancelComposition = ExtractJsonBool(json, "cancel_composition", false);
        response.EnsureSystemLayoutEn = ExtractJsonBool(json, "ensure_system_layout_en", false);
        response.NativeHookAltBackslashToggleEnabled = ExtractJsonBool(json, "native_hook_alt_backslash_toggle_enabled", true);
        response.AutoSwitchSystemLayoutEnabled = ExtractJsonBool(json, "auto_switch_system_layout_enabled", true);
        response.UseClipboardCommit = ExtractJsonBool(json, "use_clipboard_commit", false);
        response.ClipboardCommitWhitelist = WideFromUtf8(ExtractJsonString(json, "clipboard_commit_whitelist"));
        return response;
    }

    std::string PipeClient::EscapeJson(const std::wstring& value)
    {
        std::string utf8 = Utf8FromWide(value);
        std::string escaped;
        escaped.reserve(utf8.size());
        for (char ch : utf8)
        {
            switch (ch)
            {
            case '\\': escaped += "\\\\"; break;
            case '"': escaped += "\\\""; break;
            case '\r': escaped += "\\r"; break;
            case '\n': escaped += "\\n"; break;
            case '\t': escaped += "\\t"; break;
            default: escaped.push_back(ch); break;
            }
        }

        return escaped;
    }

    std::string PipeClient::ExtractJsonString(const std::string& json, const char* field)
    {
        const std::string key = std::string("\"") + field + "\":\"";
        size_t start = json.find(key);
        if (start == std::string::npos)
        {
            return std::string();
        }

        start += key.size();
        std::string value;
        bool escaping = false;
        for (size_t i = start; i < json.size(); ++i)
        {
            const char ch = json[i];
            if (escaping)
            {
                switch (ch)
                {
                case '\\': value.push_back('\\'); break;
                case '"': value.push_back('"'); break;
                case 'n': value.push_back('\n'); break;
                case 'r': value.push_back('\r'); break;
                case 't': value.push_back('\t'); break;
                default: value.push_back(ch); break;
                }

                escaping = false;
                continue;
            }

            if (ch == '\\')
            {
                escaping = true;
                continue;
            }

            if (ch == '"')
            {
                return value;
            }

            value.push_back(ch);
        }

        return value;
    }

    bool PipeClient::ExtractJsonBool(const std::string& json, const char* field, bool fallback)
    {
        const std::string key = std::string("\"") + field + "\":";
        size_t start = json.find(key);
        if (start == std::string::npos)
        {
            return fallback;
        }

        start += key.size();
        while (start < json.size() && (json[start] == ' ' || json[start] == '\t'))
        {
            ++start;
        }

        if (json.compare(start, 4, "true") == 0)
        {
            return true;
        }

        if (json.compare(start, 5, "false") == 0)
        {
            return false;
        }

        return fallback;
    }
}
