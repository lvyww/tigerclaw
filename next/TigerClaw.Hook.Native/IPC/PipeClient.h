#pragma once

#include <windows.h>

#include <string>

#include "..\Common\CoreIntegrity.h"
#include "..\Hook\KeyboardHook.h"
#include "..\State\CaretSnapshot.h"
#include "..\State\CoreResponse.h"
#include "..\State\FocusSnapshot.h"
#include "..\State\HookState.h"

namespace TigerClawHookNative
{
    class PipeClient
    {
    public:
        PipeClient();
        ~PipeClient();

        bool TryHello(CoreResponse& response, std::wstring& error);
        bool TrySendKey(const KeyboardHookEvent& keyEvent, const HookState& state, const FocusSnapshot& focus, const CaretSnapshot& caret, CoreResponse& response, std::wstring& error);
        bool TrySendFocus(const FocusSnapshot& focus, std::wstring& error);
        bool TrySendCaret(const CaretSnapshot& caret, std::wstring& error);
        bool TrySendCompositionCanceled(std::wstring& error);
        bool TrySendHookDisabled(bool disabled, std::wstring& error);
        bool IsCommunicationBlocked() const;
        bool NeedsFocusSync() const { return _focusSyncRequired; }
        std::string PrepareKey(const KeyboardHookEvent& keyEvent, const HookState& state, const CaretSnapshot& caret);
        bool TrySendPreparedKey(const std::string& request, const FocusSnapshot& focus, CoreResponse& response, std::wstring& error);
        bool TryCancelForRecovery(std::wstring& error);

    private:
        static constexpr DWORD ConnectTimeoutMs = 20;
        static constexpr DWORD NotifyTimeoutMs = 20;
        static constexpr DWORD ResponseTimeoutMs = 60;

        bool EnsureRequestPipe(std::wstring& error);
        bool EnsureNotifyPipe(std::wstring& error);
        void DisconnectRequestPipe();
        void DisconnectNotifyPipe();
        bool TryWriteLine(HANDLE pipe, const std::string& line, std::wstring& error) const;
        bool TryReadLine(HANDLE pipe, std::string& line, DWORD timeoutMs, std::wstring& error) const;
        bool TrySendRequest(const std::string& line, CoreResponse& response, std::wstring& error);
        bool TrySendNotification(const std::string& line, std::wstring& error);

        std::string BuildKeyJson(const KeyboardHookEvent& keyEvent, const HookState& state, const CaretSnapshot& caret);
        std::string BuildFocusJson(const FocusSnapshot& focus);
        std::string BuildCaretJson(const CaretSnapshot& caret);
        std::string BuildCompositionCanceledJson();
        std::string BuildHookDisabledJson(bool disabled);
        static CoreResponse ParseResponse(const std::string& json);
        static std::string EscapeJson(const std::wstring& value);
        static std::string ExtractJsonString(const std::string& json, const char* field);
        static bool ExtractJsonBool(const std::string& json, const char* field, bool fallback);

        CoreIntegrity _coreIntegrity;
        HANDLE _requestPipe = INVALID_HANDLE_VALUE;
        HANDLE _notifyPipe = INVALID_HANDLE_VALUE;
        long _nextSeq = 1;
        bool _communicationBlocked = false;
        bool _focusSyncRequired = true;
        std::string _clientSession;
        unsigned long long _nextEventId = 0;
        bool _requestFocusKnown = false;
        FocusSnapshot _requestFocus;
    };
}
