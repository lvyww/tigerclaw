#pragma once
#include <filesystem>
#include <span>
#include <cstdint>
#include <memory>

namespace tiger::core
{
    class ReadOnlyMapping
    {
    public:
        explicit ReadOnlyMapping(const std::filesystem::path& path, std::uint64_t maximumLength);
        ~ReadOnlyMapping();
        ReadOnlyMapping(const ReadOnlyMapping&) = delete;
        ReadOnlyMapping& operator=(const ReadOnlyMapping&) = delete;
        std::span<const std::uint8_t> Bytes() const;
    private:
        struct State;
        std::unique_ptr<State> _state;
    };
}
