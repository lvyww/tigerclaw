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
#include <algorithm>
#include <memory>
#include <sstream>

namespace tiger::overlay
{
    static constexpr const wchar_t* CandidateClass = L"TigerClaw.Native.Candidate.v1";
    static constexpr const wchar_t* StatusClass = L"TigerClaw.Native.Status.v1";
    static constexpr const wchar_t* MenuClass = L"TigerClaw.Native.MenuHost.v1";
    class Application
    {
#ifdef TIGERCLAW_ORIENTATION_PROBE
        friend struct OrientationPublicationProbe;
#endif
        std::uint64_t visualRevision_ = 0;
        bool refreshing_ = false, refreshQueued_ = false;
        static constexpr UINT DeferredGeometryMessage = WM_APP + 0x351;
        HWND candidate_ = nullptr, status_ = nullptr, menuHost_ = nullptr;
        bool statusPlaced_ = false;
        State state_;
        Endpoints endpoints_;
        bool isolated_ = false;
        Reveal reveal_;
        Display display_;
        std::wstring directory_;
        Renderer candidateRenderer_, statusRenderer_, transitionRenderer_;
        FrameTransition transition_;
        bool transitionsEnabled_ = true;
        void HideImmediately()
        {
            StopTransition();
            ShowWindow(candidate_, SW_HIDE);
        }
        unsigned RefreshRate(POINT point)
        {
            MONITORINFOEXW monitor{}; monitor.cbSize = sizeof(monitor);
            DEVMODEW mode{}; mode.dmSize = sizeof(mode);
            if (GetMonitorInfoW(MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST), &monitor) &&
                EnumDisplaySettingsW(monitor.szDevice, ENUM_CURRENT_SETTINGS, &mode) && mode.dmDisplayFrequency > 1)
                return mode.dmDisplayFrequency;
            return 60;
        }

