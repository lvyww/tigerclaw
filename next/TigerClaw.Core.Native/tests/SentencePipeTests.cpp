#include "SentencePipeClient.h"
#include "SentenceDecodeWorker.h"
#include <windows.h>
#include <thread>
#include <semaphore>
#include <atomic>
#include <iostream>

using namespace tiger::core;
namespace
{
    void Check(bool value) { if (!value) throw std::runtime_error("Sentence pipe regression"); }
    struct Server
    {
        std::wstring name;
        HANDLE pipe = INVALID_HANDLE_VALUE;
        std::counting_semaphore<2> finish{0};
        std::binary_semaphore requestRead{0};
        std::thread thread;
        Server(int id, bool stall, bool invalid = false, unsigned sequence = 1)
        {
            name = L"TigerClaw.Core.Native.Test.Sentence." + std::to_wstring(GetCurrentProcessId()) + L"." + std::to_wstring(id);
            auto path = std::wstring(L"\\\\.\\pipe\\") + name;
            pipe = CreateNamedPipeW(path.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 4096, 4096, 0, nullptr);
            Check(pipe != INVALID_HANDLE_VALUE);
            thread = std::thread([this, stall, invalid, sequence]
            {
                if (!ConnectNamedPipe(pipe, nullptr) && GetLastError() != ERROR_PIPE_CONNECTED) return;
                char buffer[4096]; DWORD count = 0;
                std::string request;
                while (request.find('\n') == std::string::npos)
                {
                    if (!ReadFile(pipe, buffer, sizeof(buffer), &count, nullptr) || !count) return;
                    request.append(buffer, count);
                }
                requestRead.release();
                if (stall) { finish.acquire(); return; }
                std::string response = invalid ? "{}\n" :
                    "{\"type\":\"response\",\"seq\":" + std::to_string(sequence) + ",\"generation\":2,\"raw_code\":\"aa\",\"success\":true,\"scores\":[-8.25]}\n";
                // Separate writes exercise fragmented JSON framing.
                for (std::size_t offset = 0; offset < response.size(); offset += 7)
                    if (!WriteFile(pipe, response.data() + offset, static_cast<DWORD>(std::min<std::size_t>(7, response.size() - offset)), &count, nullptr)) return;
                finish.acquire(); // keep pipe open until the client has consumed data
            });
        }
        ~Server()
        {
            finish.release();
            // The server may not have entered Connect/Read yet when the client
            // rejects its PID. Repeat cancellation until the thread exits;
            // disconnecting first can make a late Connect wait for a new client.
            while (WaitForSingleObject(thread.native_handle(), 0) == WAIT_TIMEOUT)
            {
                CancelSynchronousIo(thread.native_handle());
                WaitForSingleObject(thread.native_handle(), 10);
            }
            if (thread.joinable()) thread.join();
            DisconnectNamedPipe(pipe);
            CloseHandle(pipe);
        }
    };
}
int main()
{
    try
    {
        SentenceNeuralRequest request{{2, 0, u"aa", {}}, {u"X"}};
        {
            Server server(1, false);
            Check(ScoreSentencePipe(server.name, request, 1) == std::vector<double>({-8.25}));
        }
        {
            Server server(2, true);
            bool failed = false;
            try { ScoreSentencePipe(server.name, request, 1, {}, 1000, 50); }
            catch (const std::runtime_error&) { failed = true; }
            Check(failed);
        }
        {
            Server server(3, true);
            std::stop_source source;
            std::jthread cancel([&] { Sleep(50); source.request_stop(); });
            bool failed = false;
            try { ScoreSentencePipe(server.name, request, 1, source.get_token(), 1000, 2000); }
            catch (const std::runtime_error&) { failed = true; }
            Check(failed);
        }
        {
            Server server(4, false, true);
            bool failed = false;
            try { ScoreSentencePipe(server.name, request, 1); }
            catch (const std::runtime_error&) { failed = true; }
            Check(failed);
        }
        bool missing = false;
        auto absent = L"TigerClaw.Core.Native.Test.Absent." + std::to_wstring(GetCurrentProcessId());
        try { ScoreSentencePipe(absent, request, 1, {}, 30, 50); }
        catch (const std::runtime_error&) { missing = true; }
        Check(missing);
        {
            Server server(9, false);
            ControlSentencePipe(server.name, SentenceControlCommand::Hello, 1);
            Check(server.requestRead.try_acquire());
        }
        {
            Server server(10, false);
            ControlSentencePipe(server.name, SentenceControlCommand::Shutdown, 1, GetCurrentProcessId());
            Check(server.requestRead.try_acquire());
        }
        {
            Server server(11, false);
            bool rejected = false;
            try { ControlSentencePipe(server.name, SentenceControlCommand::Shutdown, 1, GetCurrentProcessId() + 1); }
            catch (const std::runtime_error&) { rejected = true; }
            Check(rejected && !server.requestRead.try_acquire());
        }
        {
            bool rejected = false;
            try { ControlSentencePipe(absent, SentenceControlCommand::Shutdown, 1); }
            catch (const std::invalid_argument&) { rejected = true; }
            Check(rejected);
        }
        {
            auto name = L"TigerClaw.Core.Native.Test.Sentence." + std::to_wstring(GetCurrentProcessId()) + L".7";
            auto score = MakeSentencePipeScorer(name, 30, 500);
            auto copiedScore = score;
            bool failed = false;
            try { score(request, {}); } catch (const std::runtime_error&) { failed = true; }
            Check(failed);
            Server server(7, false, false, 2);
            Check(copiedScore(request, {}) == std::vector<double>({-8.25}));
        }
        {
            Server server(8, false);
            BasicSentenceDecodeWorker<std::vector<double>, SentenceNeuralRequest> worker(
                [score = MakeSentencePipeScorer(server.name)](const SentenceNeuralRequest& input, std::stop_token stop)
                { return std::make_shared<const std::vector<double>>(score(input, stop)); });
            worker.Submit(request);
            Check(worker.WaitIdle(std::chrono::seconds(2)));
            auto result = worker.TakeCompleted();
            Check(result && !result->error && *result->result == std::vector<double>({-8.25}));
        }
        for (int id : {5, 6})
        {
            Server server(id, true);
            using PipeWorker = BasicSentenceDecodeWorker<std::vector<double>, SentenceNeuralRequest>;
            auto worker = std::make_unique<PipeWorker>([&](const SentenceNeuralRequest& input, std::stop_token stop)
            {
                return std::make_shared<const std::vector<double>>(ScoreSentencePipe(server.name, input, 1, stop));
            });
            worker->Submit(request);
            bool received = server.requestRead.try_acquire_for(std::chrono::seconds(2));
            if (id == 5)
            {
                worker->Cancel();
                Check(worker->WaitIdle(std::chrono::seconds(2)) && !worker->TakeCompleted());
            }
            else worker.reset(); // destruction must cancel and drain the blocked pipe read
            Check(received);
        }
        std::cout << "Isolated sentence pipe fragment/timeout/cancel/invalid/missing tests passed\n";
        return 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
