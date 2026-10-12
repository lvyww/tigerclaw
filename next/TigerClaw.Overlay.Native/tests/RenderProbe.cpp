#include "Renderer.h"
#include <nlohmann/json.hpp>
#include <algorithm>
#include <cmath>
#include <fstream>
#include <iostream>
#include <vector>

namespace tiger::overlay
{
    class RendererProbe
    {
    public:
        static void ResetCounters(Renderer& renderer) { for (auto& n : renderer.creations_) n = 0; }
        static nlohmann::json Counters(const Renderer& r)
        {
            return {{"formats", r.creations_[0]}, {"layouts", r.creations_[1]}, {"dibs", r.creations_[2]}, {"targets", r.creations_[3]}};
        }
        static POINT CandidatePoint(const Renderer& r, int position)
        {
            FLOAT x = 0, y = 0; DWRITE_HIT_TEST_METRICS hit{};
            CheckHr(r.layout_->HitTestTextPosition(position, FALSE, &x, &y, &hit));
            return {static_cast<LONG>((r.textOrigin_.x + x + hit.width / 2) * r.textScale_),
                static_cast<LONG>((r.textOrigin_.y + y + hit.height / 2) * r.textScale_)};
        }
        static float FontSizeAt(const Renderer& r, int position)
        {
            float size = 0;
            CheckHr(r.layout_->GetFontSize(static_cast<UINT32>(position), &size));
            return size;
        }
        static bool NaturalLines(const Renderer& r)
        {
            DWRITE_LINE_SPACING_METHOD method; FLOAT height, baseline;
            CheckHr(r.format_->GetLineSpacing(&method, &height, &baseline));
            return method == DWRITE_LINE_SPACING_METHOD_DEFAULT;
        }
        static bool GrayCode(const Renderer& r, int position)
        {
            ComPtr<IUnknown> effect;
            CheckHr(r.layout_->GetDrawingEffect(position, effect.GetAddressOf()));
            ComPtr<ID2D1SolidColorBrush> brush;
            if (!effect || FAILED(effect.As(&brush))) return false;
            auto color = brush->GetColor();
            return color.a == 0;
        }
        static void VerifyVerticalRows(const Renderer& r, const Display& display, bool sentence)
        {
            for (std::size_t i = 0; i < display.candidateRanges.size(); ++i)
            {
                const auto range = display.candidateRanges[i];
                UINT32 count = 0;
                r.layout_->HitTestTextRange(range.first, range.second, 0, 0, nullptr, 0, &count);
                std::vector<DWRITE_HIT_TEST_METRICS> hits(count);
                CheckHr(r.layout_->HitTestTextRange(range.first, range.second, 0, 0, hits.data(), count, &count));
                float left = 300000.f, right = 0;
                for (const auto& hit : hits) { left = std::min(left, hit.left); right = std::max(right, hit.left + hit.width); }
                if (std::abs(left + right - r.layout_->GetMaxWidth()) > .02f)
                    throw std::runtime_error("Candidate group not horizontally centered");
                DWRITE_TEXT_METRICS body{};
                CheckHr(r.rowLayouts_[i]->GetMetrics(&body));
                bool hasNotes = std::any_of(r.annotationLayouts_.begin(), r.annotationLayouts_.end(),
                    [](const auto& item) { return item.Get() != nullptr; });
                if (hasNotes ? std::abs(body.left) > .02f : std::abs(2 * body.left + body.width - r.layout_->GetMaxWidth()) > .02f)
                    throw std::runtime_error("All candidate bodies must align left with notes, center without notes");
                if (r.annotationLayouts_[i])
                {
                    DWRITE_TEXT_METRICS note{}; CheckHr(r.annotationLayouts_[i]->GetMetrics(&note));
                    float noteSize = 0; CheckHr(r.annotationLayouts_[i]->GetFontSize(0, &noteSize));
                    if (std::abs(noteSize - 10.f) > .001f) throw std::runtime_error("Rendered annotation must use half configured size");
                    if (std::abs(note.left + note.width - r.layout_->GetMaxWidth()) > .02f || body.left + body.width + 3.9f > note.left)
                        throw std::runtime_error("Annotation must be right aligned without overlapping body");
                }
                const auto annotation = display.annotationRanges[i];
                if (annotation.first < 0) continue;
                if (std::abs(FontSizeAt(r, annotation.first) - 10.f) > .001f)
                    throw std::runtime_error("Annotation not half configured font size");
                ComPtr<IUnknown> effect; ComPtr<ID2D1SolidColorBrush> brush;
                CheckHr(r.layout_->GetDrawingEffect(annotation.first, effect.GetAddressOf()));
                if (!effect || FAILED(effect.As(&brush)) || std::abs(brush->GetColor().a - .7f) > .001f)
                    throw std::runtime_error("Annotation opacity is not 70 percent");
            }
            if (display.codeStart >= 0 && (!r.codeLayout_ || r.codeLayout_->GetTextAlignment() != DWRITE_TEXT_ALIGNMENT_CENTER))
                throw std::runtime_error("Footer must center across the entire content width");
        }
        static void VerifyIndexOpacity(const Renderer& r, const State& state, const Display& display)
        {
            for (std::size_t i = 1; i < display.candidateRanges.size(); ++i)
            {
                const auto range = display.candidateRanges[i]; const auto note = display.annotationRanges[i];
                UINT32 end = (note.first >= 0 ? note.first : range.first + range.second);
                auto layout = r.rowLayouts_.empty() ? r.layout_.Get() : r.rowLayouts_[i].Get();
                if (!r.rowLayouts_.empty()) end -= range.first;
                ComPtr<IUnknown> effect; ComPtr<ID2D1SolidColorBrush> brush;
                CheckHr(layout->GetDrawingEffect(end - 1, effect.GetAddressOf()));
                if (!effect || FAILED(effect.As(&brush)) || std::abs(brush->GetColor().a - .5f) > .001f)
                    throw std::runtime_error("Index opacity must be 50 percent");
            }
        }
        static void VerifyOrientationCache(const Renderer& r, bool vertical, bool inputOnly)
        {
            if (r.layoutVertical_ != vertical || (!inputOnly && r.rowLayouts_.empty()) ||
                (inputOnly && !r.codeLayout_))
                throw std::runtime_error("Same-text orientation switch must rebuild body/footer layouts");
        }
        static void LoseTarget(Renderer& r) { r.target_.Reset(); }
        static void SaveBitmap(const Renderer& r, const char* path)
        {
            DIBSECTION dib{};
            if (!GetObjectW(r.bitmap_, sizeof(dib), &dib) || !dib.dsBm.bmBits)
                throw std::runtime_error("Read preview DIB failed");
            BITMAPFILEHEADER file{};
            file.bfType = 0x4d42;
            file.bfOffBits = sizeof(file) + sizeof(BITMAPINFOHEADER);
            auto info = dib.dsBmih;
            info.biHeight = -r.size_.cy; // Renderer owns a top-down DIB.
            info.biSizeImage = dib.dsBm.bmWidthBytes * r.size_.cy;
            file.bfSize = file.bfOffBits + info.biSizeImage;
            std::ofstream output(path, std::ios::binary);
            output.write(reinterpret_cast<const char*>(&file), sizeof(file));
            output.write(reinterpret_cast<const char*>(&info), sizeof(info));
            output.write(static_cast<const char*>(dib.dsBm.bmBits), info.biSizeImage);
            if (!output) throw std::runtime_error("Write preview failed");
        }
        static void IncompleteFrame(Renderer& r) { r.frameReady_ = false; }
        static std::uint64_t Pixels(const Renderer& r)
        {
            DIBSECTION dib{};
            if (!GetObjectW(r.bitmap_, sizeof(dib), &dib) || !dib.dsBm.bmBits)
                throw std::runtime_error("Read test DIB failed");
            auto bytes = static_cast<const unsigned char*>(dib.dsBm.bmBits);
            std::uint64_t hash = 14695981039346656037ull;
            for (int y = 0; y < r.size_.cy; ++y)
                for (int x = 0; x < r.size_.cx * 4; ++x)
                    hash = (hash ^ bytes[y * dib.dsBm.bmWidthBytes + x]) * 1099511628211ull;
            return hash;
        }
    };
}
using namespace tiger::overlay;
static long long Now() { LARGE_INTEGER n{}; QueryPerformanceCounter(&n); return n.QuadPart; }
static State Base()
{
    State state;
    state.candidateVisible = state.isChinese = state.showIndex = true; state.candidateLayout = 1;
    state.font = u"Microsoft YaHei"; state.fontSize = 20; state.input = u"lets";
    state.candidates = {u"\u800c\u4e09", u"\u65cb", u"\u800c\u758b", u"\U00020000"};
    state.annotations = {u"le ts", u"lets", u"le ts", u"Unicode"};
    return state;
}
int main(int argc, char** argv)
{
    HWND window = nullptr;
    HRESULT com = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    try
    {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        window = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            L"STATIC", L"TigerClaw isolated render probe", WS_POPUP, 0, 0, 1, 1, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
        if (!window) throw std::runtime_error("Test window creation failed");
        LARGE_INTEGER frequency{}; QueryPerformanceFrequency(&frequency);
        nlohmann::json result;
        bool verify = argc > 1 && std::string(argv[1]) == "--verify";
        if (argc > 1 && std::string(argv[1]) == "--superscript-preview")
        {
            Renderer renderer(L".");
            auto state = Base();
            state.candidates = {u"\u5019\u9009\u8bcd", u"\u8f93\u5165\u6cd5", u"Unicode"};
            state.annotations = {u"abc", u"def", u"ghi"};
            state.selected = 1;
            for (int layout : {3, 4, 2})
            {
                state.candidateLayout = layout;
                auto size = renderer.Prepare(state, Format(state), 144);
                RendererProbe::VerifyIndexOpacity(renderer, state, Format(state));
                RendererProbe::SaveBitmap(renderer, layout == 3 ? "superscript-horizontal.bmp" : layout == 4 ? "superscript-vertical.bmp" : "vertical-code-below.bmp");
                if (layout != 3)
                {
                    auto flipped = renderer.Prepare(state, OrientDisplay(state, Format(state), true), 144);
                    if (size.cx != flipped.cx || size.cy != flipped.cy) throw std::runtime_error("Orientation changed measured size");
                    RendererProbe::SaveBitmap(renderer, layout == 4 ? "vertical-above.bmp" : "vertical-code-above.bmp");
                }
            }
            result["superscript_preview"] = "written";
        }
        else if (argc > 1 && std::string(argv[1]) == "--pinyin-hit-check")
        {
            Renderer renderer(L".");
            auto state = Base(); state.composition = 6;
            for (bool vertical : {false, true}) for (bool above : {false, true}) for (UINT dpi : {96u, 144u, 192u})
            {
                state.candidateLayout = (vertical) ? (state.showCode() ? 2 : 4) : (state.showCode() ? 1 : 3);
                auto display = OrientDisplay(state, Format(state), above);
                renderer.Prepare(state, display, dpi);
                for (int i = 0; i < static_cast<int>(display.candidateRanges.size()); ++i)
                    if (renderer.HitCandidate(RendererProbe::CandidatePoint(renderer, display.candidateRanges[i].first), display) != i)
                        throw std::runtime_error("Candidate hit test selected a different item");
                if (renderer.HitCandidate(POINT{-10, -10}, display) != -1) throw std::runtime_error("Outside click selected an item");
            }
            result["pinyin_hit_checks"] = "passed";
        }
        else if (argc > 1 && std::string(argv[1]) == "--present-check")
        {
            Renderer renderer(L".");
            auto state = Base();
            // Same text across mode switches must invalidate font-range caching.
            state.showIndex = false; state.composition = 5;
            for (bool vertical : {false, true}) for (bool above : {false, true})
            {
                state.candidateLayout = (vertical) ? (state.showCode() ? 2 : 4) : (state.showCode() ? 1 : 3);
                auto display = OrientDisplay(state, Format(state), above);
                renderer.Prepare(state, display, 144);
                if (std::abs(RendererProbe::FontSizeAt(renderer, display.candidateRanges[0].first) - 20.f) > .001f ||
                    std::abs(RendererProbe::FontSizeAt(renderer, display.candidateRanges[1].first) - 14.f) > .001f)
                    throw std::runtime_error("Sentence candidate font ratio is not 0.7");
                if (vertical && (!RendererProbe::NaturalLines(renderer) || display.codeStart < 0 ||
                    std::abs(RendererProbe::FontSizeAt(renderer, display.codeStart) - 13.f) > .001f ||
                    !RendererProbe::GrayCode(renderer, display.codeStart)))
                    throw std::runtime_error("Natural line spacing / 65-percent footer mismatch");
                if (vertical) RendererProbe::VerifyVerticalRows(renderer, display, true);
                for (const auto& note : display.annotationRanges) if (note.first >= 0)
                    if (std::abs(RendererProbe::FontSizeAt(renderer, note.first) - 10.f) > .001f)
                        throw std::runtime_error("Both orientations require 50 percent annotation size");
                if (display.codeStart >= 0 && std::abs(RendererProbe::FontSizeAt(renderer, display.codeStart) - 13.f) > .001f)
                    throw std::runtime_error("Both orientations require 65 percent code size");
                state.composition = 2;
                auto ordinary = OrientDisplay(state, Format(state), above);
                renderer.Prepare(state, ordinary, 144);
                if (std::abs(RendererProbe::FontSizeAt(renderer, ordinary.candidateRanges[1].first) - 14.f) > .001f)
                    throw std::runtime_error("Ordinary secondary candidate font ratio mismatch");
                if (vertical) RendererProbe::VerifyVerticalRows(renderer, ordinary, false);
                state.composition = 5;
            }
            state = Base(); state.candidateLayout = 2;
            // Includes a row without notes and deferred annotation reveal on a reused renderer.
            state.annotations = {u"note", u"", u"decomposition", u""};
            for (bool above : {false, true}) for (bool notes : {true, false, true})
            {
                auto display = OrientDisplay(state, Format(state, true, notes), above);
                renderer.Prepare(state, display, 144);
                RendererProbe::VerifyVerticalRows(renderer, display, false);
            }
            state = Base(); state.candidateLayout = 2;
            renderer.Prepare(state, Format(state), 144);
            auto previousThemePixels = RendererProbe::Pixels(renderer);
            for (const auto* theme : {u"\u8ff7\u96fe", u"\u661f\u591c", u"\u901a\u900f", u"\u7eb8"})
            {
                state.theme = theme;
                renderer.Prepare(state, Format(state), 144);
                auto themedPixels = RendererProbe::Pixels(renderer);
                if (themedPixels == previousThemePixels) throw std::runtime_error("Vertical theme change did not repaint");
                previousThemePixels = themedPixels;
            }
            state = Base(); state.annotations.clear(); state.input.clear(); state.candidates = {u"word"};
            for (bool inputOnly : {false, true})
            {
                if (inputOnly) { state.candidates.clear(); state.input = u"code"; }
                for (int mode : {3,4,3,4})
                {
                    state.candidateLayout = mode;
                    renderer.Prepare(state, Format(state), 144);
                    RendererProbe::VerifyOrientationCache(renderer, mode == 4, inputOnly);
                }
            }
            state = Base();
            renderer.Render(window, state, Format(state), 144);
            RECT before{}; GetWindowRect(window, &before);
            state.input.append(40, u'a');
            auto size = renderer.Prepare(state, Format(state), 144);
            RECT prepared{}; GetWindowRect(window, &prepared);
            if (!EqualRect(&before, &prepared)) throw std::runtime_error("Prepare changed live geometry");
            RendererProbe::IncompleteFrame(renderer);
            bool rejected = false;
            try { renderer.Present(window); } catch (...) { rejected = true; }
            if (!rejected) throw std::runtime_error("Incomplete frame was published");
            GetWindowRect(window, &prepared);
            if (!EqualRect(&before, &prepared)) throw std::runtime_error("Failed frame changed live geometry");
            size = renderer.Prepare(state, Format(state), 144);
            POINT destination{137, 219};
            renderer.Present(window, &destination);
            RECT after{}; GetWindowRect(window, &after);
            if (after.left != destination.x || after.top != destination.y ||
                after.right - after.left != size.cx || after.bottom - after.top != size.cy)
                throw std::runtime_error("Combined frame/size/position publication failed");
            if (IsWindowVisible(window)) throw std::runtime_error("Prepare/Present unexpectedly showed hidden window");
            SIZE frameSize{320, 160};
            renderer.Prepare(state, Format(state), 144, false, &frameSize);
            auto framePixels = RendererProbe::Pixels(renderer);
            state.input = u"different text"; state.selected = 2;
            renderer.Prepare(state, Format(state), 144, false, &frameSize);
            if (framePixels == RendererProbe::Pixels(renderer))
                throw std::runtime_error("Transition frame failed to update candidate text");
            auto textPixels = RendererProbe::Pixels(renderer);
            state.selected = 0;
            renderer.Prepare(state, Format(state), 144, false, &frameSize);
            if (textPixels == RendererProbe::Pixels(renderer))
                throw std::runtime_error("Transition frame failed to update selection");
            for (SIZE thin : {SIZE{1, 100}, SIZE{100, 1}})
            {
                renderer.Prepare(state, Format(state), 144, false, &thin);
                renderer.Present(window);
            }
            result["present_checks"] = "passed";
        }
        else if (argc > 1 && std::string(argv[1]) == "--cache-check")
        {
            Renderer renderer(L".");
            auto state = Base();
            auto draw = [&](UINT dpi = 144, bool status = false)
            { renderer.Render(window, state, Format(state), dpi, status); };
            auto check = [&](const char* counter, std::uint64_t expected)
            {
                if (RendererProbe::Counters(renderer).at(counter).get<std::uint64_t>() != expected)
                    throw std::runtime_error(std::string("Unexpected cache count: ") + counter);
            };
            draw();
            state.selected = 2; draw();
            state.theme = u"\u7eb8"; draw();
            draw(192);
            check("formats", 1); check("layouts", 1); check("targets", 1);
            state.input += u"abcdefghijklmnopqrstuvwxyz"; draw();
            check("formats", 1); check("layouts", 2); check("targets", 1);
            auto pixels = RendererProbe::Pixels(renderer);
            RendererProbe::LoseTarget(renderer); draw();
            check("targets", 2); check("layouts", 2);
            if (pixels != RendererProbe::Pixels(renderer)) throw std::runtime_error("Target recreation changed pixels");
            state.fontSize = 30; draw(); check("formats", 2); check("layouts", 3);
            state.font = u"Consolas"; draw(); check("formats", 3);
            state.candidateLayout = state.showCode() ? 2 : 4; draw(); check("formats", 4);
            draw(144, true); check("formats", 5);
            draw(192, true); check("formats", 5);
            result["cache_checks"] = "passed";
        }
        else if (verify)
        {
            Renderer renderer(L".");
            const std::vector<Text> themes{u"", u"\u901a\u900f", u"\u4e00\u822c\u901a\u900f", u"\u8ff7\u96fe",
                u"\u661f\u591c", u"\u7eb8", u"\u7c89", u"\u8d5b\u535a\u670b\u514b", u"\u6e05\u6668"};
            int id = 0;
            for (UINT dpi : {96u, 120u, 144u, 192u})
                for (const auto& theme : themes)
                    for (int variant = 0; variant < 12; ++variant)
                    {
                        auto state = Base(); state.theme = theme;
                        state.candidateLayout = (variant % 2) ? (state.showCode() ? 2 : 4) : (state.showCode() ? 1 : 3); state.selected = variant % 5 - 1;
                        state.fontSize = variant == 6 ? 31.5 : variant == 7 ? 12 : 20;
                        if (variant == 2) state.font = u"Consolas";
                        if (variant == 3) state.font = u"#missing-private-font";
                        if (variant == 4) state.input = u"longer_input_abcdefghijk";
                        if (variant == 5) { state.candidateLayout = 5; state.input = u"a"; }
                        bool status = variant >= 8;
                        state.isChinese = variant != 9; state.nativeHook = variant == 10; state.isOff = variant == 11;
                        auto display = Format(state);
                        if (id % 13 == 0) RendererProbe::LoseTarget(renderer);
                        auto size = renderer.Render(window, state, display, dpi, status);
                        auto pixels = RendererProbe::Pixels(renderer);
                        // Same input again must exercise cache hits with identical pixels.
                        auto repeat = renderer.Render(window, state, display, dpi, status);
                        if (size.cx != repeat.cx || size.cy != repeat.cy || pixels != RendererProbe::Pixels(renderer))
                            throw std::runtime_error("Repeated render changed output");
                        result["frames"].push_back({{"id", id++}, {"width", size.cx}, {"height", size.cy}, {"hash", pixels}});
                    }
            result["creations"] = RendererProbe::Counters(renderer);
        }
        else
        {
            for (const std::string workload : {"selection", "typing", "resize", "theme"})
            {
                Renderer renderer(L".");
                std::vector<double> samples;
                for (int i = -50; i < 1000; ++i)
                {
                    auto state = Base();
                    if (workload == "selection") state.selected = (i + 50) % 4;
                    if (workload == "typing") state.input = FromUtf8("code" + std::to_string(i + 1050));
                    if (workload == "resize") state.input.assign(1 + (i + 50) % 32, u'a');
                    if (workload == "theme") state.theme = i % 2 ? u"\u7eb8" : u"\u8ff7\u96fe";
                    auto display = Format(state);
                    if (i == 0) RendererProbe::ResetCounters(renderer);
                    auto before = Now();
                    renderer.Render(window, state, display, 144);
                    auto after = Now();
                    if (i >= 0) samples.push_back((after - before) * 1000.0 / frequency.QuadPart);
                }
                result[workload] = {{"ms", samples}, {"creations", RendererProbe::Counters(renderer)}};
            }
        }
        DestroyWindow(window); window = nullptr;
        if (SUCCEEDED(com)) CoUninitialize();
        std::cout << result.dump() << '\n';
        return 0;
    }
    catch (const std::exception& e)
    {
        if (window) DestroyWindow(window);
        if (SUCCEEDED(com)) CoUninitialize();
        std::cerr << e.what() << '\n'; return 1;
    }
}
