#include "CompactLexicon.h"
#include "ReplayProbe.h"
#include <nlohmann/json.hpp>
#include <iostream>
#ifdef _WIN32
#include <windows.h>
#endif

int RunLexiconTextProbe();
static int Run(const std::vector<std::string>& args)
{
    try
    {
        if (args.size() == 2 && args[1] == "--lexicon-text-stdio") return RunLexiconTextProbe();
        if (args.size() == 2 && args[1] == "--replay-stdio") return RunReplayProbe();
        if (args.size() == 2 && args[1] == "--capabilities")
        {
            std::cout << R"({"implementation":"TigerClaw.Core.Native.Experimental","production_ready":false,"transport":"offline-only","implemented":["tclx-v1-reader","key-replay-cache"],"input_engine":false})" << '\n';
            return 0;
        }
        if (args.size() != 3 || args[1] != "--lexicon-json")
        {
            std::cerr << "Experimental parallel Core. Use --capabilities, --lexicon-json <TCLX file>, --lexicon-text-stdio or --replay-stdio. No production IPC.\n";
            return 2;
        }
        auto lexicon = tiger::core::CompactLexicon::Load(std::filesystem::path(std::u8string(args[2].begin(), args[2].end())));
        // Emit UTF-16 units, including unpaired surrogates, without lossy JSON
        // Unicode transcoding. Used by the C# cross-language fixture comparison.
        auto units = [](const std::u16string& text)
        { return std::vector<std::uint16_t>(text.begin(), text.end()); };
        for (std::uint32_t i = 0; i < lexicon.Count(); ++i)
        {
            auto code = lexicon.Code(i);
            auto upper = code;
            for (auto& c : upper) if (c >= u'a' && c <= u'z') c = char16_t(c - 32);
            if (lexicon.Find(upper) != i) throw std::runtime_error("lookup/order mismatch");
            nlohmann::json row{{"code", units(code)}, {"candidates", nlohmann::json::array()}};
            for (std::uint32_t rank = 0; rank < lexicon.CandidateCount(i); ++rank)
                row["candidates"].push_back(units(lexicon.Candidate(i, rank)));
            std::cout << row.dump() << '\n';
        }
        return 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
#ifdef _WIN32
int wmain(int argc, wchar_t** argv)
{
    std::vector<std::string> args;
    for (int i = 0; i < argc; ++i)
    {
        int size = WideCharToMultiByte(CP_UTF8, 0, argv[i], -1, nullptr, 0, nullptr, nullptr);
        if (size <= 0) return 2;
        std::string value(size, '\0');
        WideCharToMultiByte(CP_UTF8, 0, argv[i], -1, value.data(), size, nullptr, nullptr);
        value.pop_back(); args.push_back(std::move(value));
    }
    return Run(args);
}
#else
int main(int argc, char** argv) { return Run({argv, argv + argc}); }
#endif
