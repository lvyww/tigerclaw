#include "CompactLexicon.h"
#include "ReplayProbe.h"
#include <nlohmann/json.hpp>
#include <iostream>
#ifdef _WIN32
#include <windows.h>
#include "RuntimeStartup.h"
#include "RuntimeEndpoints.h"
int RunIsolatedRuntimeHost(const std::filesystem::path&, const std::wstring&, bool, bool);
int RunProductionRuntimeHost(bool);
#endif

int RunLexiconTextProbe();
int RunRuntimeProtocolProbe(const std::filesystem::path&, const std::filesystem::path&, const std::filesystem::path&);
int RunMainlineProbe(const std::filesystem::path&, const std::filesystem::path&);
static int Run(const std::vector<std::string>& args)
{
    try
    {
#ifdef _WIN32
        if(args.size()==2 && args[1]=="--startup-info")
        {
            tiger::core::RuntimeEndpoints e;
            auto narrow=[](const std::wstring& s){return std::string(s.begin(),s.end());};
            std::cout << nlohmann::json{{"executable","TigerClaw.Core.exe"},{"pipe",narrow(e.pipe)},
                {"ui",narrow(e.ui)},{"heartbeat",narrow(e.heartbeat)},{"menu",narrow(e.menu)},
                {"sentence",narrow(e.sentence)},{"mutex","Local\\TigerClaw.Core.SingleInstance"},
                {"startupAllowed",tiger::core::CanStartCore()},{"tsfRegistered",tiger::core::IsTsfRegistered()}}.dump() << '\n';
            return 0;
        }
        if (args.size() == 4 && (args[1] == "--serve-isolated" || args[1] == "--serve-isolated-ui" || args[1]=="--serve-isolated-detached"))
            return RunIsolatedRuntimeHost(std::filesystem::path(std::u8string(args[2].begin(),args[2].end())),
                std::filesystem::path(std::u8string(args[3].begin(),args[3].end())).wstring(), args[1] == "--serve-isolated-ui", args[1]!="--serve-isolated-detached");
#endif
#ifdef TIGER_CORE_PRODUCTION
        // As in C#, unknown launcher arguments are ignored. Explicit diagnostic
        // commands stay separate, including malformed probes (never start IPC).
        const bool diagnostic=args.size()>1 && (args[1]=="--startup-info" || args[1]=="--capabilities" ||
            args[1]=="--runtime-probe" || args[1]=="--mainline-probe" || args[1]=="--lexicon-text-stdio" ||
            args[1]=="--replay-stdio" || args[1]=="--lexicon-json" || args[1].starts_with("--serve-isolated") || args[1]=="--help");
        if(!diagnostic)
        {
            bool withOverlay=true;
            for(const auto& arg:args) if(_stricmp(arg.c_str(),"--without-overlay")==0) withOverlay=false;
            return RunProductionRuntimeHost(withOverlay);
        }
#endif
        if (args.size() == 5 && args[1] == "--runtime-probe")
            return RunRuntimeProtocolProbe(std::filesystem::path(std::u8string(args[2].begin(), args[2].end())),
                std::filesystem::path(std::u8string(args[3].begin(), args[3].end())), std::filesystem::path(std::u8string(args[4].begin(), args[4].end())));
        if (args.size() == 4 && args[1] == "--mainline-probe")
            return RunMainlineProbe(std::filesystem::path(std::u8string(args[2].begin(), args[2].end())),
                std::filesystem::path(std::u8string(args[3].begin(), args[3].end())));
        if (args.size() == 2 && args[1] == "--lexicon-text-stdio") return RunLexiconTextProbe();
        if (args.size() == 2 && args[1] == "--replay-stdio") return RunReplayProbe();
        if (args.size() == 2 && args[1] == "--capabilities")
        {
            nlohmann::json capabilities{{"implementation","TigerClaw.Core.Native.Experimental"},{"production_ready",false},{"input_engine",true},
                {"implemented",{"tclx-v1-reader","tcs-fivegram","ordinary-and-sentence-input","adaptive-learning","key-replay-cache","dialog-data-commands","ui-state-projection"}},
                {"remaining",{"production-tsf-hook-typing-acceptance","long-duration-resource-and-latency-acceptance"}}};
#ifdef _WIN32
            capabilities["transport"]="isolated-named-pipe";
            capabilities["ui_channels"]={"v1-mmf","v2-snapshot","heartbeat"};
            capabilities["frontend_host"]="explicit-isolated-root-owned-dialog-and-overlay";
#ifdef TIGER_CORE_PRODUCTION
            capabilities["implementation"]="TigerClaw.Core";
            capabilities["backend"]="cpp";
            capabilities["transport"]="BimeIPC";
            capabilities["production_entrypoint"]=true;
            capabilities["frontend_host"]="runtime-directory-dialog-overlay-hook";
#endif
#else
            capabilities["transport"]="file-probe";
#endif
            std::cout << capabilities.dump() << '\n';
            return 0;
        }
        if (args.size() != 3 || args[1] != "--lexicon-json")
        {
#ifdef TIGER_CORE_PRODUCTION
            std::cerr << "TigerClaw.Core: launch with --with-overlay (default), --without-overlay, --autorun or --silent. ";
#else
            std::cerr << "Experimental parallel Core (isolated IPC only). ";
#endif
            std::cerr << "Diagnostics: --capabilities, --startup-info, --runtime-probe <root> <requests> <responses>, --serve-isolated[-ui|-detached] <root> <TigerClaw.Core.Native.Test.*> (Windows), --mainline-probe <model> <input>, --lexicon-json <TCLX file>, --lexicon-text-stdio or --replay-stdio.\n";
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
