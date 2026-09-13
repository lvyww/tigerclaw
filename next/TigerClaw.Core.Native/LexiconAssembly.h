#pragma once
#include "CompactLexicon.h"
#include "LexiconText.h"
namespace tiger::core
{
    enum class CandidateAdjustment { Advance, Top, Delete };
    // Input order must already be file/source order. Uncoded entries must be
    // inferred before this stage; they are skipped here as in the C# map loop.
    std::vector<CompactLexicon::Entry> AssembleCodedRows(std::span<const LexiconRow> rows);
    // Mutate a private assembly only, before publishing its compact snapshot.
    void ApplyAdjustments(std::vector<CompactLexicon::Entry>& entries, std::span<const std::u16string> lines);
    void ApplyCustomRows(std::vector<CompactLexicon::Entry>& entries, std::span<const std::u16string> lines);
    void LoadAdjustmentFile(std::vector<CompactLexicon::Entry>& entries, const std::filesystem::path& path, bool custom = false);
}
