#include "Model.h"
#include <algorithm>
#include <cmath>
#include <stdexcept>
#include <nlohmann/json.hpp>

namespace tiger::overlay
{
    Text FromUtf8(std::string_view value)
    {
        Text result;
        for (std::size_t i = 0; i < value.size();)
        {
            auto first = static_cast<unsigned char>(value[i++]);
            std::uint32_t code = first;
            int continuation = 0;
            if (first >= 0xc2 && first <= 0xdf) { code &= 31; continuation = 1; }
            else if (first >= 0xe0 && first <= 0xef) { code &= 15; continuation = 2; }
            else if (first >= 0xf0 && first <= 0xf4) { code &= 7; continuation = 3; }
            else if (first >= 0x80) throw std::invalid_argument("Invalid UTF-8");
            for (int j = 0; j < continuation; ++j)
            {
                if (i == value.size()) throw std::invalid_argument("Truncated UTF-8");
                auto next = static_cast<unsigned char>(value[i++]);
                if ((next & 0xc0) != 0x80) throw std::invalid_argument("Invalid UTF-8 continuation");
                code = (code << 6) | (next & 63);
            }
            if ((continuation == 1 && code < 0x80) || (continuation == 2 && code < 0x800) ||
                (continuation == 3 && code < 0x10000) || code > 0x10ffff ||
                (code >= 0xd800 && code <= 0xdfff)) throw std::invalid_argument("Invalid UTF-8 scalar");
            if (code < 0x10000) result += static_cast<char16_t>(code);
            else
            {
                code -= 0x10000;
                result += static_cast<char16_t>(0xd800 + (code >> 10));
                result += static_cast<char16_t>(0xdc00 + (code & 1023));
            }
        }
        return result;
    }

    std::string ToUtf8(std::u16string_view value)
    {
        std::string result;
        for (std::size_t i = 0; i < value.size(); ++i)
        {
            std::uint32_t code = value[i];
            if (code >= 0xd800 && code <= 0xdbff)
            {
                if (++i == value.size() || value[i] < 0xdc00 || value[i] > 0xdfff)
                    throw std::invalid_argument("Unpaired UTF-16 surrogate");
                code = 0x10000 + ((code - 0xd800) << 10) + value[i] - 0xdc00;
            }
            else if (code >= 0xdc00 && code <= 0xdfff) throw std::invalid_argument("Unpaired UTF-16 surrogate");
            if (code < 0x80) result += static_cast<char>(code);
            else
            {
                if (code < 0x800) result += static_cast<char>(0xc0 | (code >> 6));
                else
                {
                    if (code < 0x10000) result += static_cast<char>(0xe0 | (code >> 12));
                    else
                    {
                        result += static_cast<char>(0xf0 | (code >> 18));
                        result += static_cast<char>(0x80 | ((code >> 12) & 63));
                    }
                    result += static_cast<char>(0x80 | ((code >> 6) & 63));
                }
                result += static_cast<char>(0x80 | (code & 63));
            }
        }
        return result;
    }

