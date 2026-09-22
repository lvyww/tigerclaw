// Standalone Windows fault tests; never connect to the user's BimeIPC pipe.
#include <windows.h>
#include <string>
#include <thread>
#include <cstdio>
#include <cstdlib>
#define private public
#include "../BimeTSF2/SampleIME/PipeClient.h"
#undef private
static wchar_t connectionPipeName[128];
#undef BIME_PIPE_NAME
#define BIME_PIPE_NAME connectionPipeName
#include "../BimeTSF2/SampleIME/PipeClient.cpp"

namespace Global
{
    void LogToFile(const char *, ...) {}
    void LogToFileVerbose(const char *, ...) {}
}

static void Check(bool value, const char *name)
{
    if (!value) { std::printf("FAIL %s error=%lu\n", name, GetLastError()); std::exit(1); }
}

struct TestPipe
{
    HANDLE server;
    CPipeClient client;
    TestPipe()
    {
        static int sequence = 0;
        wchar_t name[128];
        swprintf_s(name, L"\\\\.\\pipe\\TigerClaw.PipeTest.%lu.%d", GetCurrentProcessId(), ++sequence);
        server = CreateNamedPipeW(name, PIPE_ACCESS_DUPLEX, PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
            1, 4096, 4096, 0, nullptr);
        Check(server != INVALID_HANDLE_VALUE, "create server");
        client._hPipe = CreateFileW(name, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
        Check(client._hPipe != INVALID_HANDLE_VALUE, "connect client");
        client._isConnected = TRUE;
        Check(ConnectNamedPipe(server, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED, "connect server");
    }
    ~TestPipe() { client.Disconnect(); CloseHandle(server); }
};

static CPipeClient *timerClient;
static bool busyRejected;
static void CALLBACK Reenter(HWND, UINT, UINT_PTR id, DWORD)
{
    KillTimer(nullptr, id);
    BimeResponse response;
    busyRejected = timerClient->SendQueryStateAndWait(&response, 20) == HRESULT_FROM_WIN32(ERROR_BUSY);
}
static void CALLBACK Quit(HWND, UINT, UINT_PTR id, DWORD)
{
    KillTimer(nullptr, id);
    PostQuitMessage(37);
}

int main()
{
    swprintf_s(connectionPipeName, L"\\\\.\\pipe\\TigerClaw.TsfConnectTest.%lu", GetCurrentProcessId());
    for (int attempt = 0; attempt < 2; ++attempt)
    {
        HANDLE server = CreateNamedPipeW(connectionPipeName, PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT, 1, 4096, 4096, 0, nullptr);
        Check(server != INVALID_HANDLE_VALUE, "connection server");
        CPipeClient client;
        Check(client.Connect(), "connect arbitrary server executable");
        Check(ConnectNamedPipe(server, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED, "connection accepted");
        client.Disconnect();
        CloseHandle(server);
    }
    {
        TestPipe pipe;
        Check(pipe.client.SendShowMenuAndWait(nullptr, 100) == E_INVALIDARG, "menu null response rejected");
        std::thread server([&] {
            char request[256]{}; DWORD count = 0;
            Check(ReadFile(pipe.server, request, sizeof(request) - 1, &count, nullptr), "menu request read");
            Check(std::string(request, count) == "{\"type\":\"show_menu\",\"seq\":1}\n", "menu wire contract unchanged");
            const char reply[] = "{\"seq\":1,\"success\":true,\"handled\":true}\n";
            WriteFile(pipe.server, reply, sizeof(reply) - 1, &count, nullptr);
        });
        BimeResponse response;
        Check(SUCCEEDED(pipe.client.SendShowMenuAndWait(&response, 1000)), "menu response after foreground grant attempt");
        server.join();
        Check(response.seq == 1 && !pipe.client.IsBusy(), "menu response sequence and guard");
    }
    for (int candidateAction : {2, 18, 34, 50})
    {
        TestPipe pipe;
        std::thread server([&] {
            char request[768]{}; DWORD count = 0;
            Check(ReadFile(pipe.server, request, sizeof(request) - 1, &count, nullptr), "candidate request read");
            std::string wire(request, count);
            Check(wire.find("\"candidate_token\":\"0123456789abcdef0123456789abcdef\"") != std::string::npos &&
                  wire.find("\"event_id\":\"77\"") != std::string::npos && wire.find("\"scan\":" + std::to_string(candidateAction) + ",") != std::string::npos,
                  "candidate token/index/replay identity on wire");
            const char reply[] = "{\"seq\":1,\"success\":true,\"handled\":true,\"input_buffer\":\"nihao\",\"input_cursor\":2}\n";
            WriteFile(pipe.server, reply, sizeof(reply) - 1, &count, nullptr);
            ReadFile(pipe.server, request, sizeof(request) - 1, &count, nullptr);
            const char legacy[] = "{\"seq\":2,\"success\":true,\"handled\":false}\n";
            WriteFile(pipe.server, legacy, sizeof(legacy) - 1, &count, nullptr);
        });
        BimeResponse response;
        Check(SUCCEEDED(pipe.client.SendCandidateAndWait("0123456789abcdef0123456789abcdef", candidateAction, 77, &response, 1000)), "candidate response");
        Check(response.inputCursor == 2 && response.inputBuffer == L"nihao", "preedit cursor parsed");
        Check(SUCCEEDED(pipe.client.SendQueryStateAndWait(&response, 1000)) && response.inputCursor == -1, "legacy reply resets cursor");
        server.join();
    }
    for (int i = 0; i < 20; ++i)
    {
        TestPipe pipe;
        BimeResponse response;
        Check(FAILED(pipe.client.SendMessageAndWait("{\"seq\":1}\n", &response, 2)), "read timeout");
        Check(!pipe.client.IsBusy(), "guard released on timeout");
    }
    {
        TestPipe pipe;
        std::string large(1024 * 1024, 'x');
        ULONGLONG started = GetTickCount64();
        Check(!pipe.client.WriteMessageOverlapped(large.data(), large.size(), 20), "write cancellation");
        Check(GetTickCount64() - started < 500, "write cancellation completes");
    }
    {
        TestPipe pipe;
        timerClient = &pipe.client;
        std::string large(1024 * 1024, 'x');
        std::thread server([&] {
            Sleep(150);
            std::string buffer(large.size(), '\0'); DWORD count;
            ReadFile(pipe.server, &buffer[0], static_cast<DWORD>(buffer.size()), &count, nullptr);
        });
        BimeResponse response;
        ULONGLONG started = GetTickCount64();
        Check(FAILED(pipe.client.SendMessageAndWait(large.c_str(), &response, 200)), "combined timeout");
        ULONGLONG elapsed = GetTickCount64() - started;
        server.join();
        Check(elapsed < 300, "write and read share one deadline");
    }
    {
        TestPipe pipe;
        timerClient = &pipe.client;
        Check(SetTimer(nullptr, 0, 10, Reenter) != 0, "reentry timer");
        std::thread server([&] {
            char request[256]; DWORD count;
            ReadFile(pipe.server, request, sizeof(request), &count, nullptr);
            Sleep(80);
            const char reply[] = "{\"seq\":1,\"success\":true,\"handled\":true}\n";
            WriteFile(pipe.server, reply, sizeof(reply) - 1, &count, nullptr);
        });
        BimeResponse response;
        Check(SUCCEEDED(pipe.client.SendMessageAndWait("{\"seq\":1}\n", &response, 1000)), "outer response survives reentry");
        server.join();
        Check(busyRejected && response.seq == 1, "nested request rejected");
    }
    {
        TestPipe pipe;
        Check(SetTimer(nullptr, 0, 10, Quit) != 0, "quit timer");
        BimeResponse response;
        Check(FAILED(pipe.client.SendMessageAndWait("{\"seq\":1}\n", &response, 500)), "quit aborts read");
        MSG message;
        Check(PeekMessageW(&message, nullptr, WM_QUIT, WM_QUIT, PM_REMOVE) && message.wParam == 37,
            "quit preserved once");
    }
    std::puts("TSF pipe fault tests passed");
}
