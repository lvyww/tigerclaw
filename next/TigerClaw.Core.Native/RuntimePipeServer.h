#pragma once
#include "RuntimeProtocol.h"
#include <memory>

namespace tiger::core
{
    // Windows transport accepts the production endpoint or a validated isolated name.
    class RuntimePipeServer
    {
        struct Impl;
        std::unique_ptr<Impl> _impl;
    public:
        RuntimePipeServer(RuntimeProtocol& protocol, std::wstring name, std::function<void()> publish = {});
        ~RuntimePipeServer();
        RuntimePipeServer(const RuntimePipeServer&) = delete;
        RuntimePipeServer& operator=(const RuntimePipeServer&) = delete;
    };
}
