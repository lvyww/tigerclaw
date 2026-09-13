#include "ReadOnlyMapping.h"
#include <limits>
#include <stdexcept>
#ifdef _WIN32
#include <windows.h>
#else
#include <sys/mman.h>
#include <sys/stat.h>
#include <fcntl.h>
#include <unistd.h>
#endif

namespace tiger::core
{
    struct ReadOnlyMapping::State
    {
        const std::uint8_t* data = nullptr;
        std::size_t length = 0;
#ifdef _WIN32
        HANDLE file = INVALID_HANDLE_VALUE, mapping = nullptr;
        ~State()
        {
            if (data) UnmapViewOfFile(data);
            if (mapping) CloseHandle(mapping);
            if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
        }
#else
        int file = -1;
        ~State()
        {
            if (data) munmap(const_cast<std::uint8_t*>(data), length);
            if (file >= 0) close(file);
        }
#endif
    };
    ReadOnlyMapping::ReadOnlyMapping(const std::filesystem::path& path, std::uint64_t maximumLength)
        : _state(std::make_unique<State>())
    {
        std::uint64_t length;
#ifdef _WIN32
        _state->file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        LARGE_INTEGER size{};
        if (_state->file == INVALID_HANDLE_VALUE || !GetFileSizeEx(_state->file, &size) || size.QuadPart <= 0)
            throw std::runtime_error("Cannot open nonempty read-only model");
        length = static_cast<std::uint64_t>(size.QuadPart);
#else
        _state->file = open(path.c_str(), O_RDONLY | O_CLOEXEC);
        struct stat size{};
        if (_state->file < 0 || fstat(_state->file, &size) != 0 || !S_ISREG(size.st_mode) || size.st_size <= 0)
            throw std::runtime_error("Cannot open nonempty read-only model");
        length = static_cast<std::uint64_t>(size.st_size);
#endif
        if (length > maximumLength || length > std::numeric_limits<std::size_t>::max())
            throw std::runtime_error("Read-only model exceeds size limit");
        _state->length = static_cast<std::size_t>(length);
#ifdef _WIN32
        _state->mapping = CreateFileMappingW(_state->file, nullptr, PAGE_READONLY, 0, 0, nullptr);
        if (!_state->mapping) throw std::runtime_error("Cannot create read-only model mapping");
        _state->data = static_cast<const std::uint8_t*>(MapViewOfFile(_state->mapping, FILE_MAP_READ, 0, 0, _state->length));
        if (!_state->data) throw std::runtime_error("Cannot map read-only model view");
#else
        auto data = mmap(nullptr, _state->length, PROT_READ, MAP_PRIVATE, _state->file, 0);
        if (data == MAP_FAILED) throw std::runtime_error("Cannot map read-only model view");
        _state->data = static_cast<const std::uint8_t*>(data);
#endif
    }
    ReadOnlyMapping::~ReadOnlyMapping() = default;
    std::span<const std::uint8_t> ReadOnlyMapping::Bytes() const { return {_state->data, _state->length}; }
}
