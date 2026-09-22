#pragma once

#include "Private.h"
#include <string>

#define BIME_PIPE_NAME L"\\\\.\\pipe\\BimeIPC"
#define BIME_PIPE_BUFFER_SIZE 4096
#define BIME_DEFAULT_TIMEOUT_MS 200
#define BIME_PIPE_WRITE_TIMEOUT_MS 200
#define BIME_PROTOCOL_VERSION 2

struct BimeResponse
{
    LONG seq;
    BOOL success;
    BOOL handled;
    BOOL expectKeyUp;
    std::wstring learningReceipt; // Opaque commit receipt; never user text.
    std::wstring textToOutput;
    std::wstring inputBuffer;
    LONG inputCursor = -1; // Optional UTF-16 preedit caret; -1 preserves legacy end.
    BOOL hasProtocolVersion;
    LONG protocolVersion;
    std::wstring coreBuild;
    std::wstring coreCommit;
    std::wstring coreBranch;
    std::wstring corePath;
    BOOL hasKeyboardOpen;
    BOOL keyboardOpen;
    BOOL cancelComposition;
    BOOL compositionTracking;
    BOOL compositionPending;

    BimeResponse()
        : seq(-1),
          success(FALSE),
          handled(FALSE),
          expectKeyUp(TRUE),
          hasProtocolVersion(FALSE),
          protocolVersion(0),
          hasKeyboardOpen(FALSE),
          keyboardOpen(FALSE),
          cancelComposition(FALSE),
          compositionTracking(FALSE),
          compositionPending(FALSE)
    {
    }
};

class CPipeClient
{
public:
    CPipeClient();
    ~CPipeClient();

    BOOL Connect();
    void Disconnect();
    BOOL IsConnected() const;
    BOOL IsBusy() const { return _requestActive != 0; }

    BOOL SendMessage(const char *jsonMessage);
    HRESULT SendMessageAndWait(const char *jsonMessage, _Out_ BimeResponse *pResponse, DWORD timeoutMs = BIME_DEFAULT_TIMEOUT_MS);

    HRESULT SendKeyAndWait(UINT vkCode,
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
                           DWORD timeoutMs = BIME_DEFAULT_TIMEOUT_MS,
                           ULONGLONG eventId = 0);

    HRESULT SendCandidateAndWait(const char *token, UINT index, ULONGLONG eventId, BimeResponse *response, DWORD timeoutMs);
    ULONGLONG NextKeyEventId();

    HRESULT SendCtrlSpaceAndWait(_Out_ BimeResponse *pResponse, DWORD timeoutMs = BIME_DEFAULT_TIMEOUT_MS);
    HRESULT SendShowMenuAndWait(_Out_ BimeResponse *pResponse, DWORD timeoutMs = BIME_DEFAULT_TIMEOUT_MS);
    HRESULT SendQueryStateAndWait(_Out_ BimeResponse *pResponse, DWORD timeoutMs = BIME_DEFAULT_TIMEOUT_MS);
    HRESULT SendHelloAndWait(_Out_ BimeResponse *pResponse, DWORD timeoutMs = BIME_DEFAULT_TIMEOUT_MS);
    HRESULT EnsureHelloHandshake(DWORD timeoutMs = BIME_DEFAULT_TIMEOUT_MS);

    BOOL SendFocusMessage(LONGLONG hwnd, DWORD processId);
    BOOL SendCaretMessage(LONG x, LONG y, LONG width = 2, LONG height = 20);
    BOOL SendCompositionCanceledMessage();
    BOOL SendLearningCommit(const std::wstring& receipt, BOOL applied);
    BOOL SendImeActiveMessage(BOOL active);

private:
    HANDLE _hPipe;
    BOOL _isConnected;
    LONG _seq;
    LONGLONG _keyEventSeq;
    char _clientSession[64];
    BOOL _helloDone;
    volatile LONG _requestActive = 0;

    BOOL TryConnect();
    BOOL WriteMessageOverlapped(const char *data, size_t len, DWORD timeoutMs);
    BOOL ReadResponse(_Out_writes_bytes_(bufferSize) char *buffer, DWORD bufferSize, DWORD timeoutMs);
    BOOL ParseResponse(const char *json, _Out_ BimeResponse *pResponse);
};
