#include "AnimationWake.h"
#include "FrameTransition.h"
#include "Renderer.h"
#include <iostream>
#include <vector>
#include <stdexcept>
using namespace tiger::overlay;
static constexpr UINT Wake = WM_APP + 155;
static void Check(bool ok, const char* why) { if (!ok) throw std::runtime_error(why); }
static HANDLE WINAPI Fallback(LPSECURITY_ATTRIBUTES a, LPCWSTR n, DWORD flags, DWORD access)
{ return flags ? nullptr : CreateWaitableTimerExW(a,n,flags,access); }
static HANDLE WINAPI Failed(LPSECURITY_ATTRIBUTES, LPCWSTR, DWORD, DWORD) { return nullptr; }
static BOOL WINAPI FailArm(HANDLE,const LARGE_INTEGER*,LONG,PTIMERAPCROUTINE,LPVOID,BOOL) { return FALSE; }
static MSG Receive(HWND window)
{
    MSG message{};
    auto end=AnimationNow()+3000;
    while(AnimationNow()<end) {
        while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)) {
            if(message.hwnd==window && message.message==Wake) return message;
            TranslateMessage(&message);DispatchMessageW(&message);
        }
        MsgWaitForMultipleObjectsEx(0,nullptr,10,QS_ALLINPUT,MWMO_INPUTAVAILABLE);
    }
    throw std::runtime_error("Animation wake timeout");
}
static double CpuMs()
{
    FILETIME a{},b{},k{},u{}; GetProcessTimes(GetCurrentProcess(),&a,&b,&k,&u);
    ULARGE_INTEGER kernel{},user{};kernel.LowPart=k.dwLowDateTime;kernel.HighPart=k.dwHighDateTime;
    user.LowPart=u.dwLowDateTime;user.HighPart=u.dwHighDateTime;
    return double(kernel.QuadPart+user.QuadPart)/10000;
}
int main()
{
    HWND window=nullptr;
    try {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);
        window=CreateWindowExW(WS_EX_LAYERED|WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW,L"STATIC",L"Overlay animation probe",
            WS_POPUP,100,100,400,80,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);
        Check(window!=nullptr,"isolated HWND");
        AnimationWake failed;
        Check(!failed.Initialize(window,Wake,false,Failed),"total timer creation failure");
        for(bool fallback:{false,true}) {
            AnimationWake wake;
            Check(wake.Initialize(window,Wake,false,fallback?Fallback:CreateWaitableTimerExW),"initialize worker");
            Check(!wake.Arm(AnimationNow()+5,FailArm),"timer arm failure surfaced");
            Check(wake.Arm(AnimationNow()+1),"arm");
            auto stale=Receive(window); wake.Cancel();
            Check(!wake.Take(stale.wParam),"cancel rejects old generation");
            for(int i=0;i<20;++i) {
                Check(wake.Arm(AnimationNow()+1),"arm cancelled wake");Sleep(3);wake.Cancel();
                MSG cancelled{};Check(!PeekMessageW(&cancelled,window,Wake,Wake,PM_REMOVE),"cancel drains queued wakes");
            }
            for(int i=0;i<100;++i) {wake.Cancel();Check(wake.Arm(AnimationNow()+20),"retarget");}
            Sleep(80); auto message=Receive(window);
            MSG extra{};Check(!PeekMessageW(&extra,window,Wake,Wake,PM_REMOVE),"blocked UI queues only one wake");
            Check(wake.Take(message.wParam),"current generation accepted");
            MONITORINFOEXW monitor{};monitor.cbSize=sizeof(monitor);
            DEVMODEW mode{};mode.dmSize=sizeof(mode);
            Check(GetMonitorInfoW(MonitorFromWindow(window,MONITOR_DEFAULTTONEAREST),&monitor)!=FALSE,"monitor");
            unsigned hz=EnumDisplaySettingsW(monitor.szDevice,ENUM_CURRENT_SETTINGS,&mode)?mode.dmDisplayFrequency:60;
            if(hz<30||hz>1000)hz=60;
            Renderer renderer(L"."); State state;state.isChinese=true;state.candidateVisible=true;state.showCode=true;
            state.input=u"abcd";state.candidates={u"candidate one",u"candidate two"};
            auto display=Format(state);
            FrameTransition transition;
            double start=AnimationNow(),cpu=CpuMs(),last=start,totalDraw=0;
            transition.Start({100,100,400,80},{500,200,800,100},start,hz,1000);
            std::vector<double> intervals;
            while(transition.Active()) {
                Check(wake.Arm(transition.Next(AnimationNow(),hz)),"schedule frame");
                message=Receive(window);Check(wake.Take(message.wParam),"frame generation");
                auto now=AnimationNow();intervals.push_back(now-last);last=now;
                auto rect=transition.Sample(now);POINT point{rect.x,rect.y};SIZE size{rect.width,rect.height};
                auto draw=AnimationNow();renderer.Prepare(state,display,96,false,&size);renderer.Present(window,&point);
                totalDraw+=AnimationNow()-draw;
            }
            RECT final{};GetWindowRect(window,&final);
            Check(final.left==500 && final.right==1300 && final.top==200,"exact final geometry");
            auto cpuUsed=CpuMs()-cpu;
            wake.Cancel();Sleep(40);Check(!PeekMessageW(&extra,window,Wake,Wake,PM_REMOVE),"idle produces no wakes");
            Check(wake.Arm(AnimationNow()+50),"arm before exit");wake.Shutdown();Sleep(60);
            Check(!PeekMessageW(&extra,window,Wake,Wake,PM_REMOVE),"exit drains wakes before window destruction");
            std::sort(intervals.begin(),intervals.end());
            std::cout<<"{\"fallback\":"<<fallback<<",\"display_hz\":"<<hz<<",\"frames\":"<<intervals.size()
                <<",\"interval_p50_ms\":"<<intervals[intervals.size()/2]<<",\"interval_p95_ms\":"<<intervals[intervals.size()*95/100]
                <<",\"draw_mean_ms\":"<<totalDraw/intervals.size()<<",\"cpu_ms\":"<<cpuUsed<<",\"duration_ms\":"<<last-start<<"}\n";
        }
        {
            AnimationWake wake;Check(wake.Initialize(window,Wake),"quit fixture");
            PostQuitMessage(37);wake.Cancel();MSG quit{};
            Check(PeekMessageW(&quit,nullptr,WM_QUIT,WM_QUIT,PM_REMOVE) && quit.message==WM_QUIT && quit.wParam==37,
                "cancellation swallowed quit");
        }
        DestroyWindow(window);CoUninitialize();return 0;
    } catch(const std::exception& e) {std::cerr<<e.what()<<'\n';if(window)DestroyWindow(window);return 1;}
}
