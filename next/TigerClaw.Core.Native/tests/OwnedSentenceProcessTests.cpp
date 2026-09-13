#include "OwnedSentenceProcess.h"
#include "OwnedSentenceService.h"
#include <windows.h>
#include <iostream>
#include <thread>
#include <cstdlib>

using namespace tiger::core;
namespace
{
    void Check(bool value) { if (!value) throw std::runtime_error("Owned Sentence process regression"); }
    int Serve(std::wstring_view name)
    {
        auto path = L"\\\\.\\pipe\\" + std::wstring(name);
        for (;;)
        {
            HANDLE pipe = CreateNamedPipeW(path.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                1, 4096, 4096, 0, nullptr);
            if (pipe == INVALID_HANDLE_VALUE) return 2;
            if (!ConnectNamedPipe(pipe, nullptr) && GetLastError() != ERROR_PIPE_CONNECTED)
            { CloseHandle(pipe); return 3; }
            std::string request;
            char c; DWORD count;
            while (request.size() < 4096 && ReadFile(pipe, &c, 1, &count, nullptr) && count && c != '\n')
                request += c;
            auto sequence = request.find("\"seq\":");
            bool shutdown = request.find("\"shutdown\"") != std::string::npos;
            bool hello = request.find("\"hello\"") != std::string::npos;
            bool rerank = request.find("\"rerank\"") != std::string::npos;
            if (sequence == std::string::npos || (!hello && !shutdown && !rerank))
            { CloseHandle(pipe); return 4; }
            auto seq = std::strtoull(request.c_str() + sequence + 6, nullptr, 10);
            auto response = "{\"type\":\"response\",\"success\":true,\"seq\":" + std::to_string(seq) + "}\n";
            if (rerank)
                response = "{\"type\":\"response\",\"success\":true,\"seq\":" + std::to_string(seq) +
                    ",\"generation\":2,\"raw_code\":\"aa\",\"scores\":[-8.25]}\n";
            bool sent = WriteFile(pipe, response.data(), static_cast<DWORD>(response.size()), &count, nullptr)
                && count == response.size();
            if (sent) FlushFileBuffers(pipe);
            DisconnectNamedPipe(pipe); CloseHandle(pipe);
            if (!sent) return 5;
            if (shutdown) return 0;
        }
    }
}
int wmain(int argc, wchar_t** argv)
{
    // Dedicated non-scoring child; never connects to production IPC or models.
    if (argc > 2 && std::wstring_view(argv[1]) == L"--parent-pid")
    {
        HANDLE parent = OpenProcess(SYNCHRONIZE, FALSE, std::wcstoul(argv[2], nullptr, 10));
        for (int i = 3; parent && i + 1 < argc; ++i)
        {
            if (std::wstring_view(argv[i]) != L"--pipe" ||
                !std::wstring_view(argv[i + 1]).ends_with(L".graceful")) continue;
            std::thread([parent]
            {
                WaitForSingleObject(parent, 30000); CloseHandle(parent); ExitProcess(9);
            }).detach();
            return Serve(argv[i + 1]);
        }
        if (parent) { WaitForSingleObject(parent, 30000); CloseHandle(parent); }
        return 0;
    }
    try
    {
        wchar_t path[32768]; DWORD length = GetModuleFileNameW(nullptr, path, 32768);
        Check(length && length < 32768);
        std::filesystem::path executable(std::wstring(path, length));
        auto prefix = L"TigerClaw.Core.Native.Test.Owned." + std::to_wstring(GetCurrentProcessId());
        OwnedSentenceProcess first(executable, executable, prefix + L".1");
        OwnedSentenceProcess second(executable, executable, prefix + L".2");
        first.Start(); auto firstPid = first.ProcessId(); Check(firstPid != 0);
        first.Start(); Check(first.ProcessId() == firstPid);
        second.Start(); auto secondPid = second.ProcessId(); Check(secondPid && secondPid != firstPid);
        first.Stop(20); Check(!first.Running() && first.ProcessId() == 0);
        Check(second.Running() && second.ProcessId() == secondPid);
        first.Start(); Check(first.Running()); first.Stop(20);
        second.Stop(20); Check(!second.Running());
        std::stop_source canceled; canceled.request_stop();
        bool rejected = false;
        try { first.Preload(canceled.get_token(), 20); } catch (const std::runtime_error&) { rejected = true; }
        Check(rejected && !first.Running());
        OwnedSentenceProcess graceful(executable, executable, prefix + L" quoted pipe.graceful");
        graceful.Preload({}, 3000);
        auto gracefulPid = graceful.ProcessId(); Check(gracefulPid != 0);
        graceful.Preload({}, 3000); Check(graceful.ProcessId() == gracefulPid);
        HANDLE observed = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, gracefulPid);
        Check(observed != nullptr);
        graceful.Stop(3000);
        DWORD exitCode = STILL_ACTIVE;
        bool exited = GetExitCodeProcess(observed, &exitCode) && exitCode == 0;
        CloseHandle(observed);
        Check(exited && !graceful.Running());
        // Full ownership + lifecycle + pipe exchange using only this child.
        SentenceNeuralRequest request{{2, 0, u"aa", {}}, {u"a"}};
        {
            OwnedSentenceService service(executable, executable, prefix + L".service.graceful", 3000, 3000, 1000);
            service.RefreshConfiguration({{u"\u6574\u53e5\u795e\u7ecf\u91cd\u6392", u"false"}}, u"test\u6574\u53e5");
            service.Request(request);
            Check(service.WaitIdle(std::chrono::seconds(3)) && !service.TakeCompleted());
            service.RefreshConfiguration({}, u"test\u6574\u53e5");
            service.Request(request); Check(service.WaitIdle(std::chrono::seconds(3)));
            auto result = service.TakeCompleted();
            Check(result && !result->error && result->scores == std::vector<double>{-8.25});
            service.RefreshConfiguration({}, u"ordinary");
            service.RefreshConfiguration({}, u"test\u6574\u53e5");
            service.Request(request); Check(service.WaitIdle(std::chrono::seconds(5)));
            result = service.TakeCompleted();
            Check(result && !result->error && result->scores == std::vector<double>{-8.25});
            service.RefreshConfiguration({}, u"ordinary"); Check(service.WaitIdle(std::chrono::seconds(3)));
        }
        std::cout << "Owned process launch/idempotence/isolation/restart/cancel/graceful preload tests passed\n";
        return 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
