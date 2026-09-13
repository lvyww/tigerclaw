#include "OwnedSentenceProcess.h"
#include "SentencePipeClient.h"
#include <windows.h>

namespace tiger::core
{
    namespace
    {
        std::wstring Quote(std::wstring_view text)
        {
            std::wstring result = L"\"";
            std::size_t slashes = 0;
            for (auto c : text)
            {
                if (c == L'\\') { ++slashes; continue; }
                result.append(c == L'\"' ? slashes * 2 + 1 : slashes, L'\\');
                slashes = 0; result += c;
            }
            result.append(slashes * 2, L'\\'); result += L'\"';
            return result;
        }
    }
    OwnedSentenceProcess::OwnedSentenceProcess(std::filesystem::path executable, std::filesystem::path model, std::wstring pipe)
        : _executable(std::move(executable)), _model(std::move(model)), _pipe(std::move(pipe))
    {
        if (!_executable.is_absolute() || !_model.is_absolute() || _pipe.empty() ||
            _pipe.find_first_of(L"\\/\0", 0, 3) != std::wstring::npos)
            throw std::invalid_argument("Sentence process requires absolute paths and a local pipe name");
    }
    std::uint64_t OwnedSentenceProcess::NextSequence()
    {
        if (_sequence >= static_cast<std::uint64_t>(INT64_MAX)) throw std::overflow_error("Sentence sequence exhausted");
        return ++_sequence;
    }
    bool OwnedSentenceProcess::Running() const { return _process && WaitForSingleObject(_process, 0) == WAIT_TIMEOUT; }
    unsigned OwnedSentenceProcess::ProcessId() const { return Running() ? GetProcessId(_process) : 0; }
    void OwnedSentenceProcess::Start()
    {
        if (Running()) return;
        if (_process) { CloseHandle(_process); _process = nullptr; }
        if (!std::filesystem::is_regular_file(_executable) || !std::filesystem::is_regular_file(_model))
            throw std::runtime_error("Sentence executable or model is missing");
        auto command = Quote(_executable.native()) + L" --parent-pid " + std::to_wstring(GetCurrentProcessId()) +
            L" --pipe " + Quote(_pipe) + L" --model " + Quote(_model.native());
        STARTUPINFOW startup{}; startup.cb = sizeof(startup);
        PROCESS_INFORMATION info{};
        auto directory = _executable.parent_path().native();
        if (!CreateProcessW(_executable.c_str(), command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW,
            nullptr, directory.c_str(), &startup, &info)) throw std::runtime_error("Sentence process launch failed");
        CloseHandle(info.hThread); _process = info.hProcess;
    }
    void OwnedSentenceProcess::Preload(std::stop_token stop, unsigned timeoutMs)
    {
        if (stop.stop_requested()) throw std::runtime_error("Sentence preload canceled");
        Start();
        auto pid = ProcessId();
        if (!pid) throw std::runtime_error("Sentence process exited during preload");
        ControlSentencePipe(_pipe, SentenceControlCommand::Hello, NextSequence(), pid, stop, timeoutMs);
    }
    std::vector<double> OwnedSentenceProcess::Score(const SentenceNeuralRequest& request, std::stop_token stop,
        unsigned connectTimeoutMs, unsigned responseTimeoutMs)
    {
        if (stop.stop_requested()) throw std::runtime_error("Sentence scoring canceled");
        Start();
        auto pid = ProcessId();
        if (!pid) throw std::runtime_error("Sentence process exited before scoring");
        return ScoreSentencePipe(_pipe, request, NextSequence(), stop, connectTimeoutMs, responseTimeoutMs, pid);
    }
    void OwnedSentenceProcess::Stop(unsigned timeoutMs)
    {
        if (!_process) return;
        if (Running())
        {
            try { ControlSentencePipe(_pipe, SentenceControlCommand::Shutdown, NextSequence(), ProcessId(), {}, timeoutMs); }
            catch (const std::exception&) {} // release still targets only our retained handle
            if (WaitForSingleObject(_process, timeoutMs) == WAIT_TIMEOUT)
            {
                if (!TerminateProcess(_process, 1) && Running()) throw std::runtime_error("Owned Sentence process termination failed");
                WaitForSingleObject(_process, INFINITE); // retain ownership until termination completes
            }
        }
        CloseHandle(_process); _process = nullptr;
    }
    OwnedSentenceProcess::~OwnedSentenceProcess()
    {
        try { Stop(); } catch (...) { if (_process) CloseHandle(_process); }
    }
}
