// Standalone Windows fault tests; never connect to the user's BimeIPC pipe.
#include <windows.h>
#include <string>
#include <thread>
#include <cstdio>
#include <cstdlib>
#define private public
#include "../BimeTSF2/SampleIME/PipeClient.h"
#undef private
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
