#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef UNICODE
#define UNICODE
#define _UNICODE
#endif
#include <windows.h>
#include <wrl/client.h>
#include <stdexcept>
#include <string>
#include <utility>
#include "Model.h"

namespace tiger::overlay
{
    using Microsoft::WRL::ComPtr;
    inline void CheckHr(HRESULT hr)
    {
        if (FAILED(hr)) throw std::runtime_error("Windows graphics operation failed: " + std::to_string(static_cast<unsigned>(hr)));
    }
    inline std::wstring Wide(std::u16string_view text)
    {
        static_assert(sizeof(wchar_t) == sizeof(char16_t));
        return std::wstring(text.begin(), text.end());
    }
    class Handle
    {
        HANDLE value_ = nullptr;
    public:
        explicit Handle(HANDLE value = nullptr) : value_(value) {}
        ~Handle() { Reset(); }
        Handle(const Handle&) = delete;
        Handle& operator=(const Handle&) = delete;
        Handle(Handle&& other) noexcept : value_(std::exchange(other.value_, nullptr)) {}
        Handle& operator=(Handle&& other) noexcept
        {
            if (this != &other) { Reset(); value_ = std::exchange(other.value_, nullptr); }
            return *this;
        }
        HANDLE Get() const { return value_; }
        explicit operator bool() const { return value_ && value_ != INVALID_HANDLE_VALUE; }
        void Reset(HANDLE value = nullptr)
        {
            if (*this) CloseHandle(value_);
            value_ = value;
        }
    };
    class Mapping
    {
        Handle handle_;
        void* view_ = nullptr;
    public:
        ~Mapping() { Close(); }
        Mapping() = default;
        Mapping(const Mapping&) = delete;
        Mapping& operator=(const Mapping&) = delete;
        void Close()
        {
            if (view_) UnmapViewOfFile(view_);
            view_ = nullptr; handle_.Reset();
        }
        bool Open(const wchar_t* name, std::size_t size, bool create = false)
        {
            if (view_) return true;
            handle_.Reset(create ? CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0,
                static_cast<DWORD>(size), name) : OpenFileMappingW(FILE_MAP_READ, FALSE, name));
            if (!handle_) return false;
            view_ = MapViewOfFile(handle_.Get(), create ? FILE_MAP_WRITE : FILE_MAP_READ, 0, 0, size);
            if (!view_) { Close(); return false; }
            return true;
        }
        void* Data() const { return view_; }
    };
}
