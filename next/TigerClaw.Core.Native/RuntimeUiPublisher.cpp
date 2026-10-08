#include "RuntimeUiPublisher.h"
#include <windows.h>
#include <mutex>
#include <cstdint>
#include <cstring>
#include <stdexcept>
#include <thread>
#include <condition_variable>
namespace tiger::core
{
    namespace
    {
        struct Handle
        {
            HANDLE value=nullptr;
            ~Handle() { if (value) CloseHandle(value); }
        };
        struct Map
        {
            Handle handle; void* view=nullptr;
            explicit Map(const std::wstring& name, DWORD capacity=128*1024)
            {
                handle.value=CreateFileMappingW(INVALID_HANDLE_VALUE,nullptr,PAGE_READWRITE,0,capacity,name.c_str());
                if (!handle.value) throw std::runtime_error("Cannot create isolated UI mapping");
                view=MapViewOfFile(handle.value,FILE_MAP_ALL_ACCESS,0,0,capacity);
                if (!view) throw std::runtime_error("Cannot map isolated UI view");
            }
            ~Map() { if (view) UnmapViewOfFile(view); }
            void Write(std::uint64_t sequence,std::uint64_t tick,std::string_view payload,bool snapshot)
            {
                auto bytes=static_cast<char*>(view); std::uint64_t initial=snapshot ? 0 : sequence;
                std::int32_t size=static_cast<std::int32_t>(payload.size());
                std::memcpy(bytes,&initial,8); std::memcpy(bytes+8,&tick,8);
                std::memcpy(bytes+16,&size,4); std::memcpy(bytes+20,payload.data(),payload.size());
                if (snapshot) { MemoryBarrier(); std::memcpy(bytes,&sequence,8); }
            }
        };
        std::wstring Validate(std::wstring name)
        {
            if ((name!=L"Local\\TigerClaw.UiState.v1" && name!=L"Local\\TigerClaw.Heartbeat.v1" &&
                !name.starts_with(L"Local\\TigerClaw.Core.Native.Test.")) || name.size()>200 || name.find(L'\0')!=name.npos)
                throw std::invalid_argument("Explicit isolated UI map required");
            return name;
        }
    }
    struct RuntimeUiPublisher::Impl
    {
        std::wstring name;
        Map legacy, snapshot;
        Handle gate,changed;
        std::mutex writer;
        std::uint64_t sequence=0, tick=0;
        std::string lastPayload;
        bool snapshotPending=false;
        explicit Impl(std::wstring n):name(Validate(std::move(n))),legacy(name),snapshot(name+L".Snapshot.v2")
        {
            gate.value=CreateMutexW(nullptr,FALSE,(name+L".Snapshot.v2.Lock").c_str());
            changed.value=CreateEventW(nullptr,FALSE,FALSE,(name+L".Snapshot.v2.Changed").c_str());
            if (!gate.value || !changed.value) throw std::runtime_error("Cannot create UI synchronization objects");
        }
        bool Publish(std::string_view json)
        {
            if (json.empty() || json.size()>128*1024-20) return false;
            std::lock_guard lock(writer);
            const bool different = json != lastPayload;
            if (!different && !snapshotPending) return true;
            if (different)
            {
                // Allocate before publication; retain the latest payload even
                // when a v2 reader temporarily owns its mutex.
                lastPayload.assign(json);
                ++sequence;
                tick=GetTickCount64();
                legacy.Write(sequence,tick,lastPayload,false);
                snapshotPending=true;
            }
            auto acquired=WaitForSingleObject(gate.value,0);
            if (acquired==WAIT_OBJECT_0 || acquired==WAIT_ABANDONED)
            {
                snapshot.Write(sequence,tick,lastPayload,true); ReleaseMutex(gate.value);
                snapshotPending=false;
                SetEvent(changed.value);
            }
            return true;
        }
    };
    RuntimeUiPublisher::RuntimeUiPublisher(std::wstring name):_impl(std::make_unique<Impl>(std::move(name))) {}
    RuntimeUiPublisher::~RuntimeUiPublisher()=default;
    bool RuntimeUiPublisher::Publish(std::string_view json) { return _impl->Publish(json); }
    struct RuntimeHeartbeat::Impl
    {
        Map map;
        std::jthread worker;
        explicit Impl(std::wstring name):map(Validate(std::move(name)),16)
        {
            auto tick=[](void* view,std::uint64_t sequence)
            {
                std::uint64_t time=GetTickCount64();
                std::memcpy(view,&sequence,8); std::memcpy(static_cast<char*>(view)+8,&time,8);
            };
            tick(map.view,1);
            worker=std::jthread([this,tick](std::stop_token stop)
            {
                std::mutex mutex; std::unique_lock lock(mutex); std::condition_variable_any wake;
                std::uint64_t sequence=1;
                while(!stop.stop_requested())
                {
                    wake.wait_for(lock,stop,std::chrono::seconds(5),[] { return false; });
                    if(!stop.stop_requested()) tick(map.view,++sequence);
                }
            });
        }
    };
    RuntimeHeartbeat::RuntimeHeartbeat(std::wstring name):_impl(std::make_unique<Impl>(std::move(name))) {}
    RuntimeHeartbeat::~RuntimeHeartbeat()=default;
}