    bool ParseState(std::string_view json, State& state) noexcept
    {
        try
        {
            if (json.size() > 128 * 1024 - 20) return false;
            auto obj = nlohmann::json::parse(json, [](int depth, nlohmann::json::parse_event_t, nlohmann::json&)
            {
                if (depth > 32) throw std::invalid_argument("JSON nesting too deep");
                return true;
            });
            if (!obj.is_object()) return false;
            State next;
            auto scalar = [&](const char* key, auto& target)
            {
                auto found = obj.find(key);
                if (found != obj.end() && !found->is_null()) found->get_to(target);
            };
            auto text = [&](const char* key, Text& target)
            {
                std::string raw; scalar(key, raw); target = FromUtf8(raw);
            };
            auto array = [&](const char* key, std::vector<Text>& target)
            {
                auto found = obj.find(key);
                if (found == obj.end() || found->is_null()) return;
                if (!found->is_array()) throw std::invalid_argument("Expected string array");
                for (const auto& entry : *found)
                    target.push_back(entry.is_null() ? Text{} : FromUtf8(entry.get<std::string>()));
            };
            scalar("IsOff", next.isOff); scalar("IsChinese", next.isChinese);
            scalar("CandidateVisible", next.candidateVisible); scalar("VerticalCandidates", next.vertical);
            scalar("ShowCandidateIndex", next.showIndex); scalar("HideCandidateItems", next.hideCandidates);
            scalar("HideStatusBar", next.hideStatus); scalar("ShowInputCodeInCandidateWindow", next.showCode);
            scalar("IsNativeHook", next.nativeHook); scalar("CompositionState", next.composition);
            scalar("CaretX", next.caretX); scalar("CaretY", next.caretY); scalar("CaretHeight", next.caretHeight);
            scalar("SoundVk", next.soundVk); scalar("SoundVolumePercent", next.soundVolume);
            scalar("CandidateExpandDelayMs", next.candidateDelay); scalar("AnnotationExpandDelayMs", next.annotationDelay);
            scalar("CandidateAnimationEnabled", next.animationEnabled);
            scalar("CandidateAnimationDurationMs", next.animationDurationMs);
            next.animationDurationMs = std::clamp(next.animationDurationMs, 0, 60000);
            scalar("SelectedCandidateIndex", next.selected); scalar("FontSize", next.fontSize);
            scalar("SoundSeq", next.soundSequence); scalar("CandidateAnchorRevision", next.anchorRevision);
            scalar("CandidateBackgroundUntil", next.backgroundUntil);
            scalar("CandidateEnvironmentRevision", next.environmentRevision);
            scalar("CandidateEnvironmentActive", next.environmentActive);
            scalar("CandidateOwnerHwnd", next.ownerHwnd);
            scalar("CandidateOwnerProcessId", next.ownerProcessId);
            text("CandidateEnvironmentId", next.environmentId);
            text("StatusText", next.status); text("InputCode", next.input); text("CodeMasking", next.codeMask);
            text("ThemeName", next.theme); text("FontName", next.font);
            array("Candidates", next.candidates); array("CandidateAnnotations", next.annotations);
            if (!std::isfinite(next.fontSize)) return false;
            state = std::move(next);
            return true;
        }
        catch (...) { return false; }
    }

    static Text Escape(std::u16string_view text)
    {
        Text result;
        for (std::size_t i = 0; i < text.size(); ++i)
        {
            auto ch = text[i];
            if (ch == u'\r')
            {
                if (i + 1 < text.size() && text[i + 1] == u'\n') ++i;
                result += u"\\n";
            }
            else if (ch == u'\n') result += u"\\n";
            else if (ch == u'\t') result += u"\\t";
            else result += ch;
        }
        return result;
    }

    DisplayMode ModeFor(const State& state, bool expanded)
    {
        if (!state.candidateVisible || (state.environmentRevision > 0 &&
            (!state.environmentActive || state.isOff || !state.isChinese))) return DisplayMode::Hidden;
        bool code = state.showCode || state.nativeHook;
        if (!expanded || (state.hideCandidates && state.candidateDelay <= 0))
            return code && !state.input.empty() ? DisplayMode::CodeOnly : DisplayMode::Hidden;
        if (state.candidates.empty())
            return state.input.empty() ? DisplayMode::Hidden : DisplayMode::InputOnly;
        return code && !state.input.empty() ? DisplayMode::CodeAndCandidates : DisplayMode::CandidatesOnly;
    }

    bool SameDisplayContent(const State& a, const State& b)
    {
        return (a.environmentRevision > 0) == (b.environmentRevision > 0) &&
            (a.environmentRevision <= 0 || (a.environmentActive == b.environmentActive &&
                a.isOff == b.isOff && a.isChinese == b.isChinese)) &&
            a.candidateVisible == b.candidateVisible && a.vertical == b.vertical && a.showIndex == b.showIndex &&
            a.hideCandidates == b.hideCandidates && a.showCode == b.showCode && a.nativeHook == b.nativeHook &&
            a.input == b.input && a.codeMask == b.codeMask && a.candidates == b.candidates && a.annotations == b.annotations &&
            a.selected == b.selected && a.composition == b.composition &&
            a.candidateDelay == b.candidateDelay && a.annotationDelay == b.annotationDelay;
    }

