#include "../Renderer.h"
#include <shellapi.h>
#include <shellscalingapi.h>
#include <windowsx.h>
#include <fstream>
#include <functional>
#include <iostream>
#include <map>
#include <nlohmann/json.hpp>

namespace pending_probe {
unsigned checks=0,cases=0,published=0;
ULONGLONG now=1000;
HWND candidate=nullptr;
unsigned failPresent=0,failShow=0;
bool withoutHint=false;
std::function<void()> duringPresent;
void Check(bool ok,const char* reason) {++checks;if(!ok)throw std::runtime_error(reason);}
ULONGLONG WINAPI Tick() {return now;}
BOOL WINAPI Present(HWND window,HDC dc,POINT* position,SIZE* size,HDC source,POINT* origin,
    COLORREF key,BLENDFUNCTION* blend,DWORD flags) {
    if(window==candidate && failPresent) {--failPresent;SetLastError(ERROR_GEN_FAILURE);return FALSE;}
    auto ok=UpdateLayeredWindow(window,dc,position,size,source,origin,key,blend,flags);
    if(ok && window==candidate) {
        ++published;
        if(duringPresent) {auto callback=std::move(duringPresent);duringPresent={};callback();}
    }
    return ok;
}
BOOL WINAPI Position(HWND window,HWND after,int x,int y,int width,int height,UINT flags) {
    if(window==candidate && (flags&SWP_SHOWWINDOW) && failShow) {--failShow;SetLastError(ERROR_GEN_FAILURE);return FALSE;}
    return SetWindowPos(window,after,x,y,width,height,flags);
}
}
#define GetTickCount64 pending_probe::Tick
#define UpdateLayeredWindow pending_probe::Present
#define SetWindowPos pending_probe::Position
#include "../Renderer.cpp"
#define wWinMain unusedOverlayEntry
#include "../main.cpp"
#undef wWinMain
#undef SetWindowPos
#undef UpdateLayeredWindow
#undef GetTickCount64

