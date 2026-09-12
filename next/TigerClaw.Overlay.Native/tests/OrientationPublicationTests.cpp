// Real production Application + renderer, with test-owned HWNDs, controlled time
// and publication-failure injection. No Core/TSF registration or live IPC.
#include "Renderer.h"
#include "Transport.h"
#include "Sound.h"
#include "Placement.h"
#include "MenuDismiss.h"
#include "FrameTransition.h"
#include <shellapi.h>
#include <shellscalingapi.h>
#include <windowsx.h>
#include <nlohmann/json.hpp>
#include <functional>
#include <iostream>
namespace probe {
HWND target = nullptr;
unsigned checks = 0, cases = 0, failures = 0, showFailures = 0;
ULONGLONG now = 1000;
std::vector<RECT> frames;
std::function<void()> duringPublish;
void Check(bool value, const char* why) { ++checks; if (!value) throw std::runtime_error(why); }
ULONGLONG WINAPI Tick() { return now; }
BOOL WINAPI Publish(HWND window, HDC targetDc, POINT* point, SIZE* size, HDC sourceDc,
                    POINT* origin, COLORREF key, BLENDFUNCTION* blend, DWORD flags) {
    if (window == target && failures) { --failures; SetLastError(ERROR_GEN_FAILURE); return FALSE; }
    auto result = UpdateLayeredWindow(window, targetDc, point, size, sourceDc, origin, key, blend, flags);
    if (result && window == target && point && size)
        frames.push_back({point->x, point->y, point->x+size->cx, point->y+size->cy});
    if (window == target && duringPublish) { auto fn = std::move(duringPublish); duringPublish = {}; fn(); }
    return result;
}
BOOL WINAPI Show(HWND window, HWND after, int x, int y, int w, int h, UINT flags) {
    if (window == target && (flags & SWP_SHOWWINDOW) && showFailures) {
        --showFailures; SetLastError(ERROR_GEN_FAILURE); return FALSE;
    }
    return SetWindowPos(window, after, x, y, w, h, flags);
}
}
#define UpdateLayeredWindow probe::Publish
#include "../Renderer.cpp"
#undef UpdateLayeredWindow
#define TIGERCLAW_ORIENTATION_PROBE
#define GetTickCount64 probe::Tick
#define SetWindowPos probe::Show
#define wWinMain orientationUnusedMain
#include "../main.cpp"
#undef wWinMain
#undef SetWindowPos
#undef GetTickCount64
namespace tiger::overlay {
struct OrientationPublicationProbe {
    Application app{true};
    RECT work{};
    OrientationPublicationProbe() {
        using namespace probe;
        now += 1000; failures = showFailures = 0; duringPublish = {}; frames.clear();
        DWORD style = WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TOOLWINDOW;
        app.candidate_ = CreateWindowExW(style, L"STATIC", L"orientation publication", WS_POPUP, 0,0,1,1,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);
        app.status_ = CreateWindowExW(style, L"STATIC", L"orientation status", WS_POPUP, 0,0,1,1,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);
        target = app.candidate_; Check(target && app.status_, "test windows missing");
        MONITORINFO monitor{sizeof(monitor)};
        Check(GetMonitorInfoW(MonitorFromPoint({0,0}, MONITOR_DEFAULTTOPRIMARY), &monitor) != FALSE, "monitor missing");
        work = monitor.rcWork;
        auto& s = app.state_;
        s.candidateVisible = s.isChinese = s.vertical = s.environmentActive = true;
        s.environmentRevision = 1; s.environmentId = u"test-core"; s.font = u"Segoe UI"; s.fontSize = 17;
        s.hideStatus = true; s.animationEnabled = false; s.caretHeight = 20;
        s.caretX = work.left + 100; s.caretY = work.top + 200; s.input = u"ab";
        s.candidates = {u"first"}; app.Refresh(true);
        s.caretY = work.bottom - app.candidateSize_.cy - 20;
        app.Refresh(false); Below();
    }
    ~OrientationPublicationProbe() { probe::duringPublish = {}; probe::failures = probe::showFailures = 0; probe::target = nullptr; }
    RECT Rect() { RECT r{}; probe::Check(GetWindowRect(app.candidate_, &r) != FALSE, "no candidate rectangle"); return r; }
    void Above() { probe::Check(IsWindowVisible(app.candidate_) && app.placement_.IsAbove() &&
        Rect().bottom == app.state_.caretY-app.state_.caretHeight-5, "candidate did not stay above"); }
    void Below() { probe::Check(IsWindowVisible(app.candidate_) && !app.placement_.IsAbove() &&
        Rect().top == app.state_.caretY+5, "candidate did not return below"); }
    void Large() { app.state_.candidates = {u"first",u"second",u"third",u"fourth",u"fifth"}; app.Refresh(false); }
    void Short() { app.state_.candidates = {u"first"}; app.Refresh(false); }
    void Commit() { app.state_.candidateVisible = false; app.Refresh(false); }
    static void Run() {
        using namespace probe;
        {
            OrientationPublicationProbe p; p.Large(); p.Above();
            for (int i=0;i<20;++i) {
                p.Commit(); Check(!IsWindowVisible(p.app.candidate_) && p.app.placement_.IsAbove(), "commit discarded direction");
                p.app.state_.candidateVisible = true; frames.clear(); p.Short(); p.Above();
                Check(frames.size()==1 && frames[0].bottom==p.app.state_.caretY-25, "first frame flashed below");
                Check(p.app.state_.caretY+5+p.app.candidateSize_.cy<=p.work.bottom-2, "short fixture cannot fit below");
            } ++cases;
        }
        {
            OrientationPublicationProbe p; p.Large(); p.Above(); p.Short(); p.Above();
            --p.app.state_.caretY; p.app.Refresh(false); p.Above();
            p.app.state_.caretY-=3; p.app.Refresh(false); p.Below(); ++cases;
        }
        {
            OrientationPublicationProbe p; p.Large(); p.Commit();
            ++p.app.state_.environmentRevision; p.app.state_.candidateVisible=true; p.Short(); p.Below();
            p.Large(); p.app.state_.environmentId=u"new-core"; p.Short(); p.Below(); ++cases;
        }
        {
            OrientationPublicationProbe p; p.Large(); p.Above();
            p.app.state_.environmentActive=false; p.app.Refresh(false);
            Check(!IsWindowVisible(p.app.candidate_) && !p.app.placement_.IsAbove(), "inactive environment retained display");
            p.app.state_.environmentActive=true; ++p.app.state_.environmentRevision; p.Short(); p.Below(); ++cases;
        }
        {
            OrientationPublicationProbe p; p.Large(); p.Above();
            auto x=p.app.state_.caretX, y=p.app.state_.caretY;
            p.app.state_.caretX=p.app.state_.caretY=0; p.app.Refresh(false);
            Check(!IsWindowVisible(p.app.candidate_) && p.app.placement_.IsAbove(), "invalid caret polluted direction");
            p.app.state_.caretX=x;p.app.state_.caretY=y;p.Short();p.Above(); ++cases;
        }
        for (bool failShow : {false,true}) {
            OrientationPublicationProbe p; p.Commit(); p.app.state_.candidateVisible=true;
            if (failShow) showFailures=1; else failures=1;
            p.Large(); Check(!IsWindowVisible(p.app.candidate_) && !p.app.placement_.IsAbove(), "failed appearance latched above");
            p.Short(); p.Below(); ++cases;
        }
        {
            OrientationPublicationProbe p; p.app.state_.animationEnabled=true;
            failures=1; p.Large(); Check(!p.app.placement_.IsAbove(), "failed animation start latched above");
            p.app.state_.animationEnabled=false; p.Short(); p.Below(); ++cases;
        }
        {
            OrientationPublicationProbe p;
            duringPublish=[&] { ++p.app.visualRevision_; p.app.placement_.Reset(); p.app.HideImmediately(); };
            p.Large(); Check(!p.app.placement_.IsAbove() && !IsWindowVisible(p.app.candidate_), "stale publication overwrote reset");
            p.Short();p.Below(); ++cases;
        }
        {
            OrientationPublicationProbe p; p.Large(); p.Above(); p.Commit();
            p.app.state_.showCode=true; p.app.state_.candidateDelay=200;
            p.app.state_.candidateVisible=true; p.Short();p.Above();
            now+=201;p.app.Refresh(false);p.Above(); ++cases;
        }
    }
};
}
int main() {
    try {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        tiger::overlay::CheckHr(CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED));
        tiger::overlay::OrientationPublicationProbe::Run(); CoUninitialize();
        std::cout << "{\"status\":\"passed\",\"probe\":\"native_orientation_publication\",\"cases\":" << probe::cases
                  << ",\"checks\":" << probe::checks << ",\"real_application_renderer\":true,\"controlled_clock\":true,\"live_ipc\":false}\n";
        return 0;
    } catch (const std::exception& e) { std::cerr << e.what() << '\n';return 1; }
}
