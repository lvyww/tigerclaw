#include "SentenceQwenNative.h"
#include "SentencePipeIo.h"

#include <Windows.h>
#include <shellapi.h>

#include <nlohmann/json.hpp>

#include <algorithm>
#include <cstdint>
#include <filesystem>
#include <iomanip>
#include <memory>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace
{
    constexpr wchar_t DefaultPipeName[] = L"TigerClaw.Sentence.v1";
    constexpr wchar_t DefaultMutexName[] = L"Local\\TigerClaw.Sentence.SingleInstance";
    constexpr char ProviderName[] = "llama.cpp-cpu-q8";
    constexpr std::size_t MaximumRequestBytes = 1024 * 1024;

    struct HandleCloser
    {
        void operator()(void* handle) const
        {
            if (handle != nullptr && handle != INVALID_HANDLE_VALUE)
            {
                CloseHandle(handle);
            }
        }
    };

    using UniqueHandle = std::unique_ptr<void, HandleCloser>;
    using Json = nlohmann::json;

    struct Options
    {
        std::wstring PipeName = DefaultPipeName;
        std::filesystem::path ModelPath;
        DWORD ParentPid = 0;
    };

    std::string Utf8FromWide(const std::wstring& value)
    {
        if (value.empty())
        {
            return {};
        }
        const int required = WideCharToMultiByte(
            CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()),
            nullptr, 0, nullptr, nullptr);
        if (required <= 0)
        {
            throw std::runtime_error("cannot convert a command-line value to UTF-8");
        }
        std::string result(static_cast<std::size_t>(required), '\0');
        if (WideCharToMultiByte(
                CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()),
                result.data(), required, nullptr, nullptr) != required)
        {
            throw std::runtime_error("cannot convert a command-line value to UTF-8");
        }
        return result;
    }

    DWORD ParseProcessId(const std::wstring& value)
    {
        std::size_t consumed = 0;
        const unsigned long parsed = std::stoul(value, &consumed, 10);
        if (consumed != value.size() || parsed > MAXDWORD)
        {
            throw std::invalid_argument("invalid --parent-pid value");
        }
        return static_cast<DWORD>(parsed);
    }

    Options ParseOptions(int argc, wchar_t** argv)
    {
        Options result;
        for (int index = 1; index < argc; ++index)
        {
            const std::wstring option = argv[index];
            if (index + 1 >= argc)
            {
                throw std::invalid_argument("missing value for " + Utf8FromWide(option));
            }
            const std::wstring value = argv[++index];
            if (option == L"--pipe")
            {
                result.PipeName = value;
            }
            else if (option == L"--model")
            {
                result.ModelPath = std::filesystem::absolute(value);
            }
            else if (option == L"--parent-pid")
            {
                result.ParentPid = ParseProcessId(value);
            }
            else
            {
                throw std::invalid_argument("unknown option: " + Utf8FromWide(option));
            }
        }
        if (result.PipeName.empty())
        {
            throw std::invalid_argument("--pipe must not be empty");
        }
        if (result.ModelPath.empty())
        {
            throw std::invalid_argument("--model is required");
        }
        return result;
    }

    std::wstring BuildMutexName(const std::wstring& pipeName)
    {
        if (pipeName == DefaultPipeName)
        {
            return DefaultMutexName;
        }

        std::uint64_t hash = 14695981039346656037ull;
        for (wchar_t character : pipeName)
        {
            hash ^= static_cast<std::uint16_t>(character);
            hash *= 1099511628211ull;
        }
        std::wostringstream text;
        text << L"Local\\TigerClaw.Sentence.Test."
             << std::hex << std::setw(16) << std::setfill(L'0') << hash;
        return text.str();
    }

    void StartParentWatcher(DWORD parentPid)
    {
        if (parentPid == 0)
        {
            return;
        }

        HANDLE parent = OpenProcess(SYNCHRONIZE, FALSE, parentPid);
        if (parent == nullptr)
        {
            ExitProcess(0);
        }
        std::thread([parent]()
        {
            WaitForSingleObject(parent, INFINITE);
            CloseHandle(parent);
            ExitProcess(0);
        }).detach();
    }

    class Scorer
    {
    public:
        explicit Scorer(const std::filesystem::path& modelPath)
        {
            const std::string path = Utf8FromWide(modelPath.wstring());
            Check(tcs_create_from_file(path.c_str(), &_handle));
            if (_handle == nullptr)
            {
                throw std::runtime_error("native Qwen loader returned an empty handle");
            }
        }

        ~Scorer()
        {
            tcs_destroy(_handle);
        }

        std::vector<double> Score(const std::vector<std::string>& candidates, HANDLE pipe) const
        {
            std::vector<const char*> pointers;
            pointers.reserve(candidates.size());
            for (const std::string& candidate : candidates)
            {
                pointers.push_back(candidate.c_str());
            }
            std::vector<double> scores(candidates.size());
            Check(tcs_score_cancellable(
                _handle,
                pointers.data(),
                static_cast<std::int32_t>(pointers.size()),
                scores.data(), tigerclaw::sentence::Disconnected, pipe));
            return scores;
        }

    private:
        static void Check(int result)
        {
            if (result == 0)
            {
                return;
            }
            const char* error = tcs_last_error();
            throw std::runtime_error(
                error == nullptr || *error == '\0' ? "native Qwen operation failed" : error);
        }

        void* _handle = nullptr;
    };

    std::int64_t ReadInteger(const Json& value, const char* name)
    {
        const auto item = value.find(name);
        if (item == value.end() || item->is_null())
        {
            return 0;
        }
        if (!item->is_number_integer())
        {
            throw std::invalid_argument(std::string(name) + " must be an integer");
        }
        return item->get<std::int64_t>();
    }

    std::string ReadString(const Json& value, const char* name)
    {
        const auto item = value.find(name);
        if (item == value.end() || item->is_null())
        {
            return {};
        }
        if (!item->is_string())
        {
            throw std::invalid_argument(std::string(name) + " must be a string");
        }
        return item->get<std::string>();
    }

    Json NewResponse()
    {
        return {
            {"type", "response"},
            {"seq", 0},
            {"generation", 0},
            {"raw_code", nullptr},
            {"success", false},
            {"provider", nullptr},
            {"scores", nullptr},
            {"error", nullptr}
        };
    }

    Json HandleRequest(HANDLE pipe, const Json& request, const Scorer& scorer, bool& stopping)
    {
        if (!request.is_object())
        {
            throw std::invalid_argument("request must be a JSON object");
        }

        const std::string type = ReadString(request, "type");
        const std::int64_t seq = ReadInteger(request, "seq");
        if (type == "hello" || type == "ping")
        {
            Json response = NewResponse();
            response["seq"] = seq;
            response["success"] = true;
            response["provider"] = ProviderName;
            return response;
        }
        if (type == "shutdown")
        {
            stopping = true;
            Json response = NewResponse();
            response["seq"] = seq;
            response["success"] = true;
            return response;
        }
        if (type != "rerank")
        {
            throw std::invalid_argument("Unsupported request type: " + type);
        }

        const auto candidatesItem = request.find("candidates");
        if (candidatesItem == request.end() || !candidatesItem->is_array())
        {
            throw std::invalid_argument("rerank candidates must contain 1 to 5 items.");
        }
        std::vector<std::string> candidates;
        for (const Json& candidate : *candidatesItem)
        {
            if (!candidate.is_string())
            {
                throw std::invalid_argument("candidate text must be a string");
            }
            candidates.push_back(candidate.get<std::string>());
        }
        if (candidates.empty() || candidates.size() > 5)
        {
            throw std::invalid_argument("rerank candidates must contain 1 to 5 items.");
        }

        const std::int64_t generation = ReadInteger(request, "generation");
        const std::string rawCode = ReadString(request, "raw_code");
        Json response = NewResponse();
        response["seq"] = seq;
        response["generation"] = generation;
        response["raw_code"] = rawCode;
        response["success"] = true;
        response["provider"] = ProviderName;
        response["scores"] = scorer.Score(candidates, pipe);
        return response;
    }

    bool WriteAll(HANDLE pipe, const std::string& value)
    {
        std::size_t offset = 0;
        while (offset < value.size())
        {
            DWORD written = 0;
            const DWORD remaining = static_cast<DWORD>(
                std::min<std::size_t>(value.size() - offset, MAXDWORD));
            if (!tigerclaw::sentence::Write(pipe, value.data() + offset, remaining, written) || written == 0)
            {
                return false;
            }
            offset += written;
        }
        return true;
    }

    bool ProcessLine(HANDLE pipe, const std::string& line, const Scorer& scorer, bool& stopping)
    {
        Json response;
        try
        {
            response = HandleRequest(pipe, Json::parse(line), scorer, stopping);
        }
        catch (const std::exception& error)
        {
            response = NewResponse();
            response["error"] = error.what();
        }
        const std::string encoded = response.dump() + "\n";
        return WriteAll(pipe, encoded);
    }

    void ServeClient(HANDLE pipe, const Scorer& scorer, bool& stopping)
    {
        std::string pending;
        char buffer[4096];
        while (!stopping)
        {
            DWORD read = 0;
            if (!tigerclaw::sentence::Read(pipe, buffer, sizeof(buffer), read) || read == 0)
            {
                return;
            }
            pending.append(buffer, read);
            if (pending.size() > MaximumRequestBytes)
            {
                ProcessLine(pipe, "", scorer, stopping);
                return;
            }

            std::size_t newline = 0;
            while ((newline = pending.find('\n')) != std::string::npos)
            {
                std::string line = pending.substr(0, newline);
                pending.erase(0, newline + 1);
                if (!line.empty() && line.back() == '\r')
                {
                    line.pop_back();
                }
                if (!ProcessLine(pipe, line, scorer, stopping))
                {
                    return;
                }
                if (stopping)
                {
                    FlushFileBuffers(pipe);
                    return;
                }
            }
        }
    }

    int Run(int argc, wchar_t** argv)
    {
        const Options options = ParseOptions(argc, argv);
        const std::wstring mutexName = BuildMutexName(options.PipeName);
        HANDLE mutexHandle = CreateMutexW(nullptr, TRUE, mutexName.c_str());
        const DWORD mutexError = GetLastError();
        UniqueHandle mutex(mutexHandle);
        if (!mutex || mutexError == ERROR_ALREADY_EXISTS)
        {
            return 0;
        }

        StartParentWatcher(options.ParentPid);
        Scorer scorer(options.ModelPath);
        const std::wstring pipePath = L"\\\\.\\pipe\\" + options.PipeName;
        bool stopping = false;
        while (!stopping)
        {
            UniqueHandle pipe(CreateNamedPipeW(
                pipePath.c_str(),
                PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                1,
                4096,
                4096,
                0,
                nullptr));
            if (!pipe)
            {
                throw std::runtime_error("cannot create sentence named pipe");
            }

            if (!tigerclaw::sentence::Connect(pipe.get()))
            {
                continue;
            }
            ServeClient(pipe.get(), scorer, stopping);
            FlushFileBuffers(pipe.get());
            DisconnectNamedPipe(pipe.get());
        }
        return 0;
    }
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    int argc = 0;
    wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (argv == nullptr)
    {
        return 2;
    }
    try
    {
        const int result = Run(argc, argv);
        LocalFree(argv);
        return result;
    }
    catch (const std::exception& error)
    {
        OutputDebugStringA(error.what());
        OutputDebugStringA("\n");
        LocalFree(argv);
        return 3;
    }
}
