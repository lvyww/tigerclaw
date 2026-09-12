#include "Placement.h"
#include <iostream>
#include <limits>
#include <random>
#include <stdexcept>
using namespace tiger::overlay;
namespace {
unsigned checks = 0;
void Check(bool ok, const char* message) { ++checks; if (!ok) throw std::runtime_error(message); }
Point Resolve(Placement& p, int y, int height, std::uint64_t content, WorkArea work = {0,0,1920,1040},
    unsigned dpi = 96, int x = 500, int caretHeight = 20, std::uintptr_t monitor = 1)
{
    Check(p.Acquire(x,y,caretHeight), "valid caret rejected");
    auto out = p.Resolve(240,height,work,false,monitor,dpi,content);
    Check(p.RecordCount() <= 100 && p.EvidenceCount() <= p.RecordCount(), "ring capacity/evidence accounting");
    Check(out.x >= work.left && out.y >= work.top, "top/left clamp");
    if (height + 2 <= std::int64_t(work.bottom) - work.top)
        Check(std::int64_t(out.y) + height <= std::int64_t(work.bottom) - 2, "bottom inset");
    return out;
}
}
int main()
{
    try
    {
        Placement p;
        Check(Resolve(p,1000,120,1).y == 855 && p.IsAbove(), "initial overflow above");
        for (unsigned n = 2; n <= 100; ++n)
        {
            p.EndInput();
            Check(Resolve(p,1000,20,n, {0,0,1920,1040},96,500+int(n)).y == 955 && p.IsAbove(),
                "above inheritance before expiry");
            Check(p.Anchor().x == 500+int(n), "new input retained old X");
            Check(p.EvidenceCount() == 1, "inherited above incorrectly renewed evidence");
        }
        p.EndInput();
        Check(Resolve(p,1000,20,101).y == 1005 && !p.IsAbove(), "evidence must expire on decision 101");
        Check(p.RecordCount() == 100 && p.EvidenceCount() == 0, "old evidence not evicted");
        // A genuine overflow at decision 80 extends life to 179, not forever.
        p.Reset();
        for (unsigned n=1; n<=180; ++n)
        {
            p.EndInput(); Resolve(p,1000,n==1 || n==80 ? 120 : 20,n);
            Check(p.IsAbove() == (n<180), "real evidence refresh has wrong lifetime");
        }
        p.Reset(); Resolve(p,1000,120,1);
        auto accepted=p;
        for (unsigned n=0; n<300; ++n) Resolve(p,1000,120,1);
        Check(p.RecordCount()==1 && p.EvidenceCount()==1, "duplicate frames aged or renewed history");
        for (int i=0;i<200;++i)
        { auto failed=p; Resolve(failed,1000,20,2); }
        Check(p.RecordCount()==1, "failed unpublished copies changed accepted history");
        Resolve(p,1000,20,2);
        for (unsigned n=0;n<300;++n) Resolve(p,1000,20,2);
        Check(p.RecordCount()==2 && p.EvidenceCount()==1, "retry or repaint counted more than once");
        p.EndInput(); Resolve(p,1000,20,2);
        Check(p.RecordCount()==3, "identical new input not counted");
        p=accepted;
        for(int dy:{-1,1,-2,2,-3,3,0})
        { Resolve(p,1000+dy,20,2); Check(p.IsAbove() && p.ReferenceY()==1000, "jitter reference drift"); }
        for(int y:{999,998,997,996}) Resolve(p,y,20,3);
        Check(!p.IsAbove() && p.ReferenceY()==996 && p.EvidenceCount()==0, "slow cumulative upward motion swallowed");
        Resolve(p,1000,20,4); Check(!p.IsAbove(), "invalidated older evidence resurrected");
        Resolve(p,1000,120,5); Resolve(p,1005,20,6);
        Check(p.IsAbove() && p.ReferenceY()==1005, "downward move lost bias/reference");
        Resolve(p,1001,20,7); Check(!p.IsAbove(), "upward from advanced reference not detected");
        p=accepted; Resolve(p,990,120,8); Check(p.IsAbove(), "upward move forced non-fitting below");
        p=accepted; Resolve(p,1000,20,8,{0,0,1920,1040},96,500,990);
        Check(!p.IsAbove(), "visibility did not override remembered direction");
        for(int field=0;field<6;++field)
        {
            p=accepted; WorkArea area{0,0,1920,1040}; unsigned dpi=96;std::uintptr_t monitor=1;
            if(field==0)monitor=2;
            if(field==1)dpi=144;
            if(field==2)area.bottom+=1;
            if(field==3)area.top+=1;
            if(field==4)area.left+=1;
            if(field==5)area.right+=1;
            Resolve(p,1000,20,2,area,dpi,700,20,monitor);
            Check(!p.IsAbove() && p.RecordCount()==1 && p.Anchor().x==700, "display environment did not reset");
        }
        p=accepted; p.RefreshAnchor(); Resolve(p,1000,20,2,{0,0,1920,1040},96,900);
        Check(p.IsAbove() && p.Anchor().x==900, "anchor refresh erased direction");
        p.Reset(); Resolve(p,200,20,1); p.RefreshAnchor(); Resolve(p,1000,120,2);
        Check(p.IsAbove(), "below lock prevented a required flip after anchor refresh");
        p=accepted;
        Check(!p.Acquire(0,0,0), "unknown caret accepted");
        p.Resolve(0,20,{0,0,1920,1040}); p.Resolve(20,20,{});
        Check(p.RecordCount()==1, "invalid geometry aged history");
        auto special=p.Resolve(100,50,{-1920,-1080,0,0},true,2,144,55);
        Check(special.x==-1910 && special.y==-1070 && p.RecordCount()==1, "special placement polluted history");
        Resolve(p,1000,20,2);Check(p.IsAbove(), "special placement cleared normal direction");
        const WorkArea areas[]={{0,0,1920,1040},{-1920,0,0,1040},{0,-1080,1920,-40},
            {-2560,-1440,0,-40},{1920,120,4480,1520},{0,48,1920,1080},{48,0,1920,1080},{0,0,1872,1080}};
        for(auto work:areas) for(unsigned dpi:{96u,120u,144u,192u})
        {
            const int line=int(24*dpi/96),y=work.bottom-80,shortHeight=int(16*dpi/96),tall=int(150*dpi/96);
            p.Reset(); Resolve(p,y,tall,1,work,dpi,work.left+100,line);
            const int epsilon=Placement::Tolerance(line,line,dpi);
            Check(epsilon==int((3*dpi+48)/96), "DPI tolerance");
            Resolve(p,y-epsilon,shortHeight,2,work,dpi,work.left+100,line);Check(p.IsAbove(), "inclusive jitter boundary");
            Resolve(p,y-epsilon-1,shortHeight,3,work,dpi,work.left+100,line);Check(!p.IsAbove(), "DPI upward boundary");
            p.Reset();
            for(unsigned n=1;n<=101;++n)
            { p.EndInput();Resolve(p,y,n==1?tall:shortHeight,n,work,dpi,work.left+100,line);
              Check(p.IsAbove()==(n<=100), "DPI/work-area expiry"); }
            for(int h=1;h<1700;h+=3)Resolve(p,y,h,200+h,work,dpi,work.left+100,line);
        }
        Check(Placement::Tolerance(3,20,192)==0 && Placement::Tolerance(8,20,192)==2, "tiny caret cap");
        p.Reset();Check(p.Acquire(20,30,0), "fallback height rejected");
        p.Resolve(10,10,{0,0,100,100});Check(!p.IsAbove(), "fallback height geometry");
        // Fixed seed independent visibility and bounded-memory constraints.
        std::mt19937 rng(0x100F11u);
        for(unsigned n=0;n<100000;++n)
        {
            const int y=1+int(rng()%1800),h=1+int(rng()%2200);
            const WorkArea work{-1900,-100,1920,1900};
            Resolve(p,y,h,n+1000,work,96,-100,20);
            const bool fitsAbove=std::int64_t(y)-20-5-h>=work.top && y-25<=work.bottom-2;
            const bool fitsBelow=y+5>=work.top && std::int64_t(y)+5+h<=work.bottom-2;
            Check(!(fitsBelow && !fitsAbove) || !p.IsAbove(), "unusable above wins over fitting below");
            Check(!(fitsAbove && !fitsBelow) || p.IsAbove(), "unusable below wins over fitting above");
        }
        p.Reset(); p.Acquire(100,100,20);
        auto extreme=p.Resolve((std::numeric_limits<int>::max)(),(std::numeric_limits<int>::max)(),
            {(std::numeric_limits<int>::min)(),(std::numeric_limits<int>::min)(),200,200});
        Check(extreme.x>= (std::numeric_limits<int>::min)() && extreme.y>= (std::numeric_limits<int>::min)(), "wide arithmetic");
        std::cout<<"{\"probe\":\"placement_history\",\"status\":\"passed\",\"checks\":"<<checks
            <<",\"capacity\":100,\"random_cases\":100000,\"physical_input_tested\":false}\n";
        return 0;
    }
    catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}
}
