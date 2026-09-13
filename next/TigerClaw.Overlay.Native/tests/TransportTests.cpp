#include "Transport.h"
#include "SnapshotChannel.h"
#include <cstring>
#include <exception>
#include <iostream>
#include <memory>

using namespace tiger::overlay;
static void Check(bool ok, const char* message)
{
    if (!ok) throw std::runtime_error(message);
}
static Endpoints Isolated(const wchar_t* suffix)
{
    auto name = L"TigerClaw.Overlay.Test." + std::to_wstring(GetCurrentProcessId()) + L"." +
        std::to_wstring(GetTickCount64()) + suffix;
    Endpoints endpoints;
    endpoints.pipe = L"\\\\.\\pipe\\" + name;
    endpoints.ui = L"Local\\" + name + L".Ui";
    endpoints.coreHeartbeat = L"Local\\" + name + L".Core";
    endpoints.overlayHeartbeat = L"Local\\" + name + L".Overlay";
    endpoints.menu = L"Local\\" + name + L".Menu";
    return endpoints;
}
template<class Condition> static bool PumpUntil(Condition condition, DWORD timeout = 5000)
{
    auto deadline = GetTickCount64() + timeout;
    do
    {
        if (condition()) return true;
        MSG message{};
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) DispatchMessageW(&message);
        Sleep(5);
    } while (GetTickCount64() < deadline);
    return condition();
}
static int stateMessages = 0, menuMessages = 0, staleMessages = 0;
static LRESULT CALLBACK WindowProcedure(HWND window, UINT message, WPARAM wp, LPARAM lp)
{
    if (message == StateMessage) ++stateMessages;
    if (message == MenuMessage) ++menuMessages;
    if (message == StaleMessage) ++staleMessages;
    return DefWindowProcW(window, message, wp, lp);
}
class Server
{
    Handle pipe_, stop_{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    Handle received_{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    std::exception_ptr error_;
    std::string response_;
    bool stall_;
    std::thread worker_;
    void Complete(BOOL immediate, OVERLAPPED& operation, DWORD& bytes)
    {
        if (immediate) return;
        if (GetLastError() == ERROR_PIPE_CONNECTED) return;
        Check(GetLastError() == ERROR_IO_PENDING, "server IO submission");
        HANDLE waits[] = {operation.hEvent, stop_.Get()};
        if (WaitForMultipleObjects(2, waits, FALSE, 5000) == WAIT_OBJECT_0)
        {
            Check(GetOverlappedResult(pipe_.Get(), &operation, &bytes, FALSE) != FALSE, "server IO completion");
            return;
        }
        CancelIoEx(pipe_.Get(), &operation);
        GetOverlappedResult(pipe_.Get(), &operation, &bytes, TRUE);
        throw std::runtime_error("server IO cancelled");
    }
    void Run()
    {
        try
        {
            Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
            OVERLAPPED operation{}; operation.hEvent = event.Get();
            DWORD bytes = 0;
            Complete(ConnectNamedPipe(pipe_.Get(), &operation), operation, bytes);
            std::string request;
            do
            {
                char buffer[1024];
                ResetEvent(event.Get()); operation = {}; operation.hEvent = event.Get();
                Complete(ReadFile(pipe_.Get(), buffer, sizeof(buffer), &bytes, &operation), operation, bytes);
                Check(bytes != 0, "nonempty request"); request.append(buffer, bytes);
            } while (request.find('\n') == std::string::npos);
            SetEvent(received_.Get());
            if (stall_) { WaitForSingleObject(stop_.Get(), 5000); return; }
            // Force the client to assemble a response spanning multiple reads.
            for (const auto& part : {response_.substr(0, response_.size() / 2), response_.substr(response_.size() / 2)})
            {
                ResetEvent(event.Get()); operation = {}; operation.hEvent = event.Get();
                Complete(WriteFile(pipe_.Get(), part.data(), static_cast<DWORD>(part.size()), &bytes, &operation), operation, bytes);
                Sleep(10);
            }
        }
        catch (...) { error_ = std::current_exception(); }
    }
public:
    Server(const Endpoints& endpoints, std::string response, bool stall = false) : response_(std::move(response)), stall_(stall)
    {
        pipe_.Reset(CreateNamedPipeW(endpoints.pipe.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT, 1, 4096, 4096, 0, nullptr));
        Check(static_cast<bool>(pipe_), "isolated server creation");
        worker_ = std::thread([this] { Run(); });
    }
    ~Server() { SetEvent(stop_.Get()); if (worker_.joinable()) worker_.join(); }
    void Finish() { if (worker_.joinable()) worker_.join(); if (error_) std::rethrow_exception(error_); }
    bool WaitForRequest() { return WaitForSingleObject(received_.Get(), 5000) == WAIT_OBJECT_0; }
};

static void SnapshotTests()
{
    auto endpoints = Isolated(L"snapshot");
    auto name = endpoints.ui + L".Snapshot.v2";
    Mapping map;
    Check(map.Open(name.c_str(), 128 * 1024, true), "snapshot test map");
    Handle gate(CreateMutexW(nullptr, FALSE, (name + L".Lock").c_str()));
    SnapshotChannel channel(endpoints.ui);
    auto write = [&](std::int64_t sequence, std::int64_t tick, const std::string& text)
    {
        std::int64_t zero = 0;
        int length = static_cast<int>(text.size());
        std::memcpy(map.Data(), &zero, 8);
        std::memcpy(static_cast<char*>(map.Data()) + 8, &tick, 8);
        std::memcpy(static_cast<char*>(map.Data()) + 16, &length, 4);
        std::memcpy(static_cast<char*>(map.Data()) + 20, text.data(), text.size());
        MemoryBarrier(); std::memcpy(map.Data(), &sequence, 8);
    };
    Check(WaitForSingleObject(gate.Get(), 1000) == WAIT_OBJECT_0, "snapshot writer lock");
    write(1, 100, "complete"); ReleaseMutex(gate.Get());
    std::string payload;
    Check(channel.Read(1, 100, payload) == SnapshotResult::Ready && payload == "complete", "first read accepts committed snapshot without another poll");
    Check(channel.Read(2, 101, payload) == SnapshotResult::Legacy, "old publisher downgrade cannot reuse stale snapshot");
    Handle locked(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    Handle finish(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    std::thread writer([&]
    {
        WaitForSingleObject(gate.Get(), INFINITE);
        write(0, 101, "partial");
        SetEvent(locked.Get()); WaitForSingleObject(finish.Get(), 2000);
        // Simulate termination before committing; abandon mutex deliberately.
    });
    Check(WaitForSingleObject(locked.Get(), 1000) == WAIT_OBJECT_0, "paused writer ready");
    auto busy = channel.Read(2, 101, payload);
    SetEvent(finish.Get()); writer.join();
    Check(busy == SnapshotResult::Busy, "paused writer never exposes partial bytes");
    Check(channel.Read(2, 101, payload) == SnapshotResult::Legacy, "abandoned incomplete snapshot rejected");
    Check(WaitForSingleObject(gate.Get(), 1000) == WAIT_OBJECT_0, "abandoned gate recovered");
    write(2, 101, "recovered"); ReleaseMutex(gate.Get());
    SetEvent(channel.Changed());
    Check(WaitForSingleObject(channel.Changed(), 0) == WAIT_OBJECT_0, "change event signaled");
    Check(channel.Read(2, 101, payload) == SnapshotResult::Ready && payload == "recovered", "snapshot recovers after writer failure");
}

static void StateTests(HWND window)
{
    auto endpoints = Isolated(L"state");
    Mapping ui, core;
    Check(ui.Open(endpoints.ui.c_str(), 128 * 1024, true), "isolated UI map");
    Check(core.Open(endpoints.coreHeartbeat.c_str(), 16, true), "isolated Core heartbeat");
    auto heartbeat = GetTickCount64();
    std::memcpy(static_cast<char*>(core.Data()) + 8, &heartbeat, 8);
    auto publish = [&](std::int64_t sequence, std::string payload)
    {
        int length = static_cast<int>(payload.size());
        auto tick = GetTickCount64();
        std::memcpy(ui.Data(), &sequence, 8);
        std::memcpy(static_cast<char*>(ui.Data()) + 8, &tick, 8);
        std::memcpy(static_cast<char*>(ui.Data()) + 16, &length, 4);
        std::memcpy(static_cast<char*>(ui.Data()) + 20, payload.data(), payload.size());
        MemoryBarrier();
    };
    StateSource source(window, endpoints);
    State state;
    publish(1, R"({"CandidateVisible":true,"InputCode":"ab"})");
    Check(PumpUntil([&] { return source.Take(state); }), "initial state delivered");
    Check(state.input == u"ab", "initial state contents");
    Check(PumpUntil([&] { return stateMessages != 0; }), "initial notification drained");
    int messages = stateMessages;
    for (int i = 2; i < 12; ++i)
    {
        publish(i, "{\"InputCode\":\"" + std::to_string(i) + "\"}");
        Sleep(20);
    }
    Check(PumpUntil([&] { return stateMessages > messages; }), "pending update notification");
    // Inspect notification coalescing before consuming the mailbox. Taking an
    // intermediate snapshot legitimately permits another notification later.
    Check(stateMessages == messages + 1, "UI notifications coalesced");
    Check(PumpUntil([&] { return source.Take(state) && state.input == u"11"; }), "latest state wins");
    publish(12, "{");
    Sleep(30);
    Check(!source.Take(state), "invalid snapshot never delivered");
    publish(1, R"({"InputCode":"restart"})");
    Check(PumpUntil([&] { return source.Take(state); }) && state.input == u"restart", "sequence reset accepted");
    Handle menu(OpenEventW(EVENT_MODIFY_STATE, FALSE, endpoints.menu.c_str()));
    Check(static_cast<bool>(menu), "isolated menu event exists");
    SetEvent(menu.Get());
    Check(PumpUntil([&] { return menuMessages != 0; }), "menu signal delivered");
    Mapping own;
    Check(PumpUntil([&] { return own.Open(endpoints.overlayHeartbeat.c_str(), 16); }), "own heartbeat created");
    std::uint64_t tick = 0;
    Check(PumpUntil([&] { std::memcpy(&tick, static_cast<char*>(own.Data()) + 8, 8); return tick != 0; }), "own heartbeat live");
    if (GetTickCount64() > 11000)
    {
        tick = GetTickCount64() - 10001;
        std::memcpy(static_cast<char*>(core.Data()) + 8, &tick, 8);
        Check(PumpUntil([&] { return staleMessages != 0; }), "stale Core detected");
    }
}

int main()
{
    try
    {
        SnapshotTests();
        WNDCLASSW type{}; type.hInstance = GetModuleHandleW(nullptr); type.lpfnWndProc = WindowProcedure;
        type.lpszClassName = L"TigerClaw.Overlay.TransportTest";
        Check(RegisterClassW(&type) != 0, "test window class");
        HWND window = CreateWindowW(type.lpszClassName, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, type.hInstance, nullptr);
        Check(window != nullptr, "message-only test window");
        StateTests(window);
        for (const auto& response : {std::string("{\"success\":true,\"seq\":1,\"error\":null}\n"),
            std::string("{\"success\":true,\"seq\":99}\n"), std::string("invalid\n")})
        {
            auto endpoints = Isolated(L"pipe");
            Server server(endpoints, response);
            CommandQueue client(window, endpoints);
            Check(client.Send("get_schema_list", 42), "command queued");
            Reply reply;
            Check(PumpUntil([&] { return client.Take(reply); }), "pipe reply delivered");
            Check(reply.tag == 42, "reply routing");
            Check(reply.success == (response.find("null") != std::string::npos), "response validation");
            server.Finish();
        }
        {
            auto endpoints = Isolated(L"batch-failure");
            Server server(endpoints, "{\"success\":false,\"seq\":1}\n");
            CommandQueue client(window, endpoints);
            auto start = GetTickCount64();
            Check(client.SetConfigs({{u"one", u"yes"}, {u"two", u"yes"}}, 8), "setting batch queued");
            Reply reply;
            Check(PumpUntil([&] { return client.Take(reply); }, 2000), "batch stops before second connection");
            Check(!reply.success && reply.tag == 8 && GetTickCount64() - start < 2000, "batch failure routed");
            server.Finish();
        }
        {
            auto endpoints = Isolated(L"cancel");
            Server server(endpoints, "", true);
            auto client = std::make_unique<CommandQueue>(window, endpoints);
            client->Send("get_schema_list");
            Check(server.WaitForRequest(), "stalled request started");
            auto start = GetTickCount64(); client.reset();
            Check(GetTickCount64() - start < 1500, "dispose cancels and drains pending read");
        }
        {
            auto endpoints = Isolated(L"absent");
            CommandQueue client(window, endpoints);
            client.Send("get_schema_list");
            Reply reply;
            Check(PumpUntil([&] { return client.Take(reply); }, 5000) && !reply.success, "absent Core times out");
        }
        DestroyWindow(window);
        std::cout << "Isolated native Overlay transport tests passed\n";
        return 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
