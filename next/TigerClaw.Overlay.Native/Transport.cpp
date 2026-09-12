#include "Transport.h"
#include "SnapshotChannel.h"
#include <cstring>
#include <nlohmann/json.hpp>

namespace tiger::overlay
{
    StateSource::StateSource(HWND window, Endpoints endpoints) : window_(window), endpoints_(std::move(endpoints))
    {
        if (!stop_) throw std::runtime_error("State source stop event creation failed");
        worker_ = std::thread([this]
        {
            try { Run(); }
            catch (...) { PostMessageW(window_, StaleMessage, 0, 0); }
        });
    }
    StateSource::~StateSource() { SetEvent(stop_.Get()); if (worker_.joinable()) worker_.join(); }
    bool StateSource::Take(State& state)
    {
        std::lock_guard lock(mutex_);
        if (!pending_) return false;
        state = std::move(latest_); pending_ = false;
        return true;
    }
    void StateSource::Run()
    {
        Mapping ui, core, heartbeat;
        SnapshotChannel snapshots(endpoints_.ui);
        Handle menu(CreateEventW(nullptr, FALSE, FALSE, endpoints_.menu.c_str()));
        std::int64_t lastSequence = 0, lastTick = 0, heartbeatSequence = 0;
        auto lastHeartbeat = GetTickCount64();
        auto started = lastHeartbeat;
        std::string previousSnapshot;
        bool haveSnapshot = false;
        HANDLE waits[] = {stop_.Get(), snapshots.Changed()};
        for (;;)
        {
            DWORD wake = WaitForMultipleObjects(snapshots.Changed() ? 2 : 1, waits, FALSE, 5);
            if (wake == WAIT_OBJECT_0) break;
            if (wake == WAIT_FAILED) throw std::runtime_error("UI state wait failed");
            auto now = GetTickCount64();
            if (now - lastHeartbeat >= 250 || now - started < 10)
            {
                lastHeartbeat = now;
                if (heartbeat.Open(endpoints_.overlayHeartbeat.c_str(), 16, true))
                {
                    ++heartbeatSequence;
                    std::memcpy(heartbeat.Data(), &heartbeatSequence, 8);
                    std::memcpy(static_cast<char*>(heartbeat.Data()) + 8, &now, 8);
                }
                std::uint64_t tick = 0;
                if (core.Open(endpoints_.coreHeartbeat.c_str(), 16))
                    std::memcpy(&tick, static_cast<char*>(core.Data()) + 8, 8);
                if ((tick && now >= tick && now - tick > 10000) || (!tick && now - started > 10000))
                {
                    PostMessageW(window_, StaleMessage, 0, 0); return;
                }
            }
            if (menu && WaitForSingleObject(menu.Get(), 0) == WAIT_OBJECT_0)
                PostMessageW(window_, MenuMessage, 0, 0);
            if (!ui.Open(endpoints_.ui.c_str(), 128 * 1024)) continue;
            std::int64_t seq = 0, tick = 0, afterSeq = 0;
            int length = 0;
            std::memcpy(&seq, ui.Data(), 8);
            std::memcpy(&tick, static_cast<char*>(ui.Data()) + 8, 8);
            if (!seq || (seq == lastSequence && tick == lastTick)) continue;
            std::string payload;
            auto snapshot = snapshots.Read(seq, tick, payload);
            if (snapshot == SnapshotResult::Busy) continue;
            if (snapshot != SnapshotResult::Ready)
            {
                std::memcpy(&length, static_cast<char*>(ui.Data()) + 16, 4);
                if (length <= 0 || length > 128 * 1024 - 20) continue;
                payload.assign(static_cast<char*>(ui.Data()) + 20, static_cast<std::size_t>(length));
                MemoryBarrier();
                std::memcpy(&afterSeq, ui.Data(), 8);
                if (seq != afterSeq) { haveSnapshot = false; continue; }
                // Old Core fallback only: v1 has no synchronization contract.
                if (!haveSnapshot || previousSnapshot != payload)
                {
                    previousSnapshot = std::move(payload); haveSnapshot = true; continue;
                }
            }
            State state;
            if (!ParseState(payload, state)) continue;
            lastSequence = seq; lastTick = tick; haveSnapshot = false;
            bool notify;
            {
                std::lock_guard lock(mutex_);
                latest_ = std::move(state); notify = !pending_; pending_ = true;
            }
            if (notify) PostMessageW(window_, StateMessage, 0, 0);
        }
    }

