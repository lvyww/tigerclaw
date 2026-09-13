#include "SentencePipeClient.h"
#include <windows.h>
#include <array>
#include <atomic>

namespace tiger::core
{
    namespace
    {
        struct Handle
        {
            HANDLE value;
            explicit Handle(HANDLE handle) : value(handle) {}
            ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
            Handle(const Handle&) = delete;
            Handle& operator=(const Handle&) = delete;
        };
        [[noreturn]] void Fail() { throw std::runtime_error("Sentence pipe exchange failed or canceled"); }
        DWORD Remaining(ULONGLONG deadline)
        {
            auto now = GetTickCount64();
            return now >= deadline ? 0 : static_cast<DWORD>(std::min<ULONGLONG>(deadline - now, MAXDWORD - 1));
        }
        DWORD Transfer(HANDLE pipe, HANDLE canceled, ULONGLONG deadline, void* buffer, DWORD length, bool writing)
        {
            if (!Remaining(deadline) || WaitForSingleObject(canceled, 0) == WAIT_OBJECT_0) Fail();
            Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
            if (!event.value) Fail();
            OVERLAPPED operation{}; operation.hEvent = event.value;
            DWORD transferred = 0;
            BOOL completed = writing ? WriteFile(pipe, buffer, length, &transferred, &operation)
                                     : ReadFile(pipe, buffer, length, &transferred, &operation);
            if (!completed)
            {
                if (GetLastError() != ERROR_IO_PENDING) Fail();
                HANDLE waits[]{event.value, canceled};
                DWORD wait = WaitForMultipleObjects(2, waits, FALSE, Remaining(deadline));
                if (wait != WAIT_OBJECT_0)
                {
                    // Cancellation races normal completion. Drain either result
                    // before OVERLAPPED/event/buffer leave scope, even past deadline.
                    CancelIoEx(pipe, &operation);
                    GetOverlappedResult(pipe, &operation, &transferred, TRUE);
                    Fail();
                }
                if (!GetOverlappedResult(pipe, &operation, &transferred, FALSE)) Fail();
            }
            if (WaitForSingleObject(canceled, 0) == WAIT_OBJECT_0 || !transferred) Fail();
            return transferred;
        }
    }
    static std::string ExchangeSentencePipe(std::wstring_view shortPipeName, std::string payload,
        std::stop_token stop, unsigned connectTimeoutMs, unsigned responseTimeoutMs, unsigned ownedProcessId = 0,
        bool restartResponseBudget = true)
    {
        if (shortPipeName.empty() || shortPipeName.find_first_of(L"\\/\0", 0, 3) != std::wstring_view::npos ||
            !connectTimeoutMs || !responseTimeoutMs) throw std::invalid_argument("Invalid sentence pipe options");
        if (payload.size() > 65536) throw std::invalid_argument("Sentence pipe request too large");
        Handle canceled(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        if (!canceled.value) Fail();
        std::stop_callback cancelCallback(stop, [&] { SetEvent(canceled.value); });
        std::wstring path = L"\\\\.\\pipe\\"; path.append(shortPipeName);
        auto connectDeadline = GetTickCount64() + connectTimeoutMs;
        HANDLE connected = INVALID_HANDLE_VALUE;
        for (;;)
        {
            if (WaitForSingleObject(canceled.value, 0) == WAIT_OBJECT_0 || !Remaining(connectDeadline)) Fail();
            connected = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
            if (connected != INVALID_HANDLE_VALUE) break;
            auto error = GetLastError();
            if (error != ERROR_PIPE_BUSY && error != ERROR_FILE_NOT_FOUND) Fail();
            if (WaitForSingleObject(canceled.value, std::min<DWORD>(10, Remaining(connectDeadline))) != WAIT_TIMEOUT) Fail();
        }
        Handle pipe(connected);
        if (ownedProcessId)
        {
            ULONG serverPid = 0;
            if (!GetNamedPipeServerProcessId(pipe.value, &serverPid) || serverPid != ownedProcessId) Fail();
        }
        auto deadline = restartResponseBudget ? GetTickCount64() + responseTimeoutMs : connectDeadline;
        std::size_t sent = 0;
        while (sent < payload.size())
            sent += Transfer(pipe.value, canceled.value, deadline, payload.data() + sent, static_cast<DWORD>(payload.size() - sent), true);
        std::string response;
        std::array<char, 4096> buffer;
        for (;;)
        {
            auto count = Transfer(pipe.value, canceled.value, deadline, buffer.data(), static_cast<DWORD>(buffer.size()), false);
            auto newline = std::find(buffer.begin(), buffer.begin() + count, '\n');
            auto consumed = static_cast<std::size_t>(newline - buffer.begin());
            if (response.size() + consumed > 65536) Fail();
            response.append(buffer.data(), consumed);
            if (newline != buffer.begin() + count) break;
        }
        return response;
    }
    std::vector<double> ScoreSentencePipe(std::wstring_view shortPipeName,
        const SentenceNeuralRequest& request, std::uint64_t sequence,
        std::stop_token stop, unsigned connectTimeoutMs, unsigned responseTimeoutMs, unsigned ownedProcessId)
    {
        auto response = ExchangeSentencePipe(shortPipeName, EncodeSentenceRerank(request, sequence), stop,
            connectTimeoutMs, responseTimeoutMs, ownedProcessId);
        auto scores = DecodeSentenceRerank(response, request, sequence);
        if (!scores) Fail();
        return std::move(*scores);
    }
    void ControlSentencePipe(std::wstring_view shortPipeName, SentenceControlCommand command,
        std::uint64_t sequence, unsigned ownedProcessId, std::stop_token stop, unsigned timeoutMs)
    {
        if (command == SentenceControlCommand::Shutdown && !ownedProcessId)
            throw std::invalid_argument("Sentence shutdown requires an owned process PID");
        auto response = ExchangeSentencePipe(shortPipeName, EncodeSentenceControl(command, sequence), stop,
            timeoutMs, timeoutMs, ownedProcessId, false);
        if (!DecodeSentenceControl(response, sequence)) Fail();
    }
    SentencePipeScorer MakeSentencePipeScorer(std::wstring shortPipeName,
        unsigned connectTimeoutMs, unsigned responseTimeoutMs)
    {
        if (shortPipeName.empty() || shortPipeName.find_first_of(L"\\/\0", 0, 3) != std::wstring::npos ||
            !connectTimeoutMs || !responseTimeoutMs) throw std::invalid_argument("Invalid sentence pipe options");
        auto sequence = std::make_shared<std::atomic<std::uint64_t>>(0);
        return [name = std::move(shortPipeName), sequence, connectTimeoutMs, responseTimeoutMs]
            (const SentenceNeuralRequest& request, std::stop_token stop)
        {
            auto previous = sequence->load(std::memory_order_relaxed);
            for (;;)
            {
                if (previous >= static_cast<std::uint64_t>(INT64_MAX))
                    throw std::overflow_error("Sentence pipe sequence exhausted");
                if (sequence->compare_exchange_weak(previous, previous + 1, std::memory_order_relaxed)) break;
            }
            // Failed/canceled exchanges still consume their identity; never
            // reuse it on another connection or after a copied scorer retries.
            return ScoreSentencePipe(name, request, previous + 1, stop, connectTimeoutMs, responseTimeoutMs);
        };
    }
}
