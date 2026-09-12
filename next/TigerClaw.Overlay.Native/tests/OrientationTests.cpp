#include "Placement.h"
#include <iostream>
#include <limits>
#include <random>
using namespace tiger::overlay;
namespace {
unsigned checks = 0, positions = 0;
void Check(bool value, const char* message) { ++checks; if (!value) throw std::runtime_error(message); }
Point Place(Placement& p, int y, int height, PlacementEnvironment env, int caretHeight = 20, int x = 100) {
    Check(p.Acquire(x, y, caretHeight), "valid caret rejected");
    auto at = p.Resolve(200, height, env); ++positions;
    Check(at.x >= env.work.left && at.y >= env.work.top, "top/left escaped");
    if (std::int64_t(height) + 2 <= std::int64_t(env.work.bottom) - env.work.top)
        Check(std::int64_t(at.y) + height <= std::int64_t(env.work.bottom) - 2, "bottom escaped");
    return at;
}
}
int main() {
    try {
        PlacementEnvironment env; env.work = {0, 0, 1920, 1040}; env.revision = 1; env.instance = u"core-a";
        Placement p;
        Check(Place(p, 980, 20, env).y == 985 && !p.IsAbove(), "initial should be below");
        Check(Place(p, 980, 140, env).y == 815 && p.IsAbove(), "overflow should flip above");
        p.EndComposition();
        Check(Place(p, 980, 20, env).y == 935 && p.IsAbove(), "above memory lost across short compositions");
        for (int delta : {1, -1, 3, -3, 0}) {
            Check(Place(p, 980 + delta, 20, env).y == 935 + delta && p.IsAbove(), "jitter side or live coordinates incorrect");
        }
        for (int delta : {-1, -2, -3, -4}) Place(p, 980 + delta, 20, env);
        Check(!p.IsAbove(), "slow upward drift lost");
        Place(p, 980, 140, env); Place(p, 1000, 20, env);
        Check(p.IsAbove(), "downward motion lost above memory");
        Place(p, 996, 20, env); Check(!p.IsAbove(), "downward reference not advanced");
        Place(p, 980, 140, env); p.RefreshAnchor(); Place(p, 980, 20, env);
        Check(p.IsAbove(), "partial commit anchor refresh lost direction");
        Place(p, 976, 140, env); Check(p.IsAbove(), "upward movement forced below where it cannot fit");
        for (int change = 0; change < 12; ++change) {
            p.Reset(); Place(p, 980, 140, env); auto changed = env;
            switch (change) {
            case 0: ++changed.revision; break; case 1: changed.instance = u"core-b"; break;
            case 2: ++changed.owner; break; case 3: ++changed.foreground; break; case 4: ++changed.focus; break;
            case 5: ++changed.monitor; break; case 6: ++changed.processId; break; case 7: changed.dpi = 144; break;
            case 8: ++changed.work.bottom; break; case 9: ++changed.ownerBounds.top; break;
            case 10: ++changed.foregroundBounds.left; break; case 11: changed.uncertain = true; break;
            }
            Place(p, 980, 20, changed); Check(!p.IsAbove(), "environment change retained preference");
        }
        p.Reset(); Place(p, 980, 140, env);
        Check(!p.Acquire(0, 0, 20) && p.IsAbove(), "invalid caret polluted accepted memory");
        Place(p, 980, 20, env, 979); Check(!p.IsAbove(), "visibility failed to override preference");
        const WorkArea areas[] = {{0,0,1920,1040},{-1920,0,0,1040},{0,-1080,1920,-40},
            {-2560,-1440,0,-40},{1920,120,4480,1520},{0,48,1920,1080},{48,0,1920,1080},{0,0,1872,1080}};
        for (auto area : areas) for (unsigned dpi : {96u,120u,144u,192u}) {
            env.work = area; env.dpi = dpi; p.Reset();
            const int caretY = area.bottom - 80, caretHeight = 24 * dpi / 96;
            unsigned flips = 0, resetFlips = 0; bool side = false, resetSide = false;
            Placement resetEach;
            for (int word = 0; word < 100; ++word) {
                p.EndComposition(); resetEach.Reset();
                for (int height : {int(16*dpi/96), int(150*dpi/96)}) {
                    Place(p, caretY, height, env, caretHeight, area.left+100);
                    Place(resetEach, caretY, height, env, caretHeight, area.left+100);
                    if (p.IsAbove() != side) ++flips;
                    if (resetEach.IsAbove() != resetSide) ++resetFlips;
                    side = p.IsAbove(); resetSide = resetEach.IsAbove();
                }
            }
            Check(flips == 1 && resetFlips == 199, "word sequence did not eliminate repeated flips");
            const int epsilon = (dpi*3+48)/96;
            Place(p, caretY-epsilon, 10, env, caretHeight, area.left+100); Check(p.IsAbove(), "inclusive DPI jitter boundary lost");
            Place(p, caretY-epsilon-1, 10, env, caretHeight, area.left+100); Check(!p.IsAbove(), "DPI upward move not detected");
            p.Reset(); Place(p, caretY, 180, env, 8, area.left+100);
            Place(p, caretY-3, 10, env, 8, area.left+100); Check(!p.IsAbove(), "small caret tolerance exceeded quarter height");
            for (int height = 1; height < 2000; height += 7) Place(p, caretY, height, env, caretHeight, area.left+100);
        }
        std::mt19937 random(74021); env.dpi = 96;
        for (int i = 0; i < 100000; ++i) {
            const int x = int(random()%5000)-2500, y = int(random()%4000)-2000;
            env.work = {x,y,x+int(random()%3000)+1,y+int(random()%2000)+1};
            Place(p, y+int(random()%2000)+1, int(random()%2500)+1, env, int(random()%200)+1, x+1);
        }
        std::cout << "{\"status\":\"passed\",\"probe\":\"native_orientation\",\"positions\":" << positions
                  << ",\"checks\":" << checks << ",\"word_sequence_flips\":1,\"reset_control_flips\":199,\"physical_input_tested\":false}\n";
        return 0;
    } catch (const std::exception& e) { std::cerr << e.what() << '\n'; return 1; }
}
