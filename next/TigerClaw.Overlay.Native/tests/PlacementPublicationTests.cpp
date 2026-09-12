// Production Application and Renderer, test-owned HWNDs, no Core/TSF/IPC.
// Faults and clock are substituted only in this translation unit.
#include "Renderer.h"
#include <functional>
#include <iostream>
#include <stdexcept>
namespace history_probe {
std::uint64_t nowTick = 1000;
unsigned checks = 0, cases = 0, failPresent = 0, failShow = 0;
HWND candidate = nullptr;
std::function<void()> duringPresent;
std::vector<RECT> frames;
void Check(bool ok, const char* reason) { ++checks; if (!ok) throw std::runtime_error(reason); }
ULONGLONG WINAPI Tick() { return nowTick; }
BOOL WINAPI Present(HWND w,HDC dc,POINT* pos,SIZE* size,HDC src,POINT* origin,
    COLORREF key,BLENDFUNCTION* blend,DWORD flags)
{
    if(w==candidate && failPresent) { --failPresent;SetLastError(ERROR_GEN_FAILURE);return FALSE; }
    const auto ok=UpdateLayeredWindow(w,dc,pos,size,src,origin,key,blend,flags);
    if(ok && w==candidate && pos && size) frames.push_back({pos->x,pos->y,pos->x+size->cx,pos->y+size->cy});
    if(w==candidate && duringPresent) { auto callback=std::move(duringPresent);duringPresent={};callback(); }
    return ok;
}
BOOL WINAPI Show(HWND w,HWND after,int x,int y,int width,int height,UINT flags)
{
    if(w==candidate && failShow && (flags&SWP_SHOWWINDOW)) { --failShow;SetLastError(ERROR_GEN_FAILURE);return FALSE; }
    return SetWindowPos(w,after,x,y,width,height,flags);
}
}
#define UpdateLayeredWindow history_probe::Present
#include "../Renderer.cpp"
#undef UpdateLayeredWindow
#define GetTickCount64 history_probe::Tick
#define SetWindowPos history_probe::Show
#define wWinMain unusedOverlayEntryPoint
#include "../main.cpp"
#undef wWinMain
#undef SetWindowPos
#undef GetTickCount64
namespace tiger::overlay {
struct PlacementPublicationProbe
{
    Application app{true};
    RECT work{};
    PlacementPublicationProbe()
    {
        using namespace history_probe;
        nowTick+=1000; frames.clear(); failPresent=failShow=0;duringPresent={};
        for(const auto name:{CandidateClass,StatusClass})
        {
            WNDCLASSEXW c{};c.cbSize=sizeof(c);c.hInstance=GetModuleHandleW(nullptr);
            c.lpszClassName=name;c.lpfnWndProc=Application::Procedure;
            Check(RegisterClassExW(&c) || GetLastError()==ERROR_CLASS_ALREADY_EXISTS,"test class registration");
        }
        constexpr auto style=WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE|WS_EX_LAYERED|WS_EX_TOPMOST;
        app.candidate_=CreateWindowExW(style,CandidateClass,L"History test",WS_POPUP,0,0,1,1,nullptr,nullptr,GetModuleHandleW(nullptr),&app);
        app.status_=CreateWindowExW(style,StatusClass,L"History test status",WS_POPUP,0,0,1,1,nullptr,nullptr,GetModuleHandleW(nullptr),&app);
        Check(app.candidate_ && app.status_,"test windows");candidate=app.candidate_;
        MONITORINFO info{};info.cbSize=sizeof(info);
        Check(GetMonitorInfoW(MonitorFromPoint({0,0},MONITOR_DEFAULTTOPRIMARY),&info)!=FALSE,"test work area");work=info.rcWork;
        auto& s=app.state_;
        s.candidateVisible=s.isChinese=s.vertical=true;s.hideStatus=true;s.showCode=false;
        s.font=u"Segoe UI";s.fontSize=17;s.animationEnabled=false;s.caretHeight=20;
        s.caretX=work.left+100;s.caretY=work.top+200;s.input=u"ab";s.candidates={u"candidate"};
        app.Refresh(true);
        Check(IsWindowVisible(app.candidate_)!=FALSE,"initial measured frame");
        s.caretY=work.bottom-app.candidateSize_.cy-5-8;
        app.HideImmediately();app.placement_.Reset();app.Refresh(true);
        Below();
    }
    ~PlacementPublicationProbe()
    {
        history_probe::duringPresent={};history_probe::failPresent=history_probe::failShow=0;
        app.StopTransition();
        MSG msg{};
        while(PeekMessageW(&msg,app.candidate_,Application::RefreshMessage,Application::RefreshMessage,PM_REMOVE)) {}
        history_probe::candidate=nullptr;
    }
    void Drain()
    {
        MSG m{};unsigned n=0;
        while(PeekMessageW(&m,app.candidate_,Application::RefreshMessage,Application::RefreshMessage,PM_REMOVE))
        {history_probe::Check(++n<30,"deferred refresh did not settle");DispatchMessageW(&m);}
    }
    void Rows(unsigned n) { app.state_.candidates.assign(n,u"candidate"); }
    void Hide() { app.state_.candidateVisible=false;app.Refresh(false); }
    void NewInput(unsigned rows=1)
    { Hide();Rows(rows);app.state_.candidateVisible=true;app.Refresh(false); }
    void Seed()
    { app.HideImmediately();app.placement_.Reset();Rows(5);app.Refresh(true);Above(); }
    void Above()
    {
        RECT r{};history_probe::Check(GetWindowRect(app.candidate_,&r)!=FALSE,"candidate rectangle");
        history_probe::Check(app.placement_.IsAbove() && r.bottom==app.state_.caretY-app.state_.caretHeight-5,"expected above current caret");
    }
    void Below()
    {
        RECT r{};history_probe::Check(GetWindowRect(app.candidate_,&r)!=FALSE,"candidate rectangle");
        history_probe::Check(!app.placement_.IsAbove() && r.top==app.state_.caretY+5,"expected below current caret");
    }
    static void Run()
    {
        using namespace history_probe;
        {
            PlacementPublicationProbe p;p.Seed();
            for(unsigned n=2;n<=101;++n)
            {
                frames.clear();p.NewInput();
                if(n<=100)p.Above();else p.Below();
                Check(p.app.placement_.RecordCount()==std::min(n,100u),"new inputs not counted exactly once");
                Check(frames.size()==1,"new input published intermediate frames");
            }
            Check(p.app.placement_.EvidenceCount()==0,"expired evidence remains");++cases;
        }
        {
            PlacementPublicationProbe p;p.Seed();
            for(unsigned n=2;n<=180;++n)
            {p.NewInput(n==80?5:1);if(n<180)p.Above();else p.Below();}
            ++cases;
        }
        {
            PlacementPublicationProbe p;p.Seed();p.NewInput();
            for(int i=0;i<200;++i) {++p.app.state_.soundSequence;p.app.Refresh(i%2==0);}
            Check(p.app.placement_.RecordCount()==2 && p.app.placement_.EvidenceCount()==1,"repaint/status aged history");
            p.app.state_.candidates[0]=u"different";p.app.Refresh(false);
            Check(p.app.placement_.RecordCount()==3,"same-height content not counted");++cases;
        }
        {
            PlacementPublicationProbe p;p.app.state_.animationEnabled=true;p.app.transitionsEnabled_=true;
            p.Rows(5);p.app.Refresh(false);
            Check(p.app.transition_.Active() && p.app.placement_.RecordCount()==1 && !p.app.placement_.IsAbove(),"animation target counted early");
            for(int i=0;i<15;++i){nowTick+=10;p.app.TransitionTick();Check(p.app.placement_.RecordCount()==1,"animation samples aged history");}
            nowTick+=100;p.app.TransitionTick();p.Above();
            Check(p.app.placement_.RecordCount()==2,"completed animation did not count once");
            for(int i=0;i<10;++i)p.app.TransitionTick();
            Check(p.app.placement_.RecordCount()==2,"finished timer counted again");++cases;
        }
        {
            PlacementPublicationProbe p;p.app.state_.animationEnabled=true;p.app.transitionsEnabled_=true;
            p.Rows(5);p.app.Refresh(false);p.Hide();
            nowTick+=1000;p.app.TransitionTick();
            Check(p.app.placement_.RecordCount()==1 && !p.app.placement_.IsAbove(),"hidden pending target recorded");
            p.NewInput();p.Below();++cases;
        }
        {
            PlacementPublicationProbe p;p.Rows(5);failPresent=1;p.app.Refresh(false);
            Check(p.app.placement_.RecordCount()==1 && !p.app.placement_.IsAbove(),"failed present seeded history");
            p.app.Refresh(false);p.Above();Check(p.app.placement_.RecordCount()==2,"present retry count");++cases;
        }
        {
            PlacementPublicationProbe p;p.Hide();p.Rows(5);p.app.state_.candidateVisible=true;
            failShow=1;p.app.Refresh(false);
            Check(p.app.placement_.RecordCount()==1 && !IsWindowVisible(p.app.candidate_),"failed show seeded history");
            p.app.Refresh(false);p.Above();Check(p.app.placement_.RecordCount()==2,"show retry count");++cases;
        }
        {
            PlacementPublicationProbe p;p.app.state_.animationEnabled=true;p.app.transitionsEnabled_=true;
            p.Rows(5);p.app.Refresh(false);nowTick+=300;failPresent=1;p.app.TransitionTick();
            Check(p.app.placement_.RecordCount()==1 && !p.app.pendingPlacement_,"failed final animation target recorded");
            p.app.Refresh(false);nowTick+=300;p.app.TransitionTick();p.Above();
            Check(p.app.placement_.RecordCount()==2,"animation retry recorded more than once");++cases;
        }
        {
            PlacementPublicationProbe p;p.Rows(5);
            duringPresent=[&]{p.Hide();};p.app.Refresh(false);p.Drain();
            Check(p.app.placement_.RecordCount()==1 && !p.app.placement_.IsAbove() && !IsWindowVisible(p.app.candidate_),"reentrant hide recorded stale frame");
            p.NewInput();p.Below();++cases;
        }
        {
            PlacementPublicationProbe p;p.Seed();p.NewInput();
            const int y=p.app.state_.caretY;
            const int epsilon=Placement::Tolerance(20,20,p.app.candidateDpi_);
            for(int i=1;i<=epsilon;++i){p.app.state_.caretY=y-i;p.app.Refresh(false);p.Above();}
            p.app.state_.caretY=y-epsilon-1;p.app.Refresh(false);p.Below();
            Check(p.app.placement_.EvidenceCount()==0,"upward motion left reusable older evidence");++cases;
        }
        {
            PlacementPublicationProbe p;p.Seed();const auto x=p.app.state_.caretX,y=p.app.state_.caretY;
            p.app.state_.caretX=p.app.state_.caretY=0;p.app.Refresh(false);
            Check(!IsWindowVisible(p.app.candidate_) && p.app.placement_.RecordCount()==1,"invalid caret aged history");
            p.app.state_.caretX=x;p.app.state_.caretY=y;p.app.Refresh(false);p.Above();
            Check(p.app.placement_.RecordCount()==1,"same layout recovery counted twice");++cases;
        }
        {
            PlacementPublicationProbe p;p.Seed();p.Hide();p.Rows(1);
            p.app.state_.candidateDelay=100;p.app.state_.showCode=false;p.app.state_.candidateVisible=true;
            p.app.Refresh(false);
            Check(!IsWindowVisible(p.app.candidate_) && p.app.placement_.RecordCount()==1,"unrevealed candidates aged history");
            nowTick+=100;p.app.Refresh(false);p.Above();Check(p.app.placement_.RecordCount()==2,"delayed first frame count");++cases;
        }
        {
            PlacementPublicationProbe p;p.Seed();p.NewInput();
            ++p.app.state_.anchorRevision;p.app.state_.caretX+=40;p.app.Refresh(false);p.Above();
            Check(p.app.placement_.Anchor().x==p.app.state_.caretX && p.app.placement_.EvidenceCount()==1,"partial commit anchor refresh lost memory");++cases;
        }
        Check(cases==13,"missing publication scenarios");
    }
};
}
int main()
{
    try
    {
        using namespace history_probe;
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        Check(SUCCEEDED(CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED)),"COM initialization");
        tiger::overlay::PlacementPublicationProbe::Run();CoUninitialize();
        std::cout<<"{\"probe\":\"placement_publication\",\"status\":\"passed\",\"cases\":"<<cases<<",\"checks\":"<<checks
            <<",\"real_renderer_and_windows\":true,\"controlled_clock_and_faults\":true,\"production_ipc\":false}\n";
        return 0;
    }
    catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}
}
