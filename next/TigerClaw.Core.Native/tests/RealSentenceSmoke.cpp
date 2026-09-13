#include "OwnedSentenceProcess.h"
#include <windows.h>
#include <chrono>
#include <cmath>
#include <iostream>

using namespace tiger::core;
int wmain(int argc, wchar_t** argv)
{
    if (argc != 3)
    {
        std::cerr << "Opt-in only: real_sentence_smoke <absolute sidecar exe> <absolute GGUF>\n";
        return 2;
    }
    try
    {
        auto started = std::chrono::steady_clock::now();
        OwnedSentenceProcess process(argv[1], argv[2],
            L"TigerClaw.Core.Native.Test.RealSentence." + std::to_wstring(GetCurrentProcessId()));
        process.Preload({}, 60000);
        auto pid = process.ProcessId();
        if (!pid) throw std::runtime_error("Owned model process is not alive");
        SentenceNeuralRequest request{{2, 1, u"iejryfenahbmsp", {}},
            {u"\u65b0\u4eba\u4e0a\u5348\u6765\u9762\u8bd5", u"\u65b0\u4eba\u4e0a\u7aa6\u6765\u9762\u8bd5",
             u"\u65b0\u4eba", u"\u4e0a\u5348", u"\u9762\u8bd5"}};
        auto scores = process.Score(request, {}, 60000, 60000);
        ++request.composition.generation;
        auto repeated = process.Score(request, {}, 60000, 60000);
        if (scores.size() != 5 || repeated.size() != scores.size() || process.ProcessId() != pid)
            throw std::runtime_error("Score count or process reuse mismatch");
        for (std::size_t i = 0; i < scores.size(); ++i)
            if (!std::isfinite(scores[i]) || !std::isfinite(repeated[i]) || std::abs(scores[i] - repeated[i]) > 1e-5)
                throw std::runtime_error("Nonfinite or unstable repeated model score");
        HANDLE observed = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
        if (!observed) throw std::runtime_error("Cannot observe owned child exit");
        try { process.Stop(5000); }
        catch (...) { CloseHandle(observed); throw; }
        DWORD exitCode = STILL_ACTIVE;
        bool exited = GetExitCodeProcess(observed, &exitCode) && exitCode == 0;
        CloseHandle(observed);
        if (!exited) throw std::runtime_error("Real sidecar did not exit gracefully");
        std::cout << "Real Qwen owned preload/score/repeat/graceful-stop passed; scores:";
        for (auto score : scores) std::cout << ' ' << score;
        std::cout << "; elapsed_ms=" << std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now() - started).count() << '\n';
        return 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
