#include "CompactLexicon.h"
#include <iostream>
#include <stdexcept>

using tiger::core::CompactLexicon;
static void Check(bool ok) { if (!ok) throw std::runtime_error("compact reader assertion failed"); }
int main()
{
    try
    {
        // One code z -> U+20000. Explicit LE integers/UTF-16, no host alignment assumptions.
        std::vector<std::uint8_t> image;
        auto integer = [&](std::uint32_t n)
        { for (unsigned i = 0; i < 4; ++i) image.push_back(std::uint8_t(n >> (8 * i))); };
        for (auto n : {0x584c4354u, 1u, 1u, 1u, 2u, 0u, 0u, 0u, 1u, 1u, 0u, 1u, 3u}) integer(n);
        image.insert(image.end(), {0x7a, 0, 0x40, 0xd8, 0, 0xdc});
        CompactLexicon lexicon(image);
        Check(lexicon.Count() == 1 && lexicon.Find(u"Z") == 0u && !lexicon.Find(u"zz"));
        Check(lexicon.CandidateCount(0) == 1 && lexicon.Candidate(0, 0) == u"\U00020000");
        bool invalidRank = false;
        try { lexicon.Candidate(0, 1); } catch (const std::out_of_range&) { invalidRank = true; }
        Check(invalidRank);
        std::vector<CompactLexicon::Entry> entries{{u"z", {u"\U00020000"}}};
        auto built = CompactLexicon::Build(entries);
        Check(std::vector<std::uint8_t>(built.Image().begin(), built.Image().end()) == image);
        entries[0].second[0] = u"changed";
        Check(built.Candidate(0, 0) == u"\U00020000"); // No view into builder input survives.
        entries = {{u"z", {u"same", u"same", u""}}, {u"A", {}}, {u"", {u"same"}}};
        auto ordered = CompactLexicon::Build(entries);
        Check(ordered.Code(0) == u"z" && ordered.Code(1) == u"A" && ordered.Find(u"a") == 1u);
        Check(ordered.CandidateCount(0) == 3 && ordered.Candidate(0, 1) == u"same");
        Check(ordered.CandidateCount(1) == 0 && ordered.Code(2).empty());
        Check(CompactLexicon::Build({}).Count() == 0);
        entries = {{u"a", {}}, {u"A", {u"x"}}};
        bool duplicateRejected = false;
        try { auto duplicate = CompactLexicon::Build(entries); }
        catch (const std::runtime_error&) { duplicateRejected = true; }
        Check(duplicateRejected);
        for (std::size_t length = 0; length < image.size(); ++length)
        {
            bool rejected = false;
            try { CompactLexicon truncated({image.begin(), image.begin() + length}); }
            catch (const std::runtime_error&) { rejected = true; }
            Check(rejected);
        }
        for (std::size_t offset : {0u, 4u, 8u, 12u, 16u, 20u, 24u, 32u, 36u, 44u, 48u})
        {
            auto bad = image;
            for (std::size_t i = offset; i < offset + 4; ++i) bad[i] = 255;
            bool rejected = false;
            try { CompactLexicon corrupt(std::move(bad)); }
            catch (const std::runtime_error&) { rejected = true; }
            Check(rejected);
        }
        std::cout << "Native compact reader boundary tests passed\n";
        return 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
