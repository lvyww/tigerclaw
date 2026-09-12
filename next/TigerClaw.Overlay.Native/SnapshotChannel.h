#pragma once
#include "WinResources.h"
#include <cstring>

namespace tiger::overlay
{
    enum class SnapshotResult { Legacy, Busy, Ready };

    // Mutex synchronization, not two timed observations of unprotected bytes.
    // Names derive from the injected UI endpoint, including isolated tests.
    class SnapshotChannel
    {
        std::wstring name_;
        Mapping map_;
        Handle mutex_, changed_;
    public:
        explicit SnapshotChannel(const std::wstring& legacyName) : name_(legacyName + L".Snapshot.v2"),
            mutex_(CreateMutexW(nullptr, FALSE, (name_ + L".Lock").c_str())),
            changed_(CreateEventW(nullptr, FALSE, FALSE, (name_ + L".Changed").c_str())) {}

        HANDLE Changed() const { return changed_.Get(); }

        SnapshotResult Read(std::int64_t legacySequence, std::int64_t legacyTick, std::string& payload)
        {
            if (!mutex_ || !map_.Open(name_.c_str(), 128 * 1024)) return SnapshotResult::Legacy;
            DWORD acquired = WaitForSingleObject(mutex_.Get(), 0);
            if (acquired == WAIT_TIMEOUT) return SnapshotResult::Busy;
            if (acquired != WAIT_OBJECT_0 && acquired != WAIT_ABANDONED) return SnapshotResult::Legacy;
            struct Release
            {
                HANDLE mutex;
                ~Release() { ReleaseMutex(mutex); }
            } release{mutex_.Get()};
            std::int64_t sequence = 0, tick = 0;
            int length = 0;
            std::memcpy(&sequence, map_.Data(), 8);
            std::memcpy(&tick, static_cast<char*>(map_.Data()) + 8, 8);
            std::memcpy(&length, static_cast<char*>(map_.Data()) + 16, 4);
            // A restarted old Core only updates v1. Never keep serving the
            // stale v2 map retained by this reader after a publisher downgrade.
            if (!sequence || sequence != legacySequence || tick != legacyTick || length <= 0 || length > 128 * 1024 - 20)
                return SnapshotResult::Legacy;
            payload.assign(static_cast<char*>(map_.Data()) + 20, static_cast<std::size_t>(length));
            return SnapshotResult::Ready;
        }
    };
}