namespace tiger::overlay {
struct CandidateFrameHoldProbe {
    Application app{true};
    RECT work{};
    POINT caret{};
    bool vertical=true;
    CandidateFrameHoldProbe(bool columns=true):vertical(columns) {
        app.animationClock_ = [] { return double(pending_probe::now); };
        using namespace pending_probe;
        now+=1000;published=failPresent=failShow=0;duringPresent={};
        MONITORINFO info{sizeof(info)};
        Check(GetMonitorInfoW(MonitorFromPoint({100,100},MONITOR_DEFAULTTONEAREST),&info)!=FALSE,"No test monitor");
        work=info.rcWork;caret={work.left+120,work.top+250};
        WNDCLASSEXW type{};type.cbSize=sizeof(type);type.hInstance=GetModuleHandleW(nullptr);
        type.lpszClassName=L"TigerClaw.PendingFrame.Test";type.lpfnWndProc=Application::Procedure;
        Check(RegisterClassExW(&type)!=0 || GetLastError()==ERROR_CLASS_ALREADY_EXISTS,"Cannot register isolated class");
        auto create=[&] {return CreateWindowExW(WS_EX_LAYERED|WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW,
            type.lpszClassName,L"Pending frame test",WS_POPUP,0,0,1,1,nullptr,nullptr,type.hInstance,&app);};
        app.candidate_=create();app.status_=create();candidate=app.candidate_;
        Check(app.candidate_ && app.status_,"Cannot create isolated windows");
    }
    ~CandidateFrameHoldProbe() {pending_probe::duringPresent={};pending_probe::candidate=nullptr;}
    void Install(State state) {
        state.caretX=caret.x;state.caretY=caret.y;state.caretHeight=20;
        state.font=u"Segoe UI";state.fontSize=17;state.vertical=vertical;
        state.hideStatus=true;state.animationEnabled=false;state.animationDurationMs=200;
        state.candidateDelay=state.annotationDelay=0;
        if(pending_probe::withoutHint)state.candidateHoldWhilePending=false;
        app.state_=std::move(state);
    }
    void Refresh() {app.Refresh(false);}
    void Tick(unsigned milliseconds=250) {pending_probe::now+=milliseconds;app.TransitionTick();}
    void Drain() {MSG msg{};unsigned count=0;while(PeekMessageW(&msg,nullptr,0,0,PM_REMOVE)) {
        TranslateMessage(&msg);DispatchMessageW(&msg);
        pending_probe::Check(++count<200,"Refresh loop did not settle");}}
    RECT Rect() {RECT rect{};pending_probe::Check(GetWindowRect(app.candidate_,&rect)!=FALSE,"Missing candidate geometry");return rect;}
    bool Visible() {return IsWindowVisible(app.candidate_)!=FALSE;}
    void Held(const RECT& before,unsigned frames,std::size_t records) {
        auto rect=Rect();
        pending_probe::Check(Visible() && EqualRect(&rect,&before) && pending_probe::published==frames,
            "Pending suffix hid or changed published candidate frame");
        pending_probe::Check(!app.transition_.Active() && !app.pendingPlacement_,"Pending suffix retained old animation");
        pending_probe::Check(app.placement_.RecordCount()==records,"Pending suffix consumed placement history");
    }
    static void BlankResidence() {
        using namespace pending_probe;
        State ready;ready.isChinese=true;ready.composition=2;ready.candidateVisible=true;
        ready.input=u"ab";ready.candidates={u"Alpha",u"Beta"};ready.showCode=true;
        ready.candidateFrameSession=u"before";ready.residenceDurationMs=1500;
        auto commit=[&] {
            State idle=ready;idle.composition=1;idle.candidateVisible=false;
            idle.input.clear();idle.candidates.clear();idle.candidateFrameSession=u"committed";
            idle.backgroundUntil=now+ready.residenceDurationMs;return idle;
        };
        for(int duration:{0,1,250,3000,60000}) {
            CandidateFrameHoldProbe p;ready.residenceDurationMs=duration;p.Install(ready);p.Refresh();
            auto idle=commit();p.Install(idle);p.Refresh();
            Check(p.Visible()==(duration>0), "Residence duration enable/disable failed");
            if(duration>0) {
                now+=duration-1;p.Refresh();Check(p.Visible(), "Custom residence expired early");
                ++now;p.Refresh();Check(!p.Visible(), "Custom residence exceeded deadline");
            }
            ++cases;
        }
        ready.residenceDurationMs=1500;
        for(bool vertical:{false,true}) {
            CandidateFrameHoldProbe p(vertical);p.Install(ready);p.Refresh();
            const auto before=p.Rect();const auto frames=published;
            auto idle=commit();p.Install(idle);p.Refresh();
            auto rect=p.Rect();
            Check(p.Visible() && p.app.blankResidence_ && EqualRect(&before,&rect),"Commit changed residence geometry/visibility");
            Check(published==frames+1 && p.app.display_.text.empty() && !p.app.frameCanBeHeld_,"Residence retained candidate content");
            const auto blankFrames=published;const auto deadline=p.app.blankDeadline_;
            p.app.state_.caretX+=80;p.app.state_.caretY+=10;
            now+=1499;p.Refresh();rect=p.Rect();
            Check(p.Visible() && EqualRect(&rect,&before) && published==blankFrames && p.app.blankDeadline_==deadline,
                "Caret update moved/repainted/extended residence");
            ++now;p.app.Message(p.app.candidate_,WM_TIMER,5,0);
            Check(!p.Visible() && !p.app.blankResidence_,"Residence did not expire at 1500ms");
            p.Refresh();Check(!p.Visible(),"Expired hint restarted residence");++cases;
        }
        {
            CandidateFrameHoldProbe p;p.Install(ready);p.Refresh();const auto before=p.Rect();
            p.Install(commit());p.Refresh();now+=500;
            auto next=ready;next.candidateFrameSession=u"new-input";next.candidates={u"Next"};
            next.candidateVisible=false;p.Install(next);p.Refresh();
            Check(p.Visible() && p.app.blankResidence_,"Fresh-caret wait hid resident frame before continuation");
            next.candidateVisible=true;
            p.caret.x+=70;p.Install(next);p.app.state_.animationEnabled=true;p.Refresh();
            auto rect=p.Rect();
            Check(p.Visible() && !p.app.blankResidence_ && p.app.transition_.Active() && EqualRect(&before,&rect),
                "New input did not animate from resident rectangle");
            p.Tick(250);Check(p.Visible() && p.app.frameCanBeHeld_ && !p.app.display_.text.empty(),"New frame did not finish");
            now+=2000;p.app.Message(p.app.candidate_,WM_TIMER,5,0);
            Check(p.Visible(),"Stale residence timer hid new input");++cases;
        }
        {
            CandidateFrameHoldProbe p;p.Install(ready);p.Refresh();p.Install(commit());p.Refresh();
            auto next=ready;next.candidateFrameSession=u"delayed-input";next.showCode=false;
            p.Install(next);p.app.state_.candidateDelay=200;p.app.state_.animationEnabled=true;
            p.Refresh();Check(p.Visible() && p.app.blankResidence_,"Reveal delay hid resident frame");
            now+=199;p.Refresh();Check(p.Visible() && p.app.blankResidence_,"Reveal delay ended early");
            ++now;p.Refresh();Check(p.Visible() && !p.app.blankResidence_ && p.app.transition_.Active(),"Delayed reveal failed to animate");
            p.Tick(250);Check(p.Visible() && !p.app.display_.text.empty(),"Delayed candidate did not publish");++cases;
        }
        for(unsigned elapsed:{0u,10u,50u,99u}) {
            CandidateFrameHoldProbe p;p.Install(ready);p.Refresh();p.Install(commit());p.Refresh();
            for(unsigned word=0;word<30;++word) {
                auto next=ready;next.input=u"vujg";next.candidates={u"\u8fd9\u4e2a"};
                next.candidateFrameSession=FromUtf8("repeated-"+std::to_string(word));
                p.caret.x+=20;p.Install(next);p.app.state_.animationEnabled=true;p.app.state_.animationDurationMs=100;p.Refresh();
                Check(p.Visible() && p.app.transition_.Active(),"Repeated input did not start a transition");
                p.Tick(elapsed);const auto before=p.Rect();
                auto idle=commit();idle.candidateFrameSession=FromUtf8("commit-"+std::to_string(word));
                p.Install(idle);p.Refresh();const auto after=p.Rect();
                Check(p.Visible() && p.app.blankResidence_ && p.app.display_.text.empty() && EqualRect(&before,&after),
                    "Commit during unfinished transition lost residence");
                now+=1;
            }
            ++cases;
        }
        for(bool expire:{false,true}) {
            CandidateFrameHoldProbe p;p.Install(ready);p.Refresh();p.Install(commit());p.Refresh();
            auto waiting=ready;waiting.candidateVisible=false;waiting.backgroundUntil=0;
            waiting.candidateFrameSession=u"waiting-input";p.Install(waiting);p.Refresh();
            Check(p.Visible() && p.app.blankResidence_,"Wait did not preserve blank frame");
            if(expire)now+=1500;else p.app.state_.candidateFrameSession=u"different-focus";
            p.Refresh();Check(!p.Visible(),"Wait bypassed expiry or session invalidation");++cases;
        }
        {
            CandidateFrameHoldProbe p;p.Install(ready);p.Refresh();p.Install(commit());p.Refresh();
            auto waiting=ready;waiting.candidateVisible=false;waiting.backgroundUntil=0;
            waiting.candidateFrameSession=u"fast-next";p.Install(waiting);p.Refresh();
            const auto before=p.Rect();const auto frames=published;
            now+=20;p.Install(commit());p.Refresh();const auto after=p.Rect();
            Check(p.Visible() && p.app.blankResidence_ && EqualRect(&before,&after) && frames==published,
                "Commit during caret wait lost or repainted blank residence");
            now+=1499;p.Refresh();Check(p.Visible(),"Fresh commit failed to renew residence deadline");
            ++now;p.Refresh();Check(!p.Visible(),"Renewed residence outlasted 1500ms");++cases;
        }
        for(int cancel=0;cancel<8;++cancel) {
            CandidateFrameHoldProbe p;p.Install(ready);p.Refresh();p.Install(commit());p.Refresh();
            if(cancel==0)p.app.state_.backgroundUntil=0;
            if(cancel==1)p.app.state_.candidateFrameSession=u"focus-changed";
            if(cancel==2)p.app.state_.isChinese=false;
            if(cancel==3)p.app.state_.isOff=true;
            if(cancel==4)p.app.state_.hideCandidates=true;
            if(cancel==5)p.app.frameForeground_=reinterpret_cast<HWND>(-1);
            if(cancel==6)p.app.state_.caretX=-300000;
            if(cancel==7)p.app.state_.residenceDurationMs=0;
            p.Refresh();Check(!p.Visible() && !p.app.blankResidence_,"Cancelled residence stayed visible");++cases;
        }
        for(int composition:{2,3,4,5}) {
            CandidateFrameHoldProbe p;auto state=ready;state.composition=composition;
            p.Install(state);p.Refresh();auto idle=commit();
            if(composition==2)idle.backgroundUntil=0; // Escape/Backspace, not a text commit
            p.Install(idle);p.Refresh();Check(!p.Visible(),"Nonordinary/cancelled input started residence");++cases;
        }
        {
            CandidateFrameHoldProbe p;p.Install(commit());p.Refresh();Check(!p.Visible(),"Commit hint created an unseen window");++cases;
        }
        {
            CandidateFrameHoldProbe p;p.Install(ready);p.Refresh();p.Install(commit());failPresent=1;p.Refresh();
            Check(!p.Visible(),"Failed blank publication retained old text");++cases;
        }
        {
            CandidateFrameHoldProbe p;p.Install(ready);p.Refresh();p.Install(commit());
            duringPresent=[&] {p.app.state_.backgroundUntil=0;p.app.state_.candidateFrameSession=u"cancel";p.Refresh();};
            p.Refresh();Check(!p.Visible() && !p.app.blankResidence_,"Reentrant cancellation resurrected blank frame");++cases;
        }
    }
    static void Run(const std::map<std::string,State>& wire) {
        using namespace pending_probe;
        for(bool vertical:{true,false}) {
            for(const char* ending:{"commit","cancel","escape","backspace"}) {
                const std::string name=ending;
                CandidateFrameHoldProbe p(vertical);p.Install(wire.at("previous_"+name));p.Refresh();
                auto frames=published;auto records=p.app.placement_.RecordCount();
                // Drop the actual hidden snapshot, as the latest-only channel can.
                p.Install(wire.at("new_"+name+"_pending"));p.Refresh();
                Check(!p.Visible(),"Coalesced new input retained previous candidates");
                Check(published==frames && p.app.placement_.RecordCount()==records,
                    "New-input wait rendered or aged placement history");
                p.Tick(1000);p.Drain();Check(!p.Visible(),"Late timer revived old-session frame");
                p.Install(wire.at("new_"+name+"_ready"));p.Refresh();
                Check(p.Visible() && p.app.frameCanBeHeld_,"New-session result failed to show");
                auto before=p.Rect();frames=published;records=p.app.placement_.RecordCount();
                p.Install(wire.at("new_"+name+"_continuation"));p.Refresh();p.Held(before,frames,records);++cases;
            }
            for(bool codeOnly:{false,true})for(int animation:{0,1,2}) {
                CandidateFrameHoldProbe p(vertical);p.Install(wire.at("ready"));p.Refresh();
                auto code=wire.at("ready");code.showCode=true;code.fontSize=9;
                if(codeOnly)code.hideCandidates=true;else code.candidates.clear();
                p.Install(code);p.app.state_.fontSize=9;p.app.state_.animationEnabled=animation!=0;p.Refresh();
                if(animation) {Check(p.app.transition_.Active(),"Code animation did not start");p.Tick(animation==1?50:250);}
                Check(p.Visible() && p.app.frameCanBeHeld_,"Published code-only frame lost hold eligibility");
                auto before=p.Rect();auto frames=published;auto records=p.app.placement_.RecordCount();
                p.Install(wire.at("pending"));p.Refresh();p.Tick(1000);p.Drain();
                p.Held(before,frames,records);++cases;
            }
            {
                CandidateFrameHoldProbe p(vertical);p.Install(wire.at("ready"));p.Refresh();
                Check(p.Visible() && p.app.frameCanBeHeld_,"Ready frame not published");
                auto before=p.Rect();auto frames=published;auto records=p.app.placement_.RecordCount();
                p.Install(wire.at("pending"));p.Refresh();p.Held(before,frames,records);
                for(unsigned i=0;i<101;++i) {p.app.Refresh(true);p.Tick();p.Held(before,frames,records);}
                p.app.state_.hideStatus=false;p.app.state_.status=u"H";p.Refresh();
                Check(IsWindowVisible(p.app.status_)!=FALSE,"Holding candidates blocked status update");
                p.Held(before,frames,records);
                p.Install(wire.at("completed_candidates"));p.Refresh();
                Check(p.Visible() && published>frames && p.app.candidateDrawn_,"Completion did not refresh held frame");++cases;
            }
            {
                CandidateFrameHoldProbe p(vertical);p.Install(wire.at("initial_pending"));p.Refresh();
                Check(!p.Visible() && published==0 && !p.app.frameCanBeHeld_,"First pending result showed a placeholder");
                p.app.Message(p.app.candidate_,WM_TIMER,3,0);p.Tick();
                Check(!p.Visible(),"Retry resurrected a first pending frame");
                p.Install(wire.at("ready"));p.Refresh();Check(p.Visible(),"First result did not show");++cases;
            }
            {
                CandidateFrameHoldProbe p(vertical);auto code=wire.at("ready");code.candidates.clear();code.showCode=true;
                p.Install(code);p.Refresh();Check(p.Visible() && p.app.frameCanBeHeld_,"Published input-only frame was not eligible");
                auto before=p.Rect();auto frames=published;auto records=p.app.placement_.RecordCount();
                p.Install(wire.at("pending"));p.Refresh();p.Held(before,frames,records);++cases;
            }
            {
                CandidateFrameHoldProbe p(vertical);p.Install(wire.at("ready"));p.Refresh();
                p.Install(wire.at("pending"));p.Refresh();auto frames=published;
                p.Install(wire.at("completed_empty"));p.Refresh();
                Check(p.Visible() && published>frames && p.app.display_.mode==DisplayMode::InputOnly,
                    "Completed empty result froze the old frame");++cases;
            }
            for(const char* action:{"cancel","focus","inactive","english","disabled"}) {
                CandidateFrameHoldProbe p(vertical);p.Install(wire.at("ready"));p.Refresh();
                p.Install(wire.at("pending"));p.Refresh();p.Install(wire.at(action));p.Refresh();
                Check(!p.Visible() && !p.app.frameCanBeHeld_,"End/focus/deactivation did not hide pending frame");
                p.Install(wire.at("pending"));p.Refresh();p.Tick();p.Drain();
                Check(!p.Visible(),"Old pending frame resurrected after hide");++cases;
            }
        }
        {
            CandidateFrameHoldProbe p;p.Install(wire.at("ready"));p.Refresh();
            auto before=p.Rect();auto frames=published;auto records=p.app.placement_.RecordCount();
            auto code=wire.at("completed_empty");p.Install(code);failPresent=1;p.Refresh();
            Check(p.app.frameCanBeHeld_,"Failed code publication invalidated untouched candidate pixels");
            p.Install(wire.at("pending"));p.Refresh();p.Held(before,frames,records);++cases;
        }
        {
            CandidateFrameHoldProbe p;p.Install(wire.at("ready"));p.Refresh();
            p.Install(wire.at("completed_empty"));
            duringPresent=[&]{p.Install(wire.at("pending"));p.Refresh();};p.Refresh();p.Drain();
            Check(p.Visible() && p.app.frameCanBeHeld_,"Reentrant pending hid published code pixels");
            auto before=p.Rect();auto frames=published;auto records=p.app.placement_.RecordCount();
            p.Refresh();p.Held(before,frames,records);++cases;
        }
        {
            CandidateFrameHoldProbe p;p.Install(wire.at("previous_commit"));p.Refresh();
            auto many=wire.at("previous_commit");many.candidates={u"one",u"two",u"three"};p.Install(many);
            duringPresent=[&]{p.Install(wire.at("new_commit_pending"));p.Refresh();};p.Refresh();p.Tick();p.Drain();
            Check(!p.Visible(),"Reentrant new session retained an old candidate frame");++cases;
        }
        {
            // Missing tokens (old Core/earlier PR builds) conservatively disable hold.
            CandidateFrameHoldProbe p;auto ready=wire.at("ready");ready.candidateFrameSession.clear();p.Install(ready);p.Refresh();
            auto pending=wire.at("pending");pending.candidateFrameSession.clear();p.Install(pending);p.Refresh();
            Check(!p.Visible(),"Missing session token allowed a pending hold");++cases;
        }
        {
            CandidateFrameHoldProbe p;p.Install(wire.at("previous_commit"));p.Refresh();
            p.Install(wire.at("new_commit_pending"));p.Refresh();
            p.Install(wire.at("new_commit_ready"));p.app.state_.candidateDelay=200;p.app.state_.showCode=false;p.Refresh();
            Check(!p.Visible(),"Coalesced session inherited the old reveal clock");
            p.Tick(201);p.app.Message(p.app.candidate_,WM_TIMER,1,0);
            Check(p.Visible(),"Fresh session did not reveal candidates");++cases;
        }
        {
            CandidateFrameHoldProbe p;p.Install(wire.at("ready"));p.Refresh();
            p.Install(wire.at("completed_empty"));p.Refresh();
            Check(p.app.display_.mode==DisplayMode::InputOnly,"Expected real Core empty-result input frame");
            auto before=p.Rect();auto frames=published;auto records=p.app.placement_.RecordCount();
            p.Install(wire.at("pending_after_empty"));p.Refresh();p.Held(before,frames,records);
            p.Install(wire.at("completed_candidates"));p.Refresh();
            Check(p.Visible() && published>frames,"Input-only continuation did not refresh candidates");++cases;
        }
        for(bool clear:{false,true}) {
            CandidateFrameHoldProbe p;p.Install(wire.at("completed_empty"));p.Refresh();
            Check(p.Visible(),"Input-only reset fixture did not show");
            if(clear) {p.Install(wire.at("cancel"));p.Refresh();p.Install(wire.at("pending"));}
            else p.Install(wire.at("new_commit_pending"));
            p.Refresh();p.Tick();p.Drain();
            Check(!p.Visible(),"Input-only frame crossed clear/session boundary");++cases;
        }
        for(unsigned failure:{0u,1u}) {
            CandidateFrameHoldProbe p;p.Install(wire.at("ready"));
            if(failure==0)failPresent=1;else failShow=1;p.Refresh();
            Check(!p.Visible() && !p.app.frameCanBeHeld_,"Failed first frame latched real presentation");
            p.Install(wire.at("pending"));p.Refresh();p.Tick();Check(!p.Visible(),"Failed first frame was resurrected");++cases;
        }
        {
            CandidateFrameHoldProbe p;p.Install(wire.at("ready"));p.Refresh();
            auto many=wire.at("ready");many.candidates={u"one",u"two",u"three",u"four",u"five"};
            p.Install(many);p.app.state_.animationEnabled=true;p.Refresh();
            Check(p.app.transition_.Active(),"Animation fixture did not start");p.Tick(50);
            auto before=p.Rect();auto frames=published;auto records=p.app.placement_.RecordCount();
            p.Install(wire.at("pending"));p.Refresh();p.Held(before,frames,records);p.Tick(1000);p.Held(before,frames,records);
            p.Install(wire.at("completed_candidates"));p.Refresh();Check(p.Visible(),"Animation interruption prevented completion");++cases;
        }
        {
            CandidateFrameHoldProbe p;p.Install(wire.at("ready"));p.Refresh();auto records=p.app.placement_.RecordCount();
            auto many=wire.at("ready");many.candidates={u"one",u"two",u"three"};p.Install(many);
            duringPresent=[&] {p.Install(wire.at("pending"));p.Refresh();};p.Refresh();
            Check(p.Visible() && p.app.placement_.RecordCount()==records,"Reentrant pending hid or accepted superseded frame");
            auto before=p.Rect();auto frames=published;p.Drain();p.Held(before,frames,records);++cases;
        }
        {
            CandidateFrameHoldProbe p;p.Install(wire.at("ready"));p.Refresh();
            auto many=wire.at("ready");many.candidates={u"one",u"two",u"three"};p.Install(many);
            duringPresent=[&] {p.Install(wire.at("cancel"));p.Refresh();};p.Refresh();p.Drain();
            Check(!p.Visible() && !p.app.frameCanBeHeld_,"Reentrant cancel retained pixels");++cases;
        }
        for(unsigned invalid=0;invalid<4;++invalid) {
            CandidateFrameHoldProbe p;p.Install(wire.at("ready"));p.Refresh();p.Install(wire.at("pending"));
            if(invalid==0)p.app.state_.caretX=p.app.state_.caretY=0;
            if(invalid==1)++p.app.frameDpi_; // controlled monitor/DPI API mismatch
            if(invalid==2)--p.app.frameWork_.bottom;
            if(invalid==3)p.app.state_.hideCandidates=true;
            p.Refresh();Check(!p.Visible(),"Invalid geometry/explicit hiding was bypassed");++cases;
        }
        {
            CandidateFrameHoldProbe p;p.Install(wire.at("ready"));p.app.state_.candidateDelay=200;p.Refresh();
            Check(!p.Visible(),"Initial reveal delay ignored");p.Install(wire.at("pending"));p.Refresh();p.Tick(1000);
            Check(!p.Visible(),"Pending first result bypassed reveal");++cases;
        }
        {
            CandidateFrameHoldProbe p;p.Install(wire.at("ready"));p.Refresh();auto records=p.app.placement_.RecordCount();
            auto anchor=p.app.placement_.Anchor();p.Install(wire.at("pending"));++p.app.state_.anchorRevision;p.app.state_.caretX+=100;
            p.Refresh();Check(p.app.placement_.Anchor().x==anchor.x && p.app.placement_.RecordCount()==records,
                "Waiting state repositioned or consumed anchor revision");
            auto done=wire.at("completed_candidates");done.anchorRevision=p.app.state_.anchorRevision;p.Install(done);p.app.state_.caretX+=100;p.Refresh();
            Check(p.app.placement_.Anchor().x==anchor.x+100,"Deferred anchor revision was lost");++cases;
        }
    }
};
}
int main(int argc,char** argv) {
    using namespace pending_probe;
    try {
        Check(argc==2 || argc==3,"Provide Core-generated JSONL trace and optional --without-hint");
        withoutHint=argc==3 && std::string(argv[2])=="--without-hint";
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        Check(SUCCEEDED(CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED)),"COM initialization failed");
        if(std::string(argv[1])=="--blank-residence") {
            tiger::overlay::CandidateFrameHoldProbe::BlankResidence();CoUninitialize();
            std::cout<<"blank residence passed: "<<cases<<" cases, "<<checks<<" checks\n";return 0;
        }
        std::map<std::string,tiger::overlay::State> wire;
        if(std::string(argv[1])=="--synthetic") {
            tiger::overlay::State ready;ready.isChinese=true;ready.composition=5;
            ready.candidateVisible=true;ready.input=u"abcd";ready.candidates={u"Alpha",u"Beta"};
            ready.candidateFrameSession=u"session";ready.showCode=false;
            auto pending=ready;pending.candidateVisible=false;pending.candidates.clear();
            pending.candidateHoldWhilePending=true;pending.input+=u"e";
            wire["ready"]=wire["completed_candidates"]=ready;
            wire["pending"]=wire["pending_after_empty"]=wire["initial_pending"]=pending;
            auto empty=ready;empty.candidates.clear();empty.showCode=true;wire["completed_empty"]=empty;
            auto hidden=ready;hidden.input.clear();hidden.candidates.clear();hidden.candidateVisible=false;
            for(auto name:{"cancel","focus","inactive","english","disabled"})wire[name]=hidden;
            for(std::string name:{"commit","cancel","escape","backspace"}) {
                wire["previous_"+name]=ready;
                auto next=ready;next.candidateFrameSession=u"new-session";
                wire["new_"+name+"_ready"]=next;
                next=pending;next.candidateFrameSession=u"new-session";
                wire["new_"+name+"_pending"]=wire["new_"+name+"_continuation"]=next;
            }
            tiger::overlay::CandidateFrameHoldProbe::Run(wire);CoUninitialize();
            std::cout<<"synthetic pending-frame passed: "<<cases<<" cases, "<<checks<<" checks (no Core-generated trace)\n";
            return 0;
        }
        std::ifstream file(argv[1]);Check(file.good(),"Cannot read Core-generated trace");
        std::string line;
        while(std::getline(file,line)) {
            auto entry=nlohmann::json::parse(line);tiger::overlay::State state;
            Check(tiger::overlay::ParseState(entry.at("state").dump(),state),"Shared snapshot failed Native parsing");
            wire.emplace(entry.at("phase").get<std::string>(),std::move(state));
        }
        Check(wire.at("pending").candidateHoldWhilePending,"Wire lost the pending-frame hint");
        tiger::overlay::State legacy;Check(tiger::overlay::ParseState("{}",legacy) && !legacy.candidateHoldWhilePending && legacy.candidateFrameSession.empty(),
            "Absent optional field must default false");
        tiger::overlay::CandidateFrameHoldProbe::Run(wire);CoUninitialize();
        std::cout<<"{\"probe\":\"pending_frame_windows\",\"status\":\"passed\",\"cases\":"<<cases<<",\"checks\":"<<checks
            <<",\"real_windows_and_renderer\":true,\"core_generated_wire\":true,\"production_ipc\":false,\"physical_input_tested\":false}\n";
        return 0;
    }catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}
}