    static Reply Exchange(const std::string& json, std::atomic<bool>& stopping, const std::wstring& pipeName)
    {
        Reply reply;
        auto deadline = GetTickCount64() + 3000;
        Handle pipe;
        while (!stopping && GetTickCount64() < deadline)
        {
            pipe.Reset(CreateFileW(pipeName.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr));
            if (pipe) break;
            DWORD error = GetLastError();
            if (error != ERROR_PIPE_BUSY && error != ERROR_FILE_NOT_FOUND) break;
            // WaitNamedPipe returns immediately when the name does not exist.
            // Avoid a tight CreateFile loop while Core is absent.
            if (error == ERROR_FILE_NOT_FOUND) Sleep(25);
            else WaitNamedPipeW(pipeName.c_str(), 50);
        }
        if (!pipe) { reply.error = "Core pipe unavailable"; return reply; }
        Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        if (!event) { reply.error = "Pipe event creation failed"; return reply; }
        auto io = [&](bool read, void* buffer, DWORD size, DWORD& transferred)
        {
            OVERLAPPED overlapped{}; overlapped.hEvent = event.Get();
            ResetEvent(event.Get());
            BOOL ok = read ? ReadFile(pipe.Get(), buffer, size, &transferred, &overlapped) :
                WriteFile(pipe.Get(), buffer, size, &transferred, &overlapped);
            if (ok) return true;
            if (GetLastError() != ERROR_IO_PENDING) return false;
            while (!stopping && GetTickCount64() < deadline)
            {
                if (WaitForSingleObject(event.Get(), 25) == WAIT_OBJECT_0)
                    return GetOverlappedResult(pipe.Get(), &overlapped, &transferred, FALSE) != FALSE;
            }
            CancelIoEx(pipe.Get(), &overlapped);
            // Drain completion before releasing stack OVERLAPPED and buffers.
            GetOverlappedResult(pipe.Get(), &overlapped, &transferred, TRUE);
            return false;
        };
        std::string request = json + '\n';
        DWORD transferred = 0;
        if (!io(false, request.data(), static_cast<DWORD>(request.size()), transferred) || transferred != request.size())
        { reply.error = "Core request failed"; return reply; }
        while (reply.json.size() < 1024 * 1024)
        {
            char buffer[4096];
            if (!io(true, buffer, sizeof(buffer), transferred) || !transferred)
            { reply.error = "Core response failed"; return reply; }
            reply.json.append(buffer, transferred);
            auto newline = reply.json.find('\n');
            if (newline == std::string::npos) continue;
            reply.json.resize(newline);
            try
            {
                auto response = nlohmann::json::parse(reply.json);
                reply.success = response.value("seq", 0LL) == 1 && response.value("success", false);
                auto error = response.find("error");
                if (error != response.end() && !error->is_null()) reply.error = error->get<std::string>();
            }
            catch (...) { reply.success = false; reply.error = "Invalid Core response"; }
            return reply;
        }
        reply.error = "Core response too large";
        return reply;
    }
    CommandQueue::CommandQueue(HWND window, Endpoints endpoints) : window_(window), endpoints_(std::move(endpoints)), worker_([this] { Run(); }) {}
    CommandQueue::~CommandQueue()
    {
        stopping_ = true; signal_.notify_all();
        if (worker_.joinable()) worker_.join();
    }
    bool CommandQueue::Send(std::string type, int tag)
    {
        nlohmann::json request{{"type", type}, {"seq", 1}};
        std::lock_guard lock(mutex_);
        if (stopping_ || pending_.size() >= 64) return false;
        pending_.push_back({{request.dump()}, tag});
        signal_.notify_one();
        return true;
    }
    void CommandQueue::SetConfig(std::u16string_view key, std::u16string_view value)
    {
        SetConfigs({{Text(key), Text(value)}}, 0);
    }
    bool CommandQueue::SetConfigs(const std::vector<std::pair<Text, Text>>& settings, int tag)
    {
        if (settings.empty()) return false;
        Command command{{}, tag};
        for (const auto& setting : settings)
        {
            nlohmann::json request{{"type", "set_config"}, {"seq", 1},
                {"key", ToUtf8(setting.first)}, {"value", ToUtf8(setting.second)}};
            command.requests.push_back(request.dump());
        }
        std::lock_guard lock(mutex_);
        if (stopping_ || pending_.size() >= 64) return false;
        pending_.push_back(std::move(command));
        signal_.notify_one();
        return true;
    }
    bool CommandQueue::Take(Reply& reply)
    {
        std::lock_guard lock(mutex_);
        if (replies_.empty()) return false;
        reply = std::move(replies_.front()); replies_.pop_front(); return true;
    }
    void CommandQueue::Run()
    {
        while (!stopping_)
        {
            Command command;
            {
                std::unique_lock lock(mutex_);
                signal_.wait(lock, [&] { return stopping_ || !pending_.empty(); });
                if (stopping_) return;
                command = std::move(pending_.front()); pending_.pop_front();
            }
            Reply reply;
            try
            {
                for (const auto& request : command.requests)
                {
                    if (stopping_) break;
                    reply = Exchange(request, stopping_, endpoints_.pipe);
                    if (!reply.success) break;
                }
            }
            catch (...) { reply.error = "Core command exception"; }
            reply.tag = command.tag;
            {
                std::lock_guard lock(mutex_);
                replies_.push_back(std::move(reply));
            }
            PostMessageW(window_, ReplyMessage, 0, 0);
        }
    }
}
