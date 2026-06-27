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
}

namespace TigerClawHookNative
{
    PipeClient::PipeClient() = default;

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
        (void)focus;
        return TrySendRequest(BuildKeyJson(keyEvent, state, caret), response, error);
    }

    bool PipeClient::TrySendFocus(const FocusSnapshot& focus, std::wstring& error)
    {
        return TrySendNotification(BuildFocusJson(focus), error);
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

        _requestPipe = CreateFileW(PipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
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

        _notifyPipe = CreateFileW(PipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
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
        if (_requestPipe != INVALID_HANDLE_VALUE)
        {
            CloseHandle(_requestPipe);
            _requestPipe = INVALID_HANDLE_VALUE;
        }
    }

    void PipeClient::DisconnectNotifyPipe()
    {
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
        if (!WriteFile(pipe, payload.data(), static_cast<DWORD>(payload.size()), &written, nullptr))
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
        const long long deadline = static_cast<unsigned long>(GetTickCount()) + timeoutMs;

        while (true)
        {
            DWORD available = 0;
            if (!PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr))
            {
                error = L"Pipe read failed: " + GetLastErrorMessage(GetLastError());
                return false;
            }

            if (available == 0)
            {
                if (static_cast<unsigned long>(GetTickCount()) >= deadline)
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
            if (!ReadFile(pipe, buffer, toRead, &read, nullptr))
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
               << ",\"vk\":" << keyEvent.VirtualKey
               << ",\"scan_code\":" << keyEvent.ScanCode
               << ",\"action\":\"" << (keyEvent.IsKeyDown ? "key_down" : "key_up") << "\""
               << ",\"shift\":" << (state.ShiftDown() ? "true" : "false")
               << ",\"ctrl\":" << (state.CtrlDown() ? "true" : "false")
               << ",\"alt\":" << (state.AltDown() ? "true" : "false")
               << ",\"win\":" << (state.WinDown() ? "true" : "false")
               << ",\"caps_lock\":" << (state.CapsLockOn() ? "true" : "false")
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
