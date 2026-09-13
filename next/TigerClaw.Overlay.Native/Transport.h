#pragma once
#include "WinResources.h"
#include <atomic>
#include <condition_variable>
#include <deque>
#include <functional>
#include <mutex>
#include <thread>

namespace tiger::overlay
{
    inline constexpr UINT StateMessage = WM_APP + 10;
    inline constexpr UINT MenuMessage = WM_APP + 11;
    inline constexpr UINT StaleMessage = WM_APP + 12;
    inline constexpr UINT ReplyMessage = WM_APP + 13;
    struct Endpoints
    {
        std::wstring pipe = L"\\\\.\\pipe\\BimeIPC";
        std::wstring ui = L"Local\\TigerClaw.UiState.v1";
        std::wstring coreHeartbeat = L"Local\\TigerClaw.Heartbeat.v1";
        std::wstring overlayHeartbeat = L"Local\\TigerClaw.OverlayHeartbeat.v1";
        std::wstring menu = L"Local\\TigerClaw.ShowMenu.v1";
    };
    class StateSource
    {
        HWND window_;
        Endpoints endpoints_;
        Handle stop_{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
        std::mutex mutex_;
        State latest_;
        bool pending_ = false;
        // Start only after every field used by Run has been constructed.
        std::thread worker_;
        void Run();
    public:
        explicit StateSource(HWND window, Endpoints endpoints = {});
        ~StateSource();
        bool Take(State& state);
    };
    struct Reply
    {
        bool success = false;
        std::string json, error;
        int tag = 0;
    };
    class CommandQueue
    {
        HWND window_;
        Endpoints endpoints_;
        std::mutex mutex_;
        std::condition_variable signal_;
        struct Command { std::vector<std::string> requests; int tag; };
        std::deque<Command> pending_;
        std::deque<Reply> replies_;
        std::atomic<bool> stopping_{false};
        std::thread worker_;
        void Run();
    public:
        explicit CommandQueue(HWND window, Endpoints endpoints = {});
        ~CommandQueue();
        bool Send(std::string type, int tag = 0);
        void SetConfig(std::u16string_view key, std::u16string_view value);
        bool SetConfigs(const std::vector<std::pair<Text, Text>>& settings, int tag);
        bool Take(Reply& reply);
    };
}
