// Isolated native frontend tests: no global hook and no connection to BimeIPC.
#include <windows.h>
#include <string>
#include <vector>
#include <functional>
#include <algorithm>
#include <sstream>
#include <thread>
#include <cstdio>
#define private public
#include "../next/TigerClaw.Hook.Native/IPC/PipeClient.h"
#undef private
#include "../next/TigerClaw.Hook.Native/IPC/PipeClient.cpp"
#include "../next/TigerClaw.Hook.Native/Replay/InputReplay.cpp"
namespace TigerClawHookNative
{
    void Logger::Info(const wchar_t*, const std::wstring&) {}
    void Logger::Info(const wchar_t*, const wchar_t*) {}
}
using namespace TigerClawHookNative;
static void Check(bool condition, const char* label)
{
    if (!condition) { std::printf("FAIL %s\n", label); std::exit(1); }
}
static HANDLE Attach(PipeClient& client)
{
    static int count = 0;
    wchar_t name[128];
    swprintf_s(name, L"\\\\.\\pipe\\TigerClaw.HookTest.%lu.%d", GetCurrentProcessId(), ++count);
    HANDLE server = CreateNamedPipeW(name, PIPE_ACCESS_DUPLEX, PIPE_TYPE_BYTE | PIPE_WAIT, 1, 4096, 4096, 0, nullptr);
    Check(server != INVALID_HANDLE_VALUE, "server");
    client._requestPipe = CreateFileW(name, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
    Check(client._requestPipe != INVALID_HANDLE_VALUE, "client");
    Check(ConnectNamedPipe(server, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED, "connected");
    return server;
}
int main()
{
    // Exercise both real connection paths with this test executable as server.
    // PipeName is local to the included implementation; production is untouched.
    wchar_t connectionName[128];
    swprintf_s(connectionName, L"\\\\.\\pipe\\TigerClaw.HookConnectTest.%lu", GetCurrentProcessId());
    PipeName = connectionName;
    for (bool notify : {false, true})
    for (int attempt = 0; attempt < 2; ++attempt)
    {
        HANDLE server = CreateNamedPipeW(PipeName, PIPE_ACCESS_DUPLEX, PIPE_TYPE_BYTE | PIPE_WAIT,
            1, 4096, 4096, 0, nullptr);
        Check(server != INVALID_HANDLE_VALUE, "connection server");
        PipeClient client;
        std::wstring error;
        Check(notify ? client.EnsureNotifyPipe(error) : client.EnsureRequestPipe(error), "connect arbitrary server executable");
        Check(ConnectNamedPipe(server, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED, "connection accepted");
        client.DisconnectRequestPipe();
        client.DisconnectNotifyPipe();
        CloseHandle(server);
    }
    for (auto pair : { std::pair<UINT,UINT>{VK_LSHIFT,VK_RSHIFT}, {VK_LCONTROL,VK_RCONTROL}, {VK_LMENU,VK_RMENU}, {VK_LWIN,VK_RWIN} })
    for (bool reverse : {false,true})
    {
        HookState state;
        UINT first = reverse ? pair.second : pair.first, second = reverse ? pair.first : pair.second;
        auto held = [&] { return state.ShiftDown() || state.CtrlDown() || state.AltDown() || state.WinDown(); };
        state.UpdateModifierState(first, true, false);
        state.UpdateModifierState(second, true, false);
        state.UpdateModifierState(first, false, true);
        Check(held(), "opposite modifier stays down");
        state.UpdateModifierState(second, false, true);
        Check(!held(), "both modifiers released");
    }
    InputReplay replay;
    Check(replay.ShouldSuppressHookEvent(LLKHF_INJECTED, InputReplay::ReplayMarker), "delayed replay excluded");
    Check(!replay.ShouldSuppressHookEvent(0, 0), "physical key retained");
    PipeClient client;
    HookState state;
    KeyboardHookEvent event;
    event.VirtualKey = 'M'; event.IsKeyDown = true;
    state.UpdateModifierState(VK_LCONTROL, true, false);
    std::string request = client.PrepareKey(event, state, {});
    state.UpdateModifierState(VK_LCONTROL, false, true);
    Check(request.find("\"ctrl\":true") != std::string::npos, "frozen modifier snapshot");
    Check(request.find("\"client_session\":\"hook-") != std::string::npos && request.find("\"event_id\":\"1\"") != std::string::npos, "event identity");
    Check(client.PrepareKey(event, state, {}).find("\"event_id\":\"2\"") != std::string::npos, "next physical event identity");
    HANDLE server = Attach(client);
    CoreResponse response; std::wstring error;
    Check(!client.TrySendRequest(request, response, error), "timeout holds request");
    Check(client.NeedsFocusSync(), "reconnect invalidates focus");
    CloseHandle(server);
    server = Attach(client);
    std::thread responder([&] {
        std::string received; char buffer[256]; DWORD count;
        while (received.find('\n') == std::string::npos)
        {
            Check(ReadFile(server, buffer, sizeof(buffer), &count, nullptr) && count > 0, "server read");
            received.append(buffer, count);
        }
        Check(received == request + "\n", "retry byte identity");
        auto start = request.find("\"seq\":") + 6;
        auto end = request.find(',', start);
        std::string reply = "{\"seq\":" + request.substr(start, end-start) + ",\"success\":true,\"handled\":true}\n";
        WriteFile(server, reply.data(), static_cast<DWORD>(reply.size()), &count, nullptr);
    });
    Check(client.TrySendRequest(request, response, error), "retry response");
    responder.join();
    client.DisconnectRequestPipe(); CloseHandle(server);

    // Cancellation and restored focus must precede the next key on one stream.
    server = Attach(client);
    Check(client.TryCancelForRecovery(error), "recovery cancellation sent");
    std::thread orderedResponder([&] {
        std::string received; char buffer[256]; DWORD count;
        while (std::count(received.begin(), received.end(), '\n') < 3)
        {
            Check(ReadFile(server, buffer, sizeof(buffer), &count, nullptr) && count > 0, "ordered read");
            received.append(buffer, count);
        }
        auto cancel = received.find("composition_canceled");
        auto focus = received.find("\"type\":\"focus\"");
        auto key = received.find(request);
        Check(cancel < focus && focus < key && key != std::string::npos, "cancel focus key ordering");
        auto start = request.find("\"seq\":") + 6;
        auto end = request.find(',', start);
        std::string reply = "{\"seq\":" + request.substr(start, end-start) + ",\"success\":true}\n";
        WriteFile(server, reply.data(), static_cast<DWORD>(reply.size()), &count, nullptr);
    });
    Check(client.TrySendPreparedKey(request, {}, response, error), "ordered recovery");
    orderedResponder.join();
    Check(client._requestFocusKnown, "request focus remembered");
    client.DisconnectRequestPipe(); CloseHandle(server);

    // A failed notification remains dirty; a successful write clears it.
    server = Attach(client);
    client._notifyPipe = client._requestPipe;
    client._requestPipe = INVALID_HANDLE_VALUE;
    Check(client.TrySendFocus({}, error) && !client.NeedsFocusSync(), "focus published");
    CloseHandle(server);
    Check(!client.TrySendFocus({}, error) && client.NeedsFocusSync(), "failed focus stays pending");

    server = Attach(client);
    const char wrongReply[] = "{\"seq\":-999,\"success\":true}\n";
    DWORD written;
    WriteFile(server, wrongReply, sizeof(wrongReply)-1, &written, nullptr);
    Check(!client.TrySendRequest(request, response, error), "mismatched response rejected");
    CloseHandle(server);
    std::puts("Native Hook alignment tests passed");
}
