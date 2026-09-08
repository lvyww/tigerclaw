#include <windows.h>
#include <iostream>
#include <stdexcept>
#include <string>

static void Check(bool ok, const char* message)
{
    if (!ok) throw std::runtime_error(message);
}
template<class F> static bool Until(F condition)
{
    auto end = GetTickCount64() + 5000;
    do
    {
        MSG msg{};
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
        { TranslateMessage(&msg); DispatchMessageW(&msg); }
        if (condition()) return true;
        Sleep(10);
    } while (GetTickCount64() < end);
    return false;
}
static HWND Find(DWORD pid, const wchar_t* name)
{
    struct Search { DWORD pid; const wchar_t* name; HWND found; } search{pid, name, nullptr};
    EnumWindows([](HWND hwnd, LPARAM data) -> BOOL
    {
        auto& s = *reinterpret_cast<Search*>(data);
        DWORD pid = 0; GetWindowThreadProcessId(hwnd, &pid);
        wchar_t name[128]{}; GetClassNameW(hwnd, name, 128);
        if (pid == s.pid && !wcscmp(name, s.name) && IsWindowVisible(hwnd))
        { s.found = hwnd; return FALSE; }
        return TRUE;
    }, reinterpret_cast<LPARAM>(&search));
    return search.found;
}
static void ClickOwned(HWND window)
{
    POINT point{40, 60}; ClientToScreen(window, &point);
    SetCursorPos(point.x, point.y);
    Check(GetAncestor(WindowFromPoint(point), GA_ROOT) == window, "click stays inside owned test window");
    INPUT clicks[2]{};
    clicks[0].type = clicks[1].type = INPUT_MOUSE;
    clicks[0].mi.dwFlags = MOUSEEVENTF_LEFTDOWN; clicks[1].mi.dwFlags = MOUSEEVENTF_LEFTUP;
    Check(SendInput(2, clicks, sizeof(INPUT)) == 2, "test click injected");
}
int main()
{
    PROCESS_INFORMATION child{};
    HWND original = GetForegroundWindow(), first = nullptr, second = nullptr;
    POINT cursor{}; GetCursorPos(&cursor);
    int result = 1;
    try
    {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        first = CreateWindowExW(WS_EX_TOPMOST, L"STATIC", L"Overlay menu test: original target",
            WS_OVERLAPPEDWINDOW | WS_VISIBLE, 80, 80, 280, 180, nullptr, nullptr, nullptr, nullptr);
        second = CreateWindowExW(WS_EX_TOPMOST, L"STATIC", L"Overlay menu test: outside-click target",
            WS_OVERLAPPEDWINDOW | WS_VISIBLE, 400, 80, 280, 180, nullptr, nullptr, nullptr, nullptr);
        Check(first && second, "test windows created");
        ClickOwned(first);
        Check(Until([&] { return GetForegroundWindow() == first; }), "test foreground permission");
        wchar_t module[32768]{}; GetModuleFileNameW(nullptr, module, 32768);
        std::wstring exe(module);
        exe = exe.substr(0, exe.find_last_of(L"\\/")) + L"\\TigerClaw.Overlay.Native.Preview.exe";
        std::wstring command = L"\"" + exe + L"\" --demo";
        STARTUPINFOW startup{}; startup.cb = sizeof(startup);
        Check(CreateProcessW(exe.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &startup, &child), "preview launch");
        CloseHandle(child.hThread); child.hThread = nullptr;
        HWND status = nullptr;
        Check(Until([&] { status = Find(child.dwProcessId, L"TigerClaw.Native.Status.v1"); return status != nullptr; }), "preview status visible");
        auto open = [&]
        {
            Check(AllowSetForegroundWindow(child.dwProcessId), "allow explicit test menu activation");
            RECT rect{}; GetWindowRect(status, &rect);
            SetCursorPos(rect.left + 5, rect.top + 5);
            PostMessageW(status, WM_RBUTTONUP, 0, 0);
            Check(Until([&] { return Find(child.dwProcessId, L"#32768") != nullptr; }), "menu opens");
        };
        open();
        // Click only the client area of our own visible topmost test window.
        ClickOwned(second);
        Check(Until([&] { return !Find(child.dwProcessId, L"#32768"); }), "outside click closes menu");
        Check(Until([&] { return GetForegroundWindow() == second; }), "outside target retains focus");
        open();
        PostMessageW(status, WM_CANCELMODE, 0, 0);
        Check(Until([&] { return !Find(child.dwProcessId, L"#32768") && GetForegroundWindow() == second; }),
            "reopened menu cancels and restores prior target");
        std::cout << "Isolated menu outside-click and reopen tests passed\n";
        result = 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; }
    if (child.hProcess)
    {
        // This handle belongs only to the synthetic preview created above.
        TerminateProcess(child.hProcess, 0); WaitForSingleObject(child.hProcess, 3000); CloseHandle(child.hProcess);
    }
    if (second) DestroyWindow(second);
    if (first) DestroyWindow(first);
    SetCursorPos(cursor.x, cursor.y);
    if (IsWindow(original)) SetForegroundWindow(original);
    return result;
}
