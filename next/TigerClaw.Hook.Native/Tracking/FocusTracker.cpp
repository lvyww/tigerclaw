#include "FocusTracker.h"

#include <windows.h>
#include <psapi.h>

#include <unordered_map>
#include <string>
#include <vector>

namespace TigerClawHookNative
{
    namespace
    {
        std::unordered_map<DWORD, std::wstring> ProcessNameCache;

        std::wstring GetProcessName(DWORD processId)
        {
            if (processId == 0)
            {
                return std::wstring();
            }

            auto it = ProcessNameCache.find(processId);
            if (it != ProcessNameCache.end())
            {
                return it->second;
            }

            std::wstring processName;
            HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, FALSE, processId);
            if (process != nullptr)
            {
                std::vector<wchar_t> buffer(MAX_PATH, L'\0');
                if (GetModuleBaseNameW(process, nullptr, buffer.data(), static_cast<DWORD>(buffer.size())) > 0)
                {
                    processName = buffer.data();
                }

                CloseHandle(process);
            }

            ProcessNameCache.emplace(processId, processName);
            return processName;
        }
    }

    bool FocusTracker::TryGetSnapshot(FocusSnapshot& focus) const
    {
        HWND hwnd = GetForegroundWindow();
        if (hwnd == nullptr)
        {
            return false;
        }

        DWORD processId = 0;
        GetWindowThreadProcessId(hwnd, &processId);

        std::vector<wchar_t> classBuffer(256, L'\0');
        GetClassNameW(hwnd, classBuffer.data(), static_cast<int>(classBuffer.size()));

        std::vector<wchar_t> titleBuffer(512, L'\0');
        GetWindowTextW(hwnd, titleBuffer.data(), static_cast<int>(titleBuffer.size()));

        focus.Window = hwnd;
        focus.ProcessId = processId;
        focus.ProcessName = GetProcessName(processId);
        focus.ClassName = classBuffer.data();
        focus.WindowTitle = titleBuffer.data();
        return true;
    }
}
