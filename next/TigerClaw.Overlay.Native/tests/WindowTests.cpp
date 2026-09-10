#include "WinResources.h"
#include <nlohmann/json.hpp>
#include <cstring>
#include <cmath>
#include <iostream>

using namespace tiger::overlay;
static void Check(bool condition, const char* reason)
{
    if (!condition) throw std::runtime_error(reason);
}
template<class Condition> static bool Until(Condition condition, DWORD timeout = 5000)
{
    auto deadline = GetTickCount64() + timeout;
    do { if (condition()) return true; Sleep(15); } while (GetTickCount64() < deadline);
    return condition();
}
static HWND Find(DWORD pid, const wchar_t* type)
{
    struct Search { DWORD pid; const wchar_t* type; HWND result; } search{pid, type, nullptr};
    EnumWindows([](HWND window, LPARAM argument) -> BOOL
    {
        auto& value = *reinterpret_cast<Search*>(argument);
        DWORD owner = 0; GetWindowThreadProcessId(window, &owner);
        wchar_t name[128]{}; GetClassNameW(window, name, 128);
        if (owner == value.pid && !wcscmp(name, value.type)) { value.result = window; return FALSE; }
        return TRUE;
    }, reinterpret_cast<LPARAM>(&search));
    return search.result;
}
class Child
{
    Handle process_;
    DWORD pid_ = 0;
public:
    Child(const std::wstring& exe, const std::wstring& session)
    {
        std::wstring command = L"\"" + exe + L"\" --test-session " + session;
        STARTUPINFOW startup{}; startup.cb = sizeof(startup);
        PROCESS_INFORMATION info{};
        Check(CreateProcessW(exe.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &startup, &info) != FALSE,
            "isolated GUI process launch");
        process_.Reset(info.hProcess); CloseHandle(info.hThread); pid_ = info.dwProcessId;
    }
    ~Child()
    {
        if (WaitForSingleObject(process_.Get(), 0) == WAIT_TIMEOUT)
        {
            auto window = Find(pid_, L"TigerClaw.Native.Candidate.v1");
            if (window) PostMessageW(window, WM_CLOSE, 0, 0);
            if (WaitForSingleObject(process_.Get(), 3000) == WAIT_TIMEOUT)
            {
                // Only the exact child created by this test, never a name-based kill.
                TerminateProcess(process_.Get(), 99); WaitForSingleObject(process_.Get(), 3000);
            }
        }
    }
    DWORD Pid() const { return pid_; }
    bool Exited() const { return WaitForSingleObject(process_.Get(), 0) == WAIT_OBJECT_0; }
    DWORD ExitCode() const { DWORD code = 0; GetExitCodeProcess(process_.Get(), &code); return code; }
};
int main()
{
    try
    {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        std::wstring executable(32768, L'\0');
        executable.resize(GetModuleFileNameW(nullptr, executable.data(), static_cast<DWORD>(executable.size())));
        executable = executable.substr(0, executable.find_last_of(L"\\/")) + L"\\TigerClaw.Overlay.Native.Test.exe";
        std::wstring session = std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64());
        auto root = L"Local\\TigerClaw.Overlay.Test." + session;
        Mapping ui, core, overlay, snapshot;
        Check(ui.Open((root + L".Ui").c_str(), 128 * 1024, true), "isolated UI mapping");
        Check(core.Open((root + L".Core").c_str(), 16, true), "isolated heartbeat mapping");
        auto snapshotName = root + L".Ui.Snapshot.v2";
        Check(snapshot.Open(snapshotName.c_str(), 128 * 1024, true), "isolated synchronized snapshot");
        Handle gate(CreateMutexW(nullptr, FALSE, (snapshotName + L".Lock").c_str()));
        Handle changed(CreateEventW(nullptr, FALSE, FALSE, (snapshotName + L".Changed").c_str()));
        auto heartbeat = [&]
        {
            auto tick = GetTickCount64(); std::memcpy(static_cast<char*>(core.Data()) + 8, &tick, 8);
        };
        heartbeat();
        std::int64_t sequence = 0;
        auto publish = [&](const nlohmann::json& state)
        {
            heartbeat();
            auto payload = state.dump(); int length = static_cast<int>(payload.size());
            auto tick = GetTickCount64(); ++sequence;
            std::memcpy(ui.Data(), &sequence, 8); std::memcpy(static_cast<char*>(ui.Data()) + 8, &tick, 8);
            std::memcpy(static_cast<char*>(ui.Data()) + 16, &length, 4);
            std::memcpy(static_cast<char*>(ui.Data()) + 20, payload.data(), payload.size()); MemoryBarrier();
            Check(WaitForSingleObject(gate.Get(), 1000) == WAIT_OBJECT_0, "snapshot writer lock");
            std::int64_t zero = 0;
            std::memcpy(snapshot.Data(), &zero, 8);
            std::memcpy(static_cast<char*>(snapshot.Data()) + 8, &tick, 8);
            std::memcpy(static_cast<char*>(snapshot.Data()) + 16, &length, 4);
            std::memcpy(static_cast<char*>(snapshot.Data()) + 20, payload.data(), payload.size());
            MemoryBarrier(); std::memcpy(snapshot.Data(), &sequence, 8);
            ReleaseMutex(gate.Get()); SetEvent(changed.Get());
        };
        auto foreground = GetForegroundWindow();
        Child child(executable, session);
        HWND candidate = nullptr, status = nullptr;
        Check(Until([&] { candidate = Find(child.Pid(), L"TigerClaw.Native.Candidate.v1");
            status = Find(child.Pid(), L"TigerClaw.Native.Status.v1"); return candidate && status; }), "both real windows created");
        Check(!child.Exited(), "GUI stays alive before state");
        Check(Until([&] {
            if (!IsWindowVisible(status)) return false;
            MONITORINFO monitor{}; monitor.cbSize = sizeof(monitor);
            RECT rect{}; GetWindowRect(status, &rect);
            if (!GetMonitorInfoW(MonitorFromWindow(status, MONITOR_DEFAULTTONEAREST), &monitor)) return false;
            return rect.left >= monitor.rcWork.left && rect.top >= monitor.rcWork.top &&
                rect.right <= monitor.rcWork.right - 2 && rect.bottom <= monitor.rcWork.bottom - 2;
        }), "scaled startup status stays inside monitor work area");
        RECT area{}; SystemParametersInfoW(SPI_GETWORKAREA, 0, &area, 0);
        int x = area.left + 100, y = area.top + 150;
        nlohmann::json state{{"CandidateVisible", true}, {"IsChinese", true}, {"InputCode", "ab"},
            {"Candidates", {"one", "two", "three"}}, {"ShowInputCodeInCandidateWindow", true}, {"FontSize", 20},
            {"CaretX", 0}, {"CaretY", 0}, {"CaretHeight", 20}, {"HideStatusBar", true}};
        publish(state);
        Check(Until([&] { return !IsWindowVisible(status); }), "status hidden by IPC");
        Check(!IsWindowVisible(candidate), "invalid initial caret stays hidden");
        state["CaretX"] = x; state["CaretY"] = y; publish(state);
        RECT first{};
        Check(Until([&] { GetWindowRect(candidate, &first); return IsWindowVisible(candidate) && first.right - first.left > 20; }),
            "valid caret after same-text state renders");
        Check(first.left == x && first.top == y + 5, "IPC caret placement");
        state["CaretX"] = x + 100; state["InputCode"] = "abcdefgh"; publish(state);
        RECT longer{};
        Check(Until([&] { GetWindowRect(candidate, &longer); return longer.right - longer.left > first.right - first.left; }),
            "changed raw text redraws wider window");
        Check(longer.left == first.left, "live caret does not move pinned anchor");
        state["CandidateAnchorRevision"] = 1; publish(state);
        Check(Until([&] { RECT rect{}; GetWindowRect(candidate, &rect); return rect.left == x + 100; }), "explicit anchor revision moves window");
        state["CandidateVisible"] = false; publish(state);
        Check(Until([&] { return !IsWindowVisible(candidate); }), "composition end hides window");
        state["CandidateVisible"] = true; state["VerticalCandidates"] = true;
        state["CaretX"] = x; state["CaretY"] = area.bottom - 10; publish(state);
        Check(Until([&] { RECT rect{}; GetWindowRect(candidate, &rect);
            return IsWindowVisible(candidate) && rect.bottom <= area.bottom - 30; }), "bottom-edge vertical window flips above caret");
        state["CaretX"] = 0; state["CaretY"] = 0; publish(state);
        Check(Until([&] { return !IsWindowVisible(candidate); }), "lost caret hides window");
        state["CaretX"] = x; state["CaretY"] = area.bottom - 10; publish(state);
        Check(Until([&] { return IsWindowVisible(candidate); }), "restored caret redraws window");
        state["CandidateVisible"] = false; publish(state);
        Check(Until([&] { return !IsWindowVisible(candidate); }), "reset reveal session");
        state["CandidateVisible"] = true; state["ShowInputCodeInCandidateWindow"] = false;
        state["VerticalCandidates"] = false; state["CaretY"] = y;
        state["CandidateExpandDelayMs"] = 300; state["AnnotationExpandDelayMs"] = 650;
        state["CandidateAnnotations"] = {"long annotation for delayed reveal", "", ""};
        auto revealStart = GetTickCount64(); publish(state);
        Sleep(100);
        Check(!IsWindowVisible(candidate), "candidate delay initially hides window without code");
        Check(Until([&] { return IsWindowVisible(candidate); }, 1000), "candidate timer reveals window");
        Check(GetTickCount64() - revealStart >= 250, "candidate reveal honors configured delay");
        RECT beforeAnnotation{}; GetWindowRect(candidate, &beforeAnnotation);
        Check(Until([&] { RECT rect{}; GetWindowRect(candidate, &rect);
            return rect.right - rect.left > beforeAnnotation.right - beforeAnnotation.left; }, 1500),
            "annotation timer updates real window layout");
        Check(GetForegroundWindow() == foreground, "IPC updates never activate windows");
        auto savedState = state;
        for (bool vertical : {false, true})
        {
            for (int composition : {2, 3, 4})
            {
                state = savedState;
                state["CandidateExpandDelayMs"] = 0; state["AnnotationExpandDelayMs"] = 0;
                state["VerticalCandidates"] = vertical; state["CompositionState"] = composition;
                publish(state); Sleep(260);
                RECT full{}; GetWindowRect(candidate, &full);
                auto start = GetTickCount64();
                state["CandidateVisible"] = false;
                state["CandidateBackgroundUntil"] = start + 2000; // Legacy deadline is ignored.
                publish(state);
                Check(Until([&] { return GetWindowLongPtrW(candidate, GWL_EXSTYLE) & WS_EX_TRANSPARENT; }, 150),
                    "hide animation starts in every mode");
                Sleep(70);
                RECT shrinking{}; GetWindowRect(candidate, &shrinking);
                Check(IsWindowVisible(candidate), "hide retains pixels during 200 ms animation");
                Check(vertical ?
                    shrinking.right - shrinking.left == full.right - full.left && shrinking.bottom - shrinking.top < full.bottom - full.top :
                    shrinking.bottom - shrinking.top == full.bottom - full.top && shrinking.right - shrinking.left < full.right - full.left,
                    "hide shrinks only the layout axis");
                // A repeated hidden update must not reset the deadline.
                publish(state);
                Check(Until([&] { return !IsWindowVisible(candidate); }, 500), "hide completes without square dwell");
                Check(GetTickCount64() - start >= 190 && GetTickCount64() - start < 650, "hide uses 200 ms deadline");
                state["CandidateVisible"] = true; publish(state); Sleep(100);
                state["CandidateVisible"] = false; publish(state); Sleep(70);
                state["CandidateVisible"] = true; publish(state);
                Check(Until([&] { RECT rect{}; GetWindowRect(candidate, &rect);
                    return IsWindowVisible(candidate) && !(GetWindowLongPtrW(candidate, GWL_EXSTYLE) & WS_EX_TRANSPARENT) &&
                        rect.right - rect.left == full.right - full.left && rect.bottom - rect.top == full.bottom - full.top;
                }, 300), "new input interrupts hide and expands to full size");
                Sleep(160);
                Check(IsWindowVisible(candidate), "old hide timer cannot hide new candidates");
            }
        }
        state = savedState; state["CandidateExpandDelayMs"] = 0; state["AnnotationExpandDelayMs"] = 0;
        publish(state); Sleep(120);
        state["CandidateAnimationEnabled"] = false;
        publish(state); Sleep(100);
        state["CandidateVisible"] = false; publish(state);
        Check(Until([&] { return !IsWindowVisible(candidate); }, 150), "disabled animation hides immediately");
        state["CandidateVisible"] = true; publish(state); Sleep(100);
        state["CandidateAnimationEnabled"] = true;
        state["CandidateAnimationHideMs"] = 400;
        state["CandidateVisible"] = false; publish(state); Sleep(250);
        Check(IsWindowVisible(candidate), "custom hide duration stays visible beyond default");
        Check(Until([&] { return !IsWindowVisible(candidate); }, 400), "custom hide completes");
        state["CandidateAnimationHideMs"] = 200;
        state["CandidateVisible"] = true; publish(state); Sleep(100);
        // Exercise motion plus resizing, then cancel midway. Late animation
        // timers must never resurrect a committed/hidden composition.
        state["CandidateExpandDelayMs"] = 0; state["AnnotationExpandDelayMs"] = 0;
        state["CandidateAnchorRevision"] = 2; state["CaretX"] = x + 240;
        state["InputCode"] = "abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyz";
        publish(state);
        Check(Until([&] { RECT rect{}; GetWindowRect(candidate, &rect);
            return rect.left == x + 240; }), "animated movement reaches latest anchor");
        state["CandidateAnchorRevision"] = 3; state["CaretX"] = x;
        state["InputCode"] = "ab"; publish(state);
        Sleep(5);
        state["CandidateVisible"] = false; publish(state);
        Check(Until([&] { return !IsWindowVisible(candidate); }), "hide interrupts transition immediately");
        Sleep(180);
        Check(!IsWindowVisible(candidate), "expired transition never resurrects hidden candidate");
        // No pipe server exists in this isolated session. The worker spends up
        // to three seconds reconnecting, but the menu must appear immediately.
        Handle menuEvent(CreateEventW(nullptr, FALSE, FALSE, (root + L".Menu").c_str()));
        Check(menuEvent.Get() != nullptr, "isolated menu event created");
        heartbeat();
        auto menuStart = GetTickCount64();
        Check(SetEvent(menuEvent.Get()) != FALSE, "signal menu without Core pipe");
        HWND menuHost = nullptr;
        Check(Until([&] {
            menuHost = Find(child.Pid(), L"TigerClaw.Native.MenuHost.v1");
            return menuHost && IsWindowVisible(menuHost) && Find(child.Pid(), L"#32768");
        }, 800), "menu opens without waiting for schema request timeout");
        Check(GetTickCount64() - menuStart < 800, "offline menu latency below reconnect timeout");
        state["CandidateVisible"] = false; state["HideStatusBar"] = true; publish(state);
        Check(Until([&] { return !IsWindowVisible(candidate) && !IsWindowVisible(status); }), "UI can hide during menu");
        Check(Find(child.Pid(), L"#32768") != nullptr, "menu survives display-state hide");
        PostMessageW(menuHost, WM_CANCELMODE, 0, 0);
        Check(Until([&] { return !Find(child.Pid(), L"#32768") && !IsWindowVisible(menuHost); }), "menu host cleans up");
        Check(Until([&] { return overlay.Open((root + L".Overlay").c_str(), 16); }), "isolated overlay heartbeat published");
        Check(GetTickCount64() > 11000, "heartbeat stale test requires 11 seconds OS uptime");
        auto stale = GetTickCount64() - 10001;
        std::memcpy(static_cast<char*>(core.Data()) + 8, &stale, 8);
        Check(Until([&] { return child.Exited(); }), "stale Core exits entire GUI process");
        Check(child.ExitCode() == 0, "clean lifecycle exit code");
        std::cout << "Isolated real-window IPC tests passed\n";
        return 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
