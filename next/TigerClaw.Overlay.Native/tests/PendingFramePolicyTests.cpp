#include "../Model.h"
#include <iostream>
#include <stdexcept>
using namespace tiger::overlay;
int main() {
    unsigned checks=0;
    auto check=[&](bool ok){++checks;if(!ok)throw std::runtime_error("Pending-frame policy mismatch");};
    try {
        State pending;pending.isChinese=true;pending.composition=5;pending.input=u"j";
        pending.candidateHoldWhilePending=true;
        check(PendingCandidateFrame(pending));
        for(int change=0;change<8;++change) {
            auto state=pending;
            switch(change) {
            case 0:state.candidateHoldWhilePending=false;break;
            case 1:state.candidateVisible=true;break;
            case 2:state.isChinese=false;break;
            case 3:state.isOff=true;break;
            case 4:state.composition=2;break;
            case 5:state.input.clear();break;
            case 6:state.candidates={u"ready"};break;
            case 7:state.hideCandidates=true;break;
            }
            check(!PendingCandidateFrame(state));
        }
        for(int delay:{1,200,60000}) {auto state=pending;state.hideCandidates=true;
            state.candidateDelay=delay;check(PendingCandidateFrame(state));}
        for(int composition=0;composition<8;++composition) {auto state=pending;
            state.composition=composition;check(PendingCandidateFrame(state)==(composition==5 || composition==6));}
        check(!PendingCandidateFrame({}));
        std::cout<<"{\"probe\":\"pending_frame_policy\",\"status\":\"passed\",\"checks\":"<<checks<<"}\n";
    }catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}
}
