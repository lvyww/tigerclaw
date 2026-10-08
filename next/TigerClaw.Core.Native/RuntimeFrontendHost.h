#pragma once
#include <windows.h>
#include <filesystem>
#include <mutex>
#include <string>
#include <vector>
#include "RuntimeEndpoints.h"

namespace tiger::core
{
    // Explicit-root children; production adoption requires exact path and session.
    class RuntimeFrontendHost
    {
        std::filesystem::path _root;
        std::wstring _pipe;
        RuntimeEndpoints _endpoints;
        HANDLE _job=nullptr, _menu=nullptr, _overlay=nullptr, _dialog=nullptr, _hook=nullptr;
        bool _addCi=false;
        ULONGLONG _lastCheck=0, _nextAttempt=0;
        DWORD _retryDelay=10000;
        std::mutex _mutex;
        bool Start(const wchar_t* name, const wchar_t* arguments, HANDLE& owned);
        static void CloseChild(HANDLE& child);
    public:
        RuntimeFrontendHost(std::filesystem::path root, std::wstring pipe);
        ~RuntimeFrontendHost();
        RuntimeFrontendHost(const RuntimeFrontendHost&)=delete;
        bool Overlay(bool menu=false);
        void Tick();
        bool Dialog(bool addCi);
        bool Hook();
    };
}
