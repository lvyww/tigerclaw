// Adapted from lvyww/tigirl (local ime checkout), GPL-3.0.
// Source identities and C# parity coverage are recorded in README.md.
#pragma once
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <random>
#include <tuple>
#include <sstream>
#include <string>
#include <string_view>
#include <vector>
#include <set>
#include "CodeCase.h"
namespace tiger::core {
inline std::int64_t learningNow() {
    return std::chrono::duration_cast<std::chrono::seconds>(std::chrono::system_clock::now().time_since_epoch()).count();
}
inline std::u16string learningConfigurationHash(std::u16string_view text) {
    std::uint64_t hash=14695981039346656037ull;
    for(auto c:text){hash^=c&255;hash*=1099511628211ull;hash^=c>>8;hash*=1099511628211ull;}
    std::u16string result(16,u'0');constexpr char16_t digits[]=u"0123456789abcdef";
    for(int i=15;i>=0;--i){result[i]=digits[hash&15];hash>>=4;}return result;
}
inline std::string learningId() {
    static const auto nonce=[] {
        std::ostringstream s;
        s<<std::hex<<static_cast<std::uint64_t>(std::chrono::high_resolution_clock::now().time_since_epoch().count())
         ;try{std::random_device r;s<<r()<<r();}catch(...){s<<reinterpret_cast<std::uintptr_t>(&s);}
        return s.str();
    }();
    static std::atomic<std::uint64_t> sequence{0};
    return nonce+"-"+std::to_string(++sequence);
}
// Returns 0 for malformed UTF-16. Offsets below are UTF-16 offsets; length
// limits/context suffixes are Unicode scalar counts, never surrogate halves.
inline std::size_t learningCharacters(std::u16string_view text) {
    std::size_t count=0;
    for(std::size_t i=0;i<text.size();++i,++count) {
        auto c=text[i];
        if(c>=0xd800 && c<=0xdbff) {
            if(++i>=text.size() || text[i]<0xdc00 || text[i]>0xdfff)return 0;
        }else if(c>=0xdc00 && c<=0xdfff)return 0;
    }
    return count;
}
inline std::u16string learningContext(std::u16string_view prefix) {
    auto start=prefix.size();
    for(int n=0;n<2 && start;n++) {
        --start;if(prefix[start]>=0xdc00 && prefix[start]<=0xdfff && start)--start;
    }
    return std::u16string(prefix.substr(start));
}
inline bool learningStaticText(std::u16string_view text) {
    auto n=learningCharacters(text);if(!n || n>16)return false;
    for(auto c:text) {
        // Commands/dynamic expansions and packed display aliases are never learnt.
        if(c<32 || c==127 || c==u'{' || c==u'}' || c==u'\t' ||
           (c>=0xe000 && c<=0xf8ff))return false;
    }
    return true;
}
struct SentenceLearningEvent {
    std::string id;
    std::int64_t time=0;
    std::u16string mode,code,text,context;
    int rawStart=0,rawEnd=0,textStart=0,textEnd=0;
    int levels=1; // One actual correction, a bounded 1..3-level ranking increment.
};
struct SentenceLearningBoundary {int raw=0,text=0;};
// Compare only intervals delimited by raw boundaries shared by BOTH paths.
// No substring-to-code guesses and no learning of unchanged sentence prefixes.
inline std::vector<SentenceLearningEvent> sentenceLearningDiff(
    std::u16string_view raw,std::u16string_view before,std::u16string_view selected,
    const std::vector<SentenceLearningBoundary>& a,const std::vector<SentenceLearningBoundary>& b,
    int floorRaw) {
    std::vector<SentenceLearningEvent> result;
    if(before==selected || a.empty() || b.empty())return result;
    std::map<int,int> left,right;
    left[0]=right[0]=0;
    auto fill=[&](auto& map,const auto& values,std::u16string_view text) {
        int oldRaw=0,oldText=0;
        for(auto p:values) {
            if(p.raw<=oldRaw || p.text<=oldText || p.raw>static_cast<int>(raw.size()) || p.text>static_cast<int>(text.size()))return false;
            if(!learningCharacters(text.substr(oldText,p.text-oldText)))return false;
            map[p.raw]=p.text;oldRaw=p.raw;oldText=p.text;
        }
        return oldRaw==static_cast<int>(raw.size()) && oldText==static_cast<int>(text.size());
    };
    if(!fill(left,a,before) || !fill(right,b,selected))return result;
    int previous=0;
    for(const auto& edge:left) {
        int end=edge.first;
        if(!end || !right.count(end))continue;
        auto first=right[previous],last=right[end];
        auto chosen=selected.substr(first,last-first);
        auto old=before.substr(left[previous],edge.second-left[previous]);
        if(previous>=floorRaw && chosen!=old && learningStaticText(chosen)) {
            SentenceLearningEvent event;event.id=learningId();event.time=learningNow();
            event.code=raw.substr(previous,end-previous);
            event.code = LowerInvariant(event.code);
            event.text=chosen;event.context=learningContext(selected.substr(0,first));
            event.rawStart=previous;event.rawEnd=end;event.textStart=first;event.textEnd=last;
            result.push_back(std::move(event));
        }
        previous=end;
    }
    return result;
}
// Retired candidate-pair records are neither score evidence nor active history.
// Keep their journal bytes untouched, but exclude them before replay/windowing.
inline bool learningLegacyPairMode(std::u16string_view mode) {
    return mode.substr(0,std::u16string_view(u"fusion-v1|").size())==u"fusion-v1|" ||
           mode.substr(0,std::u16string_view(u"exact-correction-v1|").size())==u"exact-correction-v1|";
}

class SentenceLearningAccumulator;
class SentenceLearningSnapshot {
    friend class SentenceLearningAccumulator;
    using ContextScores=std::map<std::u16string,double,std::less<>>;
    struct Scores {
        ContextScores exact;
        double general=0,confidenceGeneral=0;
        ContextScores weights,confidenceExact;
        double forContext(std::u16string_view context) const {
            const auto it=exact.find(context);
            return it==exact.end()?general:std::max(general,it->second);
        }
    };
    using TextScores=std::map<std::u16string,Scores,std::less<>>;
    using ModeScores=std::map<std::u16string,TextScores,std::less<>>;
    // Immutable query index: each text occurs once per (code, mode), regardless
    // of the number of contexts/events. Transparent lookups allocate no keys.
    std::map<std::u16string,std::shared_ptr<const ModeScores>,std::less<>> byCode_, prefixes_;
    static std::shared_ptr<const ModeScores> BuildPrefixes(const ModeScores& modes) {
        auto result = std::make_shared<ModeScores>();
        for (const auto& [mode, texts] : modes) for (const auto& [text, scores] : texts)
            for (std::size_t length = 1; length < text.size(); ++length) {
                auto& prefix = (*result)[mode][text.substr(0, length)];
                prefix.general = std::max(prefix.general, scores.general);
                for (const auto& [context, score] : scores.exact)
                    prefix.exact[context] = std::max(prefix.exact[context], score);
            }
        return result;
    }
public:
    bool empty()const{return byCode_.empty();}
    static constexpr int maximumCorrectionLevel=10;
    static int correctionLevel(double weight) {
        return std::clamp(static_cast<int>(std::floor(weight+1e-12)),0,maximumCorrectionLevel);
    }
    static double generalScore(double weight) {
        const int level=correctionLevel(weight);
        return level?4+2*level:0; // L1=6 ... L10=24.
    }
    static double exactScore(double weight) {
        const int level=correctionLevel(weight);
        return level?7+2*level:0; // Same-context protection: L1=9 ... L10=27.
    }
    static std::shared_ptr<const SentenceLearningSnapshot> build(const std::vector<SentenceLearningEvent>& events,std::int64_t now=learningNow()) {
        (void)now; // Timestamps are journal metadata only; learning never decays with time.
        struct Choice {double weight=0,confirmed=0;};
        using Key=std::tuple<std::u16string,std::u16string,std::u16string>;
        std::map<Key,std::map<std::u16string,Choice>> groups;
        for(const auto& event:events) {
            if(learningLegacyPairMode(event.mode) || event.mode.empty() || event.mode.size()>512 || event.code.empty() || event.code.size()>128 || !learningStaticText(event.text) ||
               (!event.context.empty() && (!learningCharacters(event.context) || learningCharacters(event.context)>2)) || event.levels<1 || event.levels>3)continue;
            auto& choices=groups[{event.code,event.mode,event.context}];
            // Only explicit manual corrections are persisted. A competing manual
            // correction weakens the old local choice; elapsed time never does.
            for(auto& entry:choices)if(entry.first!=event.text){entry.second.weight*=0.25;entry.second.confirmed*=0.25;}
            auto& target=choices.try_emplace(event.text,Choice{}).first->second;
            target.weight=std::min(static_cast<double>(maximumCorrectionLevel),target.weight+event.levels);
            target.confirmed=std::min(static_cast<double>(maximumCorrectionLevel),target.confirmed+1);
        }
        struct Summary {ContextScores exact,weights,confidenceExact;double weight=0,confirmed=0;};
        std::map<Key,Summary> summaries;
        for(const auto& group:groups) {
            const auto& code=std::get<0>(group.first);
            const auto& mode=std::get<1>(group.first);
            const auto& context=std::get<2>(group.first);
            for(const auto& entry:group.second) {
                auto& summary=summaries[{code,mode,entry.first}];
                summary.exact[context]=exactScore(entry.second.weight);
                summary.weight+=entry.second.weight;
                summary.confirmed+=entry.second.confirmed;
                summary.weights[context]=entry.second.weight;
                summary.confidenceExact[context]=SentenceLearningSnapshot::exactScore(entry.second.confirmed);
            }
        }
        auto snapshot=std::make_shared<SentenceLearningSnapshot>();
        std::map<std::u16string,std::shared_ptr<ModeScores>> partitions;
        for(auto& entry:summaries) {
            const auto& code=std::get<0>(entry.first);
            const auto& mode=std::get<1>(entry.first);
            const auto& text=std::get<2>(entry.first);
            auto& summary=entry.second;
            auto& partition=partitions[code];if(!partition)partition=std::make_shared<ModeScores>();
            auto& scores=(*partition)[mode][text];
            scores.general=generalScore(summary.weight);
            scores.confidenceGeneral=generalScore(summary.confirmed);
            scores.weights=std::move(summary.weights);scores.confidenceExact=std::move(summary.confidenceExact);
            scores.exact=std::move(summary.exact);
        }
        for(auto& partition:partitions) {
            snapshot->prefixes_[partition.first] = BuildPrefixes(*partition.second);
            snapshot->byCode_.emplace(partition.first,std::move(partition.second));
        }
        return snapshot;
    }
    // Bounded lookup for an unfinished, previously learnt fragment. This is
    // ONLY a beam-retention hint, never a final-score or confidence reward.
    double prefixScore(std::u16string_view mode,std::u16string_view code,std::u16string_view text,std::u16string_view context) const {
        if(code.empty() || text.empty())return 0;
        double result=0;unsigned checked=0;
        for(auto it=byCode_.lower_bound(code);it!=byCode_.end() && checked<64;++it,++checked) {
            if(std::u16string_view(it->first).substr(0,code.size())!=code)break;
            if(it->first.size()<=code.size())continue;
            const auto partition = prefixes_.find(it->first);
            if (partition == prefixes_.end()) continue;
            const auto modes = partition->second->find(mode);
            if (modes == partition->second->end()) continue;
            const auto prefix = modes->second.find(text);
            if (prefix != modes->second.end()) result = std::max(result, prefix->second.forContext(context));
        }
        return result;
    }
    double confidenceScore(std::u16string_view mode,std::u16string_view code,std::u16string_view text,std::u16string_view context) const {
        const auto codes=byCode_.find(code);if(codes==byCode_.end())return 0;
        const auto modes=codes->second->find(mode);if(modes==codes->second->end())return 0;
        const auto texts=modes->second.find(text);if(texts==modes->second.end())return 0;
        const auto& scores=texts->second;const auto exact=scores.confidenceExact.find(context);
        return std::max(scores.confidenceGeneral,exact==scores.confidenceExact.end()?0.0:exact->second);
    }
    double projectedScore(std::u16string_view mode,std::u16string_view code,std::u16string_view text,
        std::u16string_view context,const std::vector<SentenceLearningEvent>& events,int levels) const {
        if(std::none_of(events.begin(),events.end(),[&](const auto& e){return e.mode==mode && e.code==code;}))return score(mode,code,text,context);
        ContextScores weights;
        const auto codes=byCode_.find(code);
        if(codes!=byCode_.end()) {
            const auto modes=codes->second->find(mode);
            if(modes!=codes->second->end()) {
                const auto texts=modes->second.find(text);
                if(texts!=modes->second.end())weights=texts->second.weights;
            }
        }
        for(const auto& e:events) {
            if(e.mode!=mode || e.code!=code)continue;
            auto found=weights.find(e.context);
            if(e.text==text)weights[e.context]=std::min(static_cast<double>(maximumCorrectionLevel),(found==weights.end()?0:found->second)+levels);
            else if(found!=weights.end())found->second*=0.25;
        }
        double total=0;for(const auto& e:weights)total+=e.second;
        const auto found=weights.find(context);
        return std::max(generalScore(total),exactScore(found==weights.end()?0:found->second));
    }
    double score(std::u16string_view mode,std::u16string_view code,std::u16string_view text,std::u16string_view context) const {
        const auto codes=byCode_.find(code);if(codes==byCode_.end())return 0;
        const auto modes=codes->second->find(mode);if(modes==codes->second->end())return 0;
        const auto texts=modes->second.find(text);if(texts==modes->second.end())return 0;
        return texts->second.forContext(context);
    }
};

inline double sentenceLearningProjectedReward(std::u16string_view raw,std::u16string_view text,
    const std::vector<SentenceLearningBoundary>& boundaries,const std::vector<SentenceLearningEvent>& events,
    const SentenceLearningSnapshot& snapshot,int levels) {
    std::vector<SentenceLearningBoundary> points{{0,0}};
    for(auto p:boundaries) {
        if(p.raw<=points.back().raw || p.text<=points.back().text || p.raw>static_cast<int>(raw.size()) || p.text>static_cast<int>(text.size()))return 0;
        points.push_back(p);
    }
    if(points.back().raw!=static_cast<int>(raw.size()) || points.back().text!=static_cast<int>(text.size()))return 0;
    std::vector<double> best(points.size());
    for(std::size_t end=1;end<points.size();++end) {
        best[end]=best[end-1];
        for(std::size_t start=end;start-->0;) {
            const auto fragment=text.substr(points[start].text,points[end].text-points[start].text);
            if(learningCharacters(fragment)>16)break;
            auto code=std::u16string(raw.substr(points[start].raw,points[end].raw-points[start].raw));
            code = LowerInvariant(code);
            const auto context=learningContext(text.substr(0,points[start].text));
            best[end]=std::max(best[end],best[start]+snapshot.projectedScore(events.front().mode,code,fragment,context,events,levels));
        }
    }
    return best.back();
}
inline void sentenceLearningPlanLevels(std::vector<SentenceLearningEvent>& events,
    const std::shared_ptr<const SentenceLearningSnapshot>& snapshot,std::u16string_view raw,
    std::u16string_view before,std::u16string_view selected,
    const std::vector<SentenceLearningBoundary>& a,const std::vector<SentenceLearningBoundary>& b,
    double beforeBase,double selectedBase,bool beforeIsCorrected=false) {
    if(events.empty())return;
    std::set<std::tuple<std::u16string,std::u16string,std::u16string,std::u16string>> seen;
    events.erase(std::remove_if(events.begin(),events.end(),[&](const auto& e){return !seen.emplace(e.mode,e.code,e.text,e.context).second;}),events.end());
    const SentenceLearningSnapshot empty;const auto& state=snapshot?*snapshot:empty;
    int levels=1;
    if(std::isfinite(beforeBase) && std::isfinite(selectedBase))for(;levels<3;++levels) {
        const auto left=beforeBase+(beforeIsCorrected?0:sentenceLearningProjectedReward(raw,before,a,events,state,levels));
        const auto right=selectedBase+sentenceLearningProjectedReward(raw,selected,b,events,state,levels);
        if(right>=left+1)break;
    }
    for(auto& e:events)e.levels=levels;
}

inline std::vector<SentenceLearningEvent> sentenceLearningReinforceExisting(
    std::u16string_view raw,std::u16string_view before,std::u16string_view selected,
    const std::vector<SentenceLearningBoundary>& a,const std::vector<SentenceLearningBoundary>& b,
    int floorRaw,std::u16string_view mode,const std::shared_ptr<const SentenceLearningSnapshot>& snapshot,
    const std::function<bool(std::u16string_view)>& supplemental={}) {
    std::vector<SentenceLearningEvent> result;
    if(((!snapshot || snapshot->empty()) && !supplemental) || mode.empty() || before==selected || a.empty() || b.empty())return result;
    std::map<int,int> left,right;left[0]=right[0]=0;
    auto fill=[&](auto& map,const auto& values,std::u16string_view text) {
        int oldRaw=0,oldText=0;
        for(auto p:values) {
            if(p.raw<=oldRaw || p.text<=oldText || p.raw>static_cast<int>(raw.size()) || p.text>static_cast<int>(text.size()))return false;
            if(!learningCharacters(text.substr(oldText,p.text-oldText)))return false;
            map[p.raw]=p.text;oldRaw=p.raw;oldText=p.text;
        }
        return oldRaw==static_cast<int>(raw.size()) && oldText==static_cast<int>(text.size());
    };
    if(!fill(left,a,before) || !fill(right,b,selected))return result;
    struct Match {int rawStart,rawEnd,textStart,textEnd,characters;std::u16string code,text,context;};
    int previous=0;
    for(const auto& edge:left) {
        const int end=edge.first;
        if(!end || !right.count(end))continue;
        const int changedStart=right[previous],changedEnd=right[end];
        const auto changed=selected.substr(changedStart,changedEnd-changedStart);
        const auto old=before.substr(left[previous],edge.second-left[previous]);
        if(previous>=floorRaw && changed!=old) {
            std::vector<Match> matches;
            for(auto start=right.lower_bound(previous);start!=right.end() && start->first<end;++start) {
                if(start->first<previous)continue;
                auto finish=start;++finish;
                for(;finish!=right.end() && finish->first<=end;++finish) {
                    const auto text=selected.substr(start->second,finish->second-start->second);
                    const int characters=static_cast<int>(learningCharacters(text));
                    if(!characters || characters>16 || !learningStaticText(text) || before.find(text)!=std::u16string_view::npos)continue;
                    std::u16string code(raw.substr(start->first,finish->first-start->first));
                    code = LowerInvariant(code);
                    const auto ctx=learningContext(selected.substr(0,start->second));
                    if(!(snapshot && snapshot->score(mode,code,text,ctx)>0) && !(supplemental && supplemental(text)))continue;
                    matches.push_back({start->first,finish->first,start->second,finish->second,characters,
                        std::move(code),std::u16string(text),ctx});
                }
            }
            if(!matches.empty()) {
                auto best=std::max_element(matches.begin(),matches.end(),[](const auto& x,const auto& y){return x.characters<y.characters;});
                const int longest=best->characters;
                int longestCount=0;for(const auto& m:matches)if(m.characters==longest)++longestCount;
                bool containsAll=longestCount==1;
                if(containsAll)for(const auto& m:matches)
                    if(m.rawStart<best->rawStart || m.rawEnd>best->rawEnd){containsAll=false;break;}
                if(containsAll) {
                    SentenceLearningEvent event;event.id=learningId();event.time=learningNow();event.mode=std::u16string(mode);
                    event.code=best->code;event.text=best->text;event.context=best->context;
                    event.rawStart=best->rawStart;event.rawEnd=best->rawEnd;event.textStart=best->textStart;event.textEnd=best->textEnd;
                    result.push_back(std::move(event));
                }
            }
        }
        previous=end;
    }
    if(result.size()!=1)result.clear();
    return result;
}

// A manual exact selection learns only validated typed-code/selected-text
// fragments. A standalone two-character choice confirms the small whole phrase;
// longer sentences may reinforce one unambiguous existing local fragment even
// when it crosses a shared-boundary diff. Unrelated prefixes are never guessed.
inline std::vector<SentenceLearningEvent> sentenceLearningSelectionEvents(
    std::u16string_view raw,std::u16string_view before,std::u16string_view selected,
    const std::vector<SentenceLearningBoundary>& a,const std::vector<SentenceLearningBoundary>& b,
    int floorRaw,std::u16string_view mode,const std::shared_ptr<const SentenceLearningSnapshot>& snapshot,
    const std::function<bool(std::u16string_view)>& supplemental={}) {
    if(mode.empty() || learningLegacyPairMode(mode))return {};
    auto result=sentenceLearningDiff(raw,before,selected,a,b,floorRaw);
    if(result.empty())return result; // Also validates both complete UTF-16 paths.
    auto make=[&](int rawStart,int rawEnd,int textStart,int textEnd) {
        SentenceLearningEvent e;e.id=learningId();e.time=learningNow();e.mode=std::u16string(mode);
        e.code=raw.substr(rawStart,rawEnd-rawStart);
        for(auto& c:e.code)if(c>=u'A' && c<=u'Z')c+=u'a'-u'A';
        e.text=selected.substr(textStart,textEnd-textStart);
        e.context=learningContext(selected.substr(0,textStart));
        e.rawStart=rawStart;e.rawEnd=rawEnd;e.textStart=textStart;e.textEnd=textEnd;
        return e;
    };
    for(auto& e:result)e.mode=std::u16string(mode);
    if(floorRaw==0 && raw.size()<=128 && learningCharacters(selected)==2 && learningStaticText(selected))
        return {make(0,static_cast<int>(raw.size()),0,static_cast<int>(selected.size()))};
    std::vector<SentenceLearningBoundary> points{{0,0}};
    points.insert(points.end(),b.begin(),b.end());
    struct Match {int characters;SentenceLearningEvent event;};
    std::vector<Match> matches;
    auto overlaps=[&](int start,int end) {
        return std::any_of(result.begin(),result.end(),[&](const auto& e){return start<e.rawEnd && end>e.rawStart;});
    };
    if((snapshot && !snapshot->empty()) || supplemental)for(std::size_t start=0;start+1<points.size();++start) {
        const auto first=points[start];
        if(first.raw<floorRaw)continue;
        // Only inspect starts capable of reaching a changed interval within
        // the 16-character / 128-code fragment limits.
        const bool nearby=std::any_of(result.begin(),result.end(),[&](const auto& e) {
            return first.raw<e.rawEnd && e.rawStart-first.raw<128 &&
                (first.text>=e.textStart || learningCharacters(selected.substr(first.text,e.textStart-first.text))<16);
        });
        if(!nearby)continue;
        for(std::size_t end=start+1;end<points.size();++end) {
            const auto last=points[end];
            const auto text=selected.substr(first.text,last.text-first.text);
            const auto characters=learningCharacters(text);
            if(!characters || characters>16 || last.raw-first.raw>128)break;
            if(!overlaps(first.raw,last.raw) || !learningStaticText(text) ||
               before.find(text)!=std::u16string_view::npos)continue;
            auto event=make(first.raw,last.raw,first.text,last.text);
            if(!(snapshot && snapshot->score(mode,event.code,event.text,event.context)>0) &&
               !(supplemental && supplemental(event.text)))continue;
            matches.push_back({static_cast<int>(characters),std::move(event)});
        }
    }
    if(!matches.empty()) {
        const auto best=std::max_element(matches.begin(),matches.end(),
            [](const auto& x,const auto& y){return x.characters<y.characters;});
        const int longest=best->characters;
        const auto count=std::count_if(matches.begin(),matches.end(),[&](const auto& m){return m.characters==longest;});
        const bool containsAll=count==1 && std::all_of(matches.begin(),matches.end(),[&](const auto& m) {
            return m.event.rawStart>=best->event.rawStart && m.event.rawEnd<=best->event.rawEnd;
        });
        if(containsAll) {
            const auto winner=best->event;
            result.erase(std::remove_if(result.begin(),result.end(),[&](const auto& e) {
                return e.rawStart<winner.rawEnd && e.rawEnd>winner.rawStart;
            }),result.end());
            result.push_back(winner);
        }
    }
    result.erase(std::remove_if(result.begin(),result.end(),[](const auto& e){return e.code.empty() || e.code.size()>128;}),result.end());
    std::sort(result.begin(),result.end(),[](const auto& x,const auto& y){return x.rawStart<y.rawStart;});
    return result;
}


// Retired record constructors remain only for compatibility regression fixtures.
struct SentenceFusionPreference {
    static std::u16string mode(std::u16string_view sentenceMode) {
        if(sentenceMode.empty())return {};
        std::u16string value=u"fusion-v1|";value+=sentenceMode;return value;
    }
    static std::u16string pairCode(std::u16string_view raw,std::u16string_view direct,std::u16string_view composed) {
        std::u16string value(raw);
        value.push_back(0);value.push_back(u'D');value.push_back(0);value+=direct;
        value.push_back(0);value.push_back(u'C');value.push_back(0);value+=composed;
        return std::u16string(u"~f")+learningConfigurationHash(value);
    }
    static double signedScore(const std::shared_ptr<const SentenceLearningSnapshot>& snapshot,
        std::u16string_view sentenceMode,std::u16string_view raw,
        std::u16string_view direct,std::u16string_view composed) {
        if(!snapshot || snapshot->empty() || sentenceMode.empty())return 0;
        auto m=mode(sentenceMode),code=pairCode(raw,direct,composed);
        return snapshot->score(m,code,u"D",u"")-snapshot->score(m,code,u"C",u"");
    }
    static SentenceLearningEvent event(std::u16string_view sentenceMode,std::u16string_view raw,
        std::u16string_view direct,std::u16string_view composed,bool directWins,int rawEnd) {
        SentenceLearningEvent e;e.id=learningId();e.time=learningNow();e.mode=mode(sentenceMode);
        e.code=pairCode(raw,direct,composed);e.text=directWins?u"D":u"C";e.context=u"";
        e.rawStart=0;e.rawEnd=std::max(0,rawEnd);e.textStart=0;e.textEnd=1;return e;
    }
};

// Only ordinary fragments from the active sentence mode may reach receipts.
inline void filterSentenceLearningForMode(std::vector<SentenceLearningEvent>& events,std::u16string_view activeMode) {
    events.erase(std::remove_if(events.begin(),events.end(),[&](const auto& event) {
        return activeMode.empty() || learningLegacyPairMode(event.mode) || event.mode!=activeMode;
    }),events.end());
}

// Writer-local replay state, never shared with decoders. Immutable snapshots
// share untouched code partitions only within the same scoring second. Clock
// rollback, future-clamped events, undo/clear and dropped history replay fully.
class SentenceLearningAccumulator {
    struct Choice {double weight=0,confirmed=0;};
    using Key=std::tuple<std::u16string,std::u16string,std::u16string>;
    std::map<Key,std::map<std::u16string,Choice>> groups_;
    std::vector<SentenceLearningEvent> events_;
    std::shared_ptr<const SentenceLearningSnapshot> current_;
    static bool same(const SentenceLearningEvent& a,const SentenceLearningEvent& b) {
        return a.id==b.id && a.time==b.time && a.mode==b.mode && a.code==b.code && a.text==b.text && a.context==b.context && a.levels==b.levels;
    }
public:
    std::shared_ptr<const SentenceLearningSnapshot> update(const std::vector<SentenceLearningEvent>& events,std::int64_t now=learningNow()) {
        (void)now;
        bool rebuild=!current_ || events.size()<events_.size();
        if(!rebuild)for(std::size_t i=0;i<events_.size();++i)if(!same(events_[i],events[i])){rebuild=true;break;}
        std::set<std::u16string> dirty;
        if(rebuild){groups_.clear();events_.clear();}
        for(std::size_t i=events_.size();i<events.size();++i) {
            const auto& event=events[i];
            if(learningLegacyPairMode(event.mode) || event.mode.empty() || event.mode.size()>512 || event.code.empty() || event.code.size()>128 || !learningStaticText(event.text) ||
               (!event.context.empty() && (!learningCharacters(event.context) || learningCharacters(event.context)>2)) || event.levels<1 || event.levels>3)continue;
            dirty.insert(event.code);
            auto& choices=groups_[{event.code,event.mode,event.context}];
            for(auto& entry:choices)if(entry.first!=event.text){entry.second.weight*=0.25;entry.second.confirmed*=0.25;}
            auto& target=choices.try_emplace(event.text,Choice{}).first->second;
            target.weight=std::min(static_cast<double>(SentenceLearningSnapshot::maximumCorrectionLevel),target.weight+event.levels);
            target.confirmed=std::min(static_cast<double>(SentenceLearningSnapshot::maximumCorrectionLevel),target.confirmed+1);
        }
        events_=events;
        if(!rebuild && dirty.empty())return current_;
        auto next=std::make_shared<SentenceLearningSnapshot>();
        if(!rebuild && current_) { next->byCode_=current_->byCode_; next->prefixes_=current_->prefixes_; }
        for(const auto& code:dirty) {
            struct Summary {SentenceLearningSnapshot::ContextScores exact,weights,confidenceExact;double weight=0,confirmed=0;};
            std::map<std::pair<std::u16string,std::u16string>,Summary> summaries;
            for(auto group=groups_.lower_bound(Key{code,u"",u""});group!=groups_.end() && std::get<0>(group->first)==code;++group) {
                const auto& mode=std::get<1>(group->first);const auto& context=std::get<2>(group->first);
                for(const auto& entry:group->second) {
                    auto& summary=summaries[{mode,entry.first}];
                    summary.exact[context]=SentenceLearningSnapshot::exactScore(entry.second.weight);
                    summary.weight+=entry.second.weight;
                summary.confirmed+=entry.second.confirmed;
                summary.weights[context]=entry.second.weight;
                summary.confidenceExact[context]=SentenceLearningSnapshot::exactScore(entry.second.confirmed);
                }
            }
            auto partition=std::make_shared<SentenceLearningSnapshot::ModeScores>();
            for(auto& entry:summaries) {
                auto& summary=entry.second;
                auto& scores=(*partition)[entry.first.first][entry.first.second];
                scores.general=SentenceLearningSnapshot::generalScore(summary.weight);
                scores.confidenceGeneral=SentenceLearningSnapshot::generalScore(summary.confirmed);
                scores.weights=std::move(summary.weights);scores.confidenceExact=std::move(summary.confidenceExact);
                scores.exact=std::move(summary.exact);
            }
            next->prefixes_[code]=SentenceLearningSnapshot::BuildPrefixes(*partition);
            next->byCode_[code]=std::move(partition);
        }
        if(next->empty() && current_ && current_->empty())return current_;
        current_=std::move(next);return current_;
    }
};
}