    Display Format(const State& state, bool expanded, bool annotations)
    {
        Display result;
        result.mode = ModeFor(state, expanded);
        if (result.mode == DisplayMode::Hidden) return result;
        if (result.mode == DisplayMode::CodeOnly || result.mode == DisplayMode::InputOnly)
        {
            result.text = Escape(state.input);
            return result;
        }
        if (result.mode == DisplayMode::CodeAndCandidates)
        {
            result.text = Escape(state.input);
            if (state.vertical) result.text += u'\n';
            else if (result.text.size() < 7) result.text.append(7 - result.text.size(), u' ');
        }
        for (std::size_t i = 0; i < state.candidates.size(); ++i)
        {
            if (i) result.text += state.vertical ? u"\n" : u"  ";
            auto start = result.text.size();
            if (state.showIndex) result.text += FromUtf8(std::to_string(i + 1)) + u" ";
            result.text += Escape(state.candidates[i]);
            if (annotations && i < state.annotations.size() && !state.annotations[i].empty())
                result.text += u"\u3014" + Escape(state.annotations[i]) + u"\u3015";
            if (state.selected == static_cast<int>(i) && !(state.composition == 5 && i == 0))
            {
                result.selectionStart = static_cast<int>(start);
                result.selectionLength = static_cast<int>(result.text.size() - start);
            }
        }
        return result;
    }

    Display Reveal::Update(const State& state, std::uint64_t now)
    {
        if (ModeFor(state) == DisplayMode::Hidden)
        {
            active_ = candidates_ = annotations_ = false;
            return {};
        }
        if (!active_) { active_ = true; started_ = now; }
        auto elapsed = now >= started_ ? now - started_ : 0;
        candidates_ |= state.candidates.empty() || elapsed >= static_cast<std::uint64_t>(std::max(0, state.candidateDelay));
        annotations_ |= std::none_of(state.annotations.begin(), state.annotations.end(), [](const Text& t) { return !t.empty(); }) ||
            elapsed >= static_cast<std::uint64_t>(std::max(0, state.annotationDelay));
        return Format(state, candidates_, candidates_ && annotations_);
    }

    std::uint32_t Reveal::NextDelay(const State& state, std::uint64_t now) const
    {
        if (!active_ || (candidates_ && annotations_)) return 0;
        auto elapsed = now >= started_ ? now - started_ : 0;
        auto remaining = [&](int delay)
        {
            auto duration = static_cast<std::uint64_t>(std::max(0, delay));
            return duration > elapsed ? duration - elapsed : 1;
        };
        auto delay = !candidates_ ? remaining(state.candidateDelay) : remaining(state.annotationDelay);
        if (!annotations_) delay = std::min(delay, remaining(state.annotationDelay));
        return static_cast<std::uint32_t>(delay);
    }

    Metrics MeasureStyle(const State& state, DisplayMode mode)
    {
        double font = std::clamp(state.fontSize > 0 && std::isfinite(state.fontSize) ? state.fontSize : 17.0, 3.0, 200.0);
        if (mode == DisplayMode::CodeOnly)
            return {font, std::ceil(font), 0, std::nearbyint(font * 0.4), 2, std::nearbyint(font * 0.4), 2};
        if (state.vertical) return {font, std::ceil(font * 1.5), std::ceil(font * 3.76 + 15), 12, 8, 8, 7};
        return {font, std::ceil(font), std::ceil(font * 2.88 + 15), 8, 8, 8, 8};
    }

    Palette Theme(std::u16string_view name)
    {
        if (name == u"\u901a\u900f") return {0xff2277ee, 0, 0, 0x482277ee, 1.25};
        if (name == u"\u4e00\u822c\u901a\u900f") return {0xff2277ee, 0x1a000000, 0, 0x482277ee, 1.25};
        if (name == u"\u8ff7\u96fe") return {0xffd9d9d9, 0xff2f2f2f, 0xff5a5a5a, 0x48d9d9d9, 1.25};
        if (name == u"\u661f\u591c") return {0xffffdc6a, 0xff232b39, 0xff3a6b9b, 0x48ffdc6a, 1.25};
        if (name == u"\u7eb8") return {0xff111111, 0xfff5f2e8, 0xffa8a09d, 0x48111111, 1.3};
        if (name == u"\u7c89") return {0xff000000, 0xfffdf9f5, 0xffdeacac, 0x48000000, 1.25};
        if (name == u"\u8d5b\u535a\u670b\u514b") return {0xff71e4fd, 0x88001122, 0xfff651fc, 0x4871e4fd, 1.5, true};
        if (name == u"\u6e05\u6668") return {0xff303030, 0xfffdfdff, 0xff56a1dd, 0x48303030, 1.25};
        return {0xff000000, 0xfffff8f3, 0xff1a7b6b, 0x48000000, 1.25};
    }
}
