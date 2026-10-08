#pragma once
#include <string>
#include <string_view>
#include <memory>
namespace tiger::core
{
    class RuntimeHeartbeat
    {
        struct Impl;
        std::unique_ptr<Impl> _impl;
    public:
        explicit RuntimeHeartbeat(std::wstring mapName);
        ~RuntimeHeartbeat();
    };
    // Same v1/v2 layout as TigerClaw.Shared, with an explicit endpoint name.
    class RuntimeUiPublisher
    {
        struct Impl;
        std::unique_ptr<Impl> _impl;
    public:
        explicit RuntimeUiPublisher(std::wstring mapName);
        ~RuntimeUiPublisher();
        bool Publish(std::string_view json);
    };
}