        void StopTransition() { transition_.Cancel(); KillTimer(candidate_, 4); }
        void TransitionTick()
        {
            if (!transition_.Active()) return;
            auto rect = transition_.Sample(GetTickCount64());
            POINT point{rect.x, rect.y}; SIZE size{std::max(1, rect.width), std::max(1, rect.height)};
            try
            {
                if (transition_.Active())
                {
                    transitionRenderer_.Prepare(state_, display_, candidateDpi_, false, &size);
                    transitionRenderer_.Present(candidate_, &point);
                }
                else
                {
                    KillTimer(candidate_, 4);
                    candidateRenderer_.Present(candidate_, &point);
                }
            }
            catch (...)
            {
                StopTransition();
                candidateDrawn_ = false;
                SetTimer(candidate_, 3, 100, nullptr);
            }
        }
        void PublishCandidate(SIZE size, POINT destination)
        {
            FrameRect target{destination.x, destination.y, size.cx, size.cy};
            RECT current{}; GetWindowRect(candidate_, &current);
            FrameRect from{current.left, current.top, current.right - current.left, current.bottom - current.top};
            bool appearing = !IsWindowVisible(candidate_);
            if (!appearing && transitionsEnabled_ && state_.animationEnabled && from != target)
            {
                if (!transition_.Active() || transition_.Target() != target ||
                    transition_.Duration() != static_cast<unsigned>(state_.animationDurationMs))
                {
                    transition_.Start(from, target, GetTickCount64(), RefreshRate(destination),
                        state_.animationDurationMs);
                    if (!transition_.Active())
                    { StopTransition(); candidateRenderer_.Present(candidate_, &destination); return; }

                    if (!SetTimer(candidate_, 4, transition_.Interval(), nullptr))
                    { StopTransition(); candidateRenderer_.Present(candidate_, &destination); }
                }
                if (transition_.Active()) {
                    auto frame = transition_.Sample(GetTickCount64());
                    POINT at{frame.x, frame.y}; SIZE frameSize{std::max(1, frame.width), std::max(1, frame.height)};
                    transitionRenderer_.Prepare(state_, display_, candidateDpi_, false, &frameSize);
                    transitionRenderer_.Present(candidate_, &at);
                }
                return;
            }
            StopTransition();
            candidateRenderer_.Present(candidate_, &destination);
        }
        Sound sound_;
        std::unique_ptr<StateSource> source_;
        std::unique_ptr<CommandQueue> commands_;
        std::int64_t soundSeq_ = 0, anchorRevision_ = 0;
        bool demo_, validCaret_ = false, candidateDrawn_ = false;
        Placement placement_;
        bool dragging_ = false, horizontalCode_ = false, verticalCode_ = false;
        POINT dragPoint_{}, dragOrigin_{};
        UINT candidateDpi_ = 96;
        SIZE candidateSize_{};
        unsigned candidateRetryCount_ = 0;
        bool statusDrawn_ = false;
        State renderedState_;
        std::vector<Text> schemas_;
        Text currentSchema_;
        bool schemaPending_ = false, menuOpen_ = false, layoutPending_ = false;
        HMENU activeSchemaMenu_ = nullptr;
        std::vector<Text> openMenuSchemas_;
        POINT menuRequestPoint_{};
        MenuDismiss menuDismiss_;
        static bool KeyDown(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
        void CheckMenuDismiss()
        {
            if (!menuOpen_) return;
            struct Hit { POINT point{}; bool inside = false; } hit;
            if (!GetCursorPos(&hit.point)) return;
            // Include all visible submenu popups, but only on our UI thread.
            EnumThreadWindows(GetCurrentThreadId(), [](HWND window, LPARAM data) -> BOOL
            {
                auto& hit = *reinterpret_cast<Hit*>(data);
                wchar_t name[32]{}; RECT rect{};
                if (IsWindowVisible(window) && GetClassNameW(window, name, 32) &&
                    !wcscmp(name, L"#32768") && GetWindowRect(window, &rect) && PtInRect(&rect, hit.point))
                { hit.inside = true; return FALSE; }
                return TRUE;
            }, reinterpret_cast<LPARAM>(&hit));
            if (menuDismiss_.Update(hit.inside, KeyDown(VK_LBUTTON), KeyDown(VK_RBUTTON), KeyDown(VK_ESCAPE)))
                EndMenu();
        }
        static std::wstring Directory()
        {
            std::wstring path(32768, L'\0');
            DWORD length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
            path.resize(length);
            return path.substr(0, path.find_last_of(L"\\/"));
        }
        UINT DpiAt(POINT point)
        {
            UINT x = 96, y = 96;
            GetDpiForMonitor(MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST), MDT_EFFECTIVE_DPI, &x, &y);
            return x;
        }
        static WorkArea Area(const RECT& r) { return {r.left, r.top, r.right, r.bottom}; }
        bool CapturePlacementEnvironment(PlacementEnvironment& env)
        {
            auto monitor = MonitorFromPoint({state_.caretX, state_.caretY}, MONITOR_DEFAULTTONEAREST);
            MONITORINFO info{sizeof(info)};
            if (!monitor || !GetMonitorInfoW(monitor, &info)) return false;
            env.work = Area(info.rcWork); env.monitor = reinterpret_cast<std::uintptr_t>(monitor);
            env.revision = state_.environmentRevision; env.instance = state_.environmentId;
            env.owner = static_cast<std::uintptr_t>(state_.ownerHwnd); env.processId = state_.ownerProcessId;
            UINT dpiY = 96; env.dpi = 96;
            if (FAILED(GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &env.dpi, &dpiY))) env.uncertain = true;
            HWND foreground = GetForegroundWindow();
            env.foreground = reinterpret_cast<std::uintptr_t>(foreground);
            RECT bounds{};
            if (foreground && GetWindowRect(foreground, &bounds)) env.foregroundBounds = Area(bounds);
            // Legacy snapshots have no owner identity; use the actual foreground
            // environment, never the candidate HWND. New Core also fences missed
            // off/on or same-HWND document transitions with an epoch + instance ID.
            if (env.owner) {
                auto owner = reinterpret_cast<HWND>(env.owner);
                DWORD ownerPid = 0, foregroundPid = 0;
                GetWindowThreadProcessId(owner, &ownerPid);
                auto thread = GetWindowThreadProcessId(foreground, &foregroundPid);
                if (!ownerPid || (env.processId && env.processId != ownerPid) ||
                    (foreground && foregroundPid != ownerPid)) return false;
                if (GetWindowRect(owner, &bounds)) env.ownerBounds = Area(bounds);
                else env.uncertain = true;
                GUITHREADINFO gui{sizeof(gui)};
                if (thread && GetGUIThreadInfo(thread, &gui)) env.focus = reinterpret_cast<std::uintptr_t>(gui.hwndFocus);
                else env.uncertain = true;
            }
            wchar_t className[128]{}, title[128]{};
            GetClassNameW(foreground, className, 128); GetWindowTextW(foreground, title, 128);
            env.startMenu = !wcscmp(className, L"Windows.UI.Core.CoreWindow") && !wcscmp(title, L"\u641c\u7d22");
            return true;
        }
        bool ResolvePosition(Placement& placement, SIZE size, POINT& result, bool& environmentChanged)
        {
            if (!validCaret_) return false;
            PlacementEnvironment env;
            if (!CapturePlacementEnvironment(env)) return false;
            environmentChanged = !placement.SameEnvironment(env);
            if (!placement.Acquire(state_.caretX, state_.caretY, state_.caretHeight)) return false;
            auto position = placement.Resolve(size.cx, size.cy, env);
            result = {position.x, position.y};
            return true;
        }
        void Position()
        {
            const auto revision = visualRevision_;
            auto nextPlacement = placement_;
            POINT position{}; bool environmentChanged = false;
            if (!ResolvePosition(nextPlacement, candidateSize_, position, environmentChanged)) { HideImmediately(); return; }
            if (environmentChanged) StopTransition();
            RECT current{}; GetWindowRect(candidate_, &current);
            if (transition_.Active() || current.left != position.x || current.top != position.y || !IsWindowVisible(candidate_)) {
                // Do not interpolate from a different monitor/window environment.
                if (environmentChanged) candidateRenderer_.Present(candidate_, &position);
                else PublishCandidate(candidateSize_, position);
                if (revision != visualRevision_) return;
                if (!IsWindowVisible(candidate_) && !SetWindowPos(candidate_, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW))
                    throw std::runtime_error("Candidate show failed");
            }
            if (revision == visualRevision_ && IsWindowVisible(candidate_)) placement_ = nextPlacement;
        }
        void Refresh(bool force)
        {
            if (!candidate_ || !status_) return;
            const auto revision = ++visualRevision_;
            // Publishing can synchronously deliver DPI/layout messages. Do not
            // reenter the renderer; invalidate this frame and coalesce a refresh.
            if (refreshing_) {
                if (!refreshQueued_) refreshQueued_ = PostMessageW(candidate_, DeferredGeometryMessage, 0, 0) != FALSE;
                return;
            }
            struct Guard { bool& flag; explicit Guard(bool& f) : flag(f) { flag = true; } ~Guard() { flag = false; } } guard(refreshing_);
            const bool environmentReset = state_.environmentRevision != renderedState_.environmentRevision ||
                state_.environmentId != renderedState_.environmentId || state_.ownerHwnd != renderedState_.ownerHwnd ||
                state_.ownerProcessId != renderedState_.ownerProcessId || state_.nativeHook != renderedState_.nativeHook ||
                state_.isChinese != renderedState_.isChinese || state_.isOff != renderedState_.isOff ||
                state_.environmentActive != renderedState_.environmentActive;
            if (environmentReset) { placement_.Reset(); StopTransition(); }
            auto now = GetTickCount64();
            bool reformat = force || !SameDisplayContent(state_, renderedState_) || reveal_.NextDelay(state_, now) != 0;
            Display formatted;
            if (reformat) formatted = reveal_.Update(state_, now);
            const auto& next = reformat ? formatted : display_;
            if (ModeFor(state_) == DisplayMode::Hidden)
            {
                placement_.EndComposition();
            }
            if (anchorRevision_ != state_.anchorRevision)
            {
                anchorRevision_ = state_.anchorRevision;
                placement_.RefreshAnchor();
            }
            validCaret_ = next.mode != DisplayMode::Hidden && placement_.Acquire(state_.caretX, state_.caretY, state_.caretHeight);
            UINT dpi = validCaret_ ? DpiAt({state_.caretX, state_.caretY}) : 96;
            bool changed = force || !candidateDrawn_ || state_.animationEnabled != renderedState_.animationEnabled ||
                state_.animationDurationMs != renderedState_.animationDurationMs || state_.theme != renderedState_.theme || state_.font != renderedState_.font ||
                state_.fontSize != renderedState_.fontSize || state_.vertical != renderedState_.vertical ||
                next.mode != display_.mode || next.text != display_.text ||
                next.selectionStart != display_.selectionStart || next.selectionLength != display_.selectionLength || dpi != candidateDpi_;
            if (reformat) display_ = std::move(formatted);
            if (validCaret_) candidateDpi_ = dpi;
            if (display_.mode == DisplayMode::Hidden || !validCaret_)
            {
                HideImmediately();
                candidateDrawn_ = false;
                KillTimer(candidate_, 3);
                candidateRetryCount_ = 0;
            }
            else
            {
                if (changed)
                {
                    candidateDrawn_ = false;
                    try
                    {
                        auto size = candidateRenderer_.Prepare(state_, display_, dpi);
                        auto placement = placement_;
                        POINT destination{}; bool environmentChanged = false;
                        if (!ResolvePosition(placement, size, destination, environmentChanged)) {
                            HideImmediately(); return;
                        }
                        // Publish pixels, dimensions and final location together.
                        // The OS retains the previous frame throughout Prepare.
                        if (environmentChanged) { StopTransition(); candidateRenderer_.Present(candidate_, &destination); }
                        else PublishCandidate(size, destination);
                        if (revision != visualRevision_) return;
                        if (!IsWindowVisible(candidate_) && !SetWindowPos(candidate_, HWND_TOPMOST, 0, 0, 0, 0,
                            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW))
                            throw std::runtime_error("Candidate show failed");
                        if (revision != visualRevision_ || !IsWindowVisible(candidate_)) return;
                        candidateSize_ = size;
                        placement_ = placement;
                        candidateDrawn_ = true;
                        candidateRetryCount_ = 0;
                        KillTimer(candidate_, 3);
                    }
                    catch (...)
                    {
                        StopTransition();
                        // Keep the last published frame, retry without routing
                        // this recoverable failure to Procedure's hide fallback.
                        if (candidateRetryCount_++ < 3) SetTimer(candidate_, 3, 100, nullptr);
                    }
                }
                else {
                    try { Position(); }
                    catch (...) { StopTransition(); candidateDrawn_ = false;
                        if (candidateRetryCount_++ < 3) SetTimer(candidate_, 3, 100, nullptr); }
                }
            }
            if (revision != visualRevision_) return;
            if (force || !statusDrawn_ || state_.isOff != renderedState_.isOff || state_.isChinese != renderedState_.isChinese ||
                state_.nativeHook != renderedState_.nativeHook || state_.status != renderedState_.status || state_.hideStatus != renderedState_.hideStatus)
            {
                auto size = statusRenderer_.Render(status_, state_, {}, GetDpiForWindow(status_), true);
                MONITORINFO monitor{}; monitor.cbSize = sizeof(monitor);
                if (GetMonitorInfoW(MonitorFromWindow(status_, MONITOR_DEFAULTTONEAREST), &monitor))
                {
                    RECT current{}; GetWindowRect(status_, &current);
                    const auto& work = monitor.rcWork;
                    auto position = StatusPosition({current.left, current.top}, size.cx, size.cy,
                        {work.left, work.top, work.right, work.bottom}, !statusPlaced_);
                    statusPlaced_ = true;
                    SetWindowPos(status_, nullptr, position.x, position.y, 0, 0,
                        SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER);
                }
                ShowWindow(status_, state_.hideStatus ? SW_HIDE : SW_SHOWNOACTIVATE);
                statusDrawn_ = true;
            }
            renderedState_ = state_;
            KillTimer(candidate_, 1);
            auto delay = reveal_.NextDelay(state_, GetTickCount64());
            if (delay) SetTimer(candidate_, 1, delay, nullptr);
        }
        void FillSchemaMenu(HMENU menu)
        {
            while (GetMenuItemCount(menu) > 0) DeleteMenu(menu, 0, MF_BYPOSITION);
            openMenuSchemas_ = schemas_;
            for (std::size_t i = 0; i < openMenuSchemas_.size() && i < 1000; ++i)
                AppendMenuW(menu, MF_STRING | (openMenuSchemas_[i] == currentSchema_ ? MF_CHECKED : 0),
                    100 + i, Wide(openMenuSchemas_[i]).c_str());
            if (openMenuSchemas_.empty()) AppendMenuW(menu, MF_STRING | MF_GRAYED, 0, L"\u6682\u65e0\u65b9\u6848");
        }
        void Menu()
        {
            if (menuOpen_) return;
            GetCursorPos(&menuRequestPoint_);
            // Opening the menu must not wait for Core or queued pipe work.
            if (!demo_ && !schemaPending_) schemaPending_ = commands_->Send("get_schema_list", 1);
            HMENU menu = CreatePopupMenu(), schemas = CreatePopupMenu(), themes = CreatePopupMenu();
            if (!menu || !schemas || !themes)
            {
                if (menu) DestroyMenu(menu);
                if (schemas) DestroyMenu(schemas);
                if (themes) DestroyMenu(themes);
                return;
            }
            AppendMenuW(menu, MF_STRING, 1, L"Github\u9875\u9762");
            AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
            AppendMenuW(menu, MF_STRING, 2, L"\u65b9\u6848\u6587\u4ef6\u5939");
            AppendMenuW(menu, MF_STRING, 3, L"\u5bfc\u51fa\u7801\u8868");
            AppendMenuW(menu, MF_STRING, 4, L"\u91cd\u8f7d\u7801\u8868");
            AppendMenuW(menu, MF_STRING, 5, L"\u52a0\u8bcd");
            FillSchemaMenu(schemas);
            const Text themeNames[] = {u"\u9ed8\u8ba4", u"\u901a\u900f", u"\u4e00\u822c\u901a\u900f", u"\u8ff7\u96fe", u"\u661f\u591c", u"\u7eb8", u"\u7c89", u"\u8d5b\u535a\u670b\u514b", u"\u6e05\u6668"};
            for (unsigned i = 0; i < 9; ++i)
                AppendMenuW(themes, MF_STRING | (themeNames[i] == state_.theme ? MF_CHECKED : 0), 2000 + i, Wide(themeNames[i]).c_str());
            AppendMenuW(menu, MF_POPUP, reinterpret_cast<UINT_PTR>(schemas), L"\u65b9\u6848");
            AppendMenuW(menu, MF_POPUP, reinterpret_cast<UINT_PTR>(themes), L"\u4e3b\u9898");
            AppendMenuW(menu, MF_STRING, 6, L"\u8bbe\u7f6e");
            AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
            AppendMenuW(menu, MF_STRING, 7, L"\u9000\u51fa");
            POINT cursor = menuRequestPoint_;
            if (demo_) GetCursorPos(&cursor);
            menuOpen_ = true;
            // Foreground activation improves native keyboard/click handling,
            // but taskbar TSF callbacks cannot always grant it. Like WPF, keep
            // the menu usable without it and independently monitor dismissal.
            HWND previousForeground = GetForegroundWindow();
            // Caret/focus updates may hide either display window during a menu.
            // Keep the owner independent of those updates for its full lifetime.
            HWND owner = menuHost_;
            SetWindowPos(owner, HWND_TOPMOST, cursor.x, cursor.y, 1, 1, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            SetForegroundWindow(owner);
            menuDismiss_ = {KeyDown(VK_LBUTTON), KeyDown(VK_RBUTTON), KeyDown(VK_ESCAPE)};
            UINT_PTR dismissTimer = SetTimer(candidate_, 2, 20, nullptr);
            if (!dismissTimer && GetForegroundWindow() != owner)
            {
                menuOpen_ = false;
                DestroyMenu(menu);
                if (owner == menuHost_) ShowWindow(owner, SW_HIDE);
                return; // No native dismissal and no fallback monitor available.
            }
            activeSchemaMenu_ = schemas;
            int choice = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, cursor.x, cursor.y, owner, nullptr);
            activeSchemaMenu_ = nullptr;
            if (dismissTimer) KillTimer(candidate_, dismissTimer);
            menuOpen_ = false;
            PostMessageW(owner, WM_NULL, 0, 0);
            DestroyMenu(menu);
            // Escape/selection may leave our owner active; an outside click must
            // keep its newly selected application, never reactivate the old one.
            if (GetForegroundWindow() == owner && IsWindow(previousForeground))
                SetForegroundWindow(previousForeground);
            if (owner == menuHost_) ShowWindow(owner, SW_HIDE);
            if (demo_)
            {
                if (choice >= 2000 && choice < 2009) { state_.theme = themeNames[choice - 2000]; Refresh(true); }
                if (choice == 7) PostQuitMessage(0);
                return;
            }
            const char* actions[] = {"", "open_official", "open_mb_folder", "export_mb", "reload_mb", "show_addci", "show_config", "exit_core"};
            if (choice > 0 && choice <= 7) commands_->Send(actions[choice], choice == 2 ? 2 : choice == 7 ? 7 : 0);
            if (choice >= 100 && choice < 1100 && static_cast<std::size_t>(choice - 100) < openMenuSchemas_.size())
                commands_->SetConfig(u"\u5f53\u524d\u7801\u8868", openMenuSchemas_[choice - 100]);
            if (choice >= 2000 && choice < 2009) commands_->SetConfig(u"\u4e3b\u9898", themeNames[choice - 2000]);
        }
        void Cycle()
        {
            if (layoutPending_) return;
            if (!state_.hideCandidates)
            {
                if (state_.vertical) verticalCode_ = state_.showCode;
                else horizontalCode_ = state_.showCode;
            }
            State desired = state_;
            if (desired.hideCandidates && (desired.showCode || desired.nativeHook))
            {
                desired.hideCandidates = false; desired.vertical = false; desired.showCode = horizontalCode_;
            }
            else if (desired.vertical) { desired.hideCandidates = true; desired.showCode = true; }
            else { desired.hideCandidates = false; desired.vertical = true; desired.showCode = verticalCode_; }
            if (!demo_)
            {
                const std::pair<Text, Text> show{u"\u5019\u9009\u7a97\u663e\u793a\u7f16\u7801", desired.showCode ? u"\u662f" : u"\u5426"};
                const std::pair<Text, Text> hide{u"\u9690\u85cf\u5019\u9009", desired.hideCandidates ? u"\u662f" : u"\u5426"};
                const std::pair<Text, Text> vertical{u"\u7ad6\u6392\u5019\u9009", desired.vertical ? u"\u662f" : u"\u5426"};
                // Match WPF order. Stop at first failure; incoming Core state is
                // authoritative even if only part of a sequence was accepted.
                layoutPending_ = commands_->SetConfigs(desired.hideCandidates ?
                    std::vector<std::pair<Text, Text>>{show, hide} : std::vector<std::pair<Text, Text>>{hide, vertical, show}, 8);
                return;
            }
            state_ = std::move(desired);
            Refresh(true);
        }
        LRESULT Message(HWND hwnd, UINT message, WPARAM wp, LPARAM lp)
        {
            switch (message)
            {
            case DeferredGeometryMessage: refreshQueued_ = false; Refresh(true); return 0;
            case WM_INITMENUPOPUP:
                // Freeze command IDs to the list actually shown. Later Core
                // replies update the cache, not a submenu under the pointer.
                if (activeSchemaMenu_ && reinterpret_cast<HMENU>(wp) == activeSchemaMenu_)
                    FillSchemaMenu(activeSchemaMenu_);
                return 0;
            case WM_MOUSEACTIVATE: return MA_NOACTIVATE;
            case WM_ERASEBKGND: return 1;
            case WM_PAINT: { PAINTSTRUCT paint{}; BeginPaint(hwnd, &paint); EndPaint(hwnd, &paint); return 0; }
            case WM_TIMER:
                if (wp == 4) TransitionTick();
                else if (wp == 2) CheckMenuDismiss();
                else { if (wp == 3) KillTimer(candidate_, 3); Refresh(false); }
                return 0;
            case StateMessage:
                if (source_ && source_->Take(state_))
                {
                    candidateRetryCount_ = 0;
                    if (!layoutPending_ && !state_.hideCandidates)
                    { if (state_.vertical) verticalCode_ = state_.showCode; else horizontalCode_ = state_.showCode; }
                    if (state_.soundSequence > 0 && state_.soundSequence != soundSeq_)
                    { soundSeq_ = state_.soundSequence; sound_.Play(state_.soundVk, state_.soundVolume); }
                    Refresh(false);
                }
                return 0;
            case ReplyMessage:
                if (commands_)
                {
                    Reply reply;
                    while (commands_->Take(reply))
                    {
                        if (reply.tag == 8) layoutPending_ = false;
                        if (reply.tag == 1) schemaPending_ = false;
                        if (reply.tag == 7 && reply.success) PostQuitMessage(0);
                        if (!reply.success)
                        {
                            continue;
                        }
                        auto json = nlohmann::json::parse(reply.json);
                        if (reply.tag == 1)
                        {
                            schemas_.clear();
                            std::istringstream list(json.value("schema_list", std::string{}));
                            std::string line;
                            while (std::getline(list, line))
                            { if (!line.empty() && line.back() == '\r') line.pop_back(); if (!line.empty()) schemas_.push_back(FromUtf8(line)); }
                            currentSchema_ = FromUtf8(json.value("current_schema", std::string{}));
                        }
                        if (reply.tag == 2)
                        {
                            auto path = Wide(FromUtf8(json.value("path", std::string{})));
                            if (!path.empty()) ShellExecuteW(nullptr, L"open", path.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
                        }
                    }
                }
                return 0;
            case StaleMessage: PostQuitMessage(0); return 0;
            case MenuMessage: case WM_RBUTTONUP: Menu(); return 0;
            case WM_MBUTTONUP: if (hwnd == candidate_) Cycle(); return 0;
            case WM_MOUSEWHEEL:
                if (hwnd == candidate_)
                {
                    double fontSize = std::clamp((state_.fontSize > 0 ? state_.fontSize : 17) + GET_WHEEL_DELTA_WPARAM(wp) / 120.0 * 0.5, 3.0, 200.0);
                    if (!demo_) commands_->SetConfig(u"\u5b57\u4f53\u5927\u5c0f", FromUtf8(std::to_string(fontSize)));
                    else { state_.fontSize = fontSize; Refresh(true); }
                }
                return 0;
            case WM_LBUTTONDOWN:
                if (hwnd == status_)
                {
                    GetCursorPos(&dragPoint_); RECT rect{}; GetWindowRect(hwnd, &rect);
                    dragOrigin_ = {rect.left, rect.top}; dragging_ = true; SetCapture(hwnd);
                }
                return 0;
            case WM_MOUSEMOVE:
                if (dragging_)
                {
                    POINT cursor{}; GetCursorPos(&cursor);
                    SetWindowPos(status_, nullptr, dragOrigin_.x + cursor.x - dragPoint_.x, dragOrigin_.y + cursor.y - dragPoint_.y,
                        0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER);
                }
                return 0;
            case WM_LBUTTONUP: if (dragging_) { dragging_ = false; ReleaseCapture(); } return 0;
            case WM_CAPTURECHANGED: dragging_ = false; return 0;
            case WM_DPICHANGED: case WM_DISPLAYCHANGE: Refresh(true); return 0;
            case WM_SETTINGCHANGE:
                if (wp == SPI_SETWORKAREA) { Refresh(true); return 0; }
                break;
            case WM_CLOSE: PostQuitMessage(0); return 0;
            }
            return DefWindowProcW(hwnd, message, wp, lp);
        }
        static LRESULT CALLBACK Procedure(HWND hwnd, UINT message, WPARAM wp, LPARAM lp)
        {
            auto self = reinterpret_cast<Application*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
            if (message == WM_NCCREATE)
            {
                self = static_cast<Application*>(reinterpret_cast<CREATESTRUCTW*>(lp)->lpCreateParams);
                SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
            }
            try { return self ? self->Message(hwnd, message, wp, lp) : DefWindowProcW(hwnd, message, wp, lp); }
            catch (...) { ShowWindow(hwnd, SW_HIDE); return 0; }
        }
    public:
        explicit Application(bool demo, Endpoints endpoints = {}, bool isolated = false) :
            endpoints_(std::move(endpoints)), isolated_(isolated), directory_(Directory()),
            candidateRenderer_(directory_), statusRenderer_(directory_), transitionRenderer_(directory_), sound_(directory_), demo_(demo)
        {
            wchar_t setting[8]{};
            GetEnvironmentVariableW(L"TIGERCLAW_OVERLAY_TRANSITION", setting, 8);
            transitionsEnabled_ = wcscmp(setting, L"0") != 0;
        }
        ~Application()
        {
            source_.reset(); commands_.reset();
            if (candidate_) DestroyWindow(candidate_);
            if (status_) DestroyWindow(status_);
            if (menuHost_) DestroyWindow(menuHost_);
        }
        int Run()
        {
            for (const auto name : {CandidateClass, StatusClass, MenuClass})
            {
                WNDCLASSEXW type{}; type.cbSize = sizeof(type); type.hInstance = GetModuleHandleW(nullptr);
                type.lpszClassName = name; type.lpfnWndProc = Procedure; type.hCursor = LoadCursorW(nullptr, IDC_ARROW);
                if (!RegisterClassExW(&type)) throw std::runtime_error("Window class registration failed");
            }
            constexpr DWORD style = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TOPMOST;
            candidate_ = CreateWindowExW(style, CandidateClass, demo_ || isolated_ ? L"TigerClaw Native Preview" : L"TigerClawCandidate", WS_POPUP,
                0, 0, 1, 1, nullptr, nullptr, GetModuleHandleW(nullptr), this);
            status_ = CreateWindowExW(style, StatusClass, demo_ || isolated_ ? L"TigerClaw Native Preview Status" : L"TigerStatusOverlay", WS_POPUP,
                0, 0, 26, 50, nullptr, nullptr, GetModuleHandleW(nullptr), this);
            // An activatable transparent owner for a menu with both UI windows
            // hidden. Ordinary candidate/status visibility remains unchanged.
            menuHost_ = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_TOPMOST,
                MenuClass, L"TigerClaw Native Menu Host", WS_POPUP,
                0, 0, 1, 1, nullptr, nullptr, GetModuleHandleW(nullptr), this);
            if (!candidate_ || !status_ || !menuHost_) throw std::runtime_error("Window creation failed");
            SetLayeredWindowAttributes(menuHost_, 0, 0, LWA_ALPHA);
            RECT work{}; SystemParametersInfoW(SPI_GETWORKAREA, 0, &work, 0);
            SetWindowPos(status_, HWND_TOPMOST, work.right - 26, work.bottom - 50, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
            if (demo_)
            {
                state_.candidateVisible = state_.showCode = state_.showIndex = state_.isChinese = true;
                state_.input = u"lets"; state_.candidates = {u"\u800c\u4e09", u"\u65cb", u"\u800c\u758b", u"\U00020000"};
                state_.annotations = {u"le ts", u"lets", u"le ts", u"Unicode"};
                state_.caretX = work.left + 150; state_.caretY = work.top + 200; state_.caretHeight = 20;
                state_.selected = 1; state_.fontSize = 22;
            }
            else
            {
                commands_ = std::make_unique<CommandQueue>(candidate_, endpoints_);
                schemaPending_ = commands_->Send("get_schema_list", 1);
                source_ = std::make_unique<StateSource>(candidate_, endpoints_);
            }
            Refresh(true);
            MSG message{};
            while (GetMessageW(&message, nullptr, 0, 0) > 0) { TranslateMessage(&message); DispatchMessageW(&message); }
            return static_cast<int>(message.wParam);
        }
    };
}
int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    using namespace tiger::overlay;
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    HRESULT com = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    int result = 1;
    try
    {
        int argc = 0; auto argv = CommandLineToArgvW(GetCommandLineW(), &argc);
        bool demo = false;
        std::wstring testSession;
        for (int i = 1; i < argc; ++i)
        {
            if (!wcscmp(argv[i], L"--demo")) demo = true;
            if (!wcscmp(argv[i], L"--test-session") && i + 1 < argc) testSession = argv[++i];
        }
        std::wstring executable = argc ? argv[0] : L"";
        executable = executable.substr(executable.find_last_of(L"\\/") + 1);
        LocalFree(argv);
        if (!_wcsicmp(executable.c_str(), L"TigerClaw.Overlay.Native.Preview.exe")) demo = true;
        bool testBinary = !_wcsicmp(executable.c_str(), L"TigerClaw.Overlay.Native.Test.exe");
        if (testBinary || !testSession.empty())
        {
            if (!testBinary || testSession.empty() || testSession.size() > 64 ||
                testSession.find_first_not_of(L"abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_") != std::wstring::npos)
                throw std::runtime_error("Test executable requires --test-session and a bounded alphanumeric ID; production IPC is forbidden.");
        }
        Endpoints endpoints;
        if (testBinary)
        {
            std::wstring root = L"TigerClaw.Overlay.Test." + testSession;
            endpoints.pipe = L"\\\\.\\pipe\\" + root;
            endpoints.ui = L"Local\\" + root + L".Ui";
            endpoints.coreHeartbeat = L"Local\\" + root + L".Core";
            endpoints.overlayHeartbeat = L"Local\\" + root + L".Overlay";
            endpoints.menu = L"Local\\" + root + L".Menu";
        }
        if (demo && !_wcsicmp(executable.c_str(), L"TigerClaw.Overlay.exe"))
            throw std::runtime_error("Use TigerClaw.Overlay.Native.Preview.exe --demo for isolation from Core process discovery.");
        std::wstring mutexName = testBinary ? L"Local\\TigerClaw.Overlay.Test." + testSession + L".Instance" :
            demo ? L"Local\\TigerClaw.Overlay.Native.Preview" : L"Local\\TigerClaw.Overlay.Native.SingleInstance";
        Handle instance(CreateMutexW(nullptr, FALSE, mutexName.c_str()));
        if (!instance || GetLastError() == ERROR_ALREADY_EXISTS) result = 0;
        else if (!demo && !testBinary && FindWindowW(nullptr, L"TigerClawCandidate"))
        {
            MessageBoxW(nullptr, L"An Overlay is already running. Close it before starting the native replacement, or use --demo.",
                L"TigerClaw Native Overlay", MB_OK | MB_ICONINFORMATION);
            result = 0;
        }
        else { Application app(demo, endpoints, testBinary); result = app.Run(); }
    }
    catch (const std::exception& error)
    {
        MessageBoxA(nullptr, error.what(), "TigerClaw Native Overlay", MB_OK | MB_ICONERROR);
    }
    if (SUCCEEDED(com)) CoUninitialize();
    return result;
}
