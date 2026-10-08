#include "RuntimeUiPublisher.h"
#include <windows.h>
#include <thread>
#include <cstdint>
#include <stdexcept>
#include <iostream>
using namespace tiger::core;
static void Check(bool value) { if(!value) throw std::runtime_error("UI publisher dedup/retry regression"); }
int main()
{
    try
    {
        auto name=L"Local\\TigerClaw.Core.Native.Test.Publisher."+std::to_wstring(GetCurrentProcessId());
        RuntimeUiPublisher publisher(name);
        auto legacy=OpenFileMappingW(FILE_MAP_READ,FALSE,name.c_str());
        auto snapshot=OpenFileMappingW(FILE_MAP_READ,FALSE,(name+L".Snapshot.v2").c_str());
        auto v1=static_cast<const std::uint64_t*>(MapViewOfFile(legacy,FILE_MAP_READ,0,0,0));
        auto v2=static_cast<const std::uint64_t*>(MapViewOfFile(snapshot,FILE_MAP_READ,0,0,0));
        auto gate=OpenMutexW(SYNCHRONIZE|MUTEX_MODIFY_STATE,FALSE,(name+L".Snapshot.v2.Lock").c_str());
        auto changed=OpenEventW(SYNCHRONIZE,FALSE,(name+L".Snapshot.v2.Changed").c_str());
        Check(v1 && v2 && gate && changed);
        Check(publisher.Publish("{\"x\":1}"));Check(v1[0]==1 && v2[0]==1);
        Check(WaitForSingleObject(changed,0)==WAIT_OBJECT_0);
        for(int i=0;i<100;++i) Check(publisher.Publish("{\"x\":1}"));
        Check(v1[0]==1 && v2[0]==1 && WaitForSingleObject(changed,0)==WAIT_TIMEOUT);
        auto held=CreateEventW(nullptr,TRUE,FALSE,nullptr), release=CreateEventW(nullptr,TRUE,FALSE,nullptr);
        std::thread reader([&]{WaitForSingleObject(gate,INFINITE);SetEvent(held);WaitForSingleObject(release,INFINITE);ReleaseMutex(gate);});
        WaitForSingleObject(held,INFINITE);
        bool pending=publisher.Publish("{\"x\":2}") && publisher.Publish("{\"x\":3}") && v1[0]==3 && v2[0]==1;
        SetEvent(release);reader.join();Check(pending);
        Sleep(10);Check(publisher.Publish("{\"x\":3}"));
        Check(v1[0]==3 && v2[0]==3 && v1[1]==v2[1]); // late v2 keeps exact v1 identity
        Check(WaitForSingleObject(changed,0)==WAIT_OBJECT_0);
        Check(publisher.Publish("{\"x\":3}") && WaitForSingleObject(changed,0)==WAIT_TIMEOUT);
        Check(!publisher.Publish("") && v1[0]==3);
        UnmapViewOfFile(v1);UnmapViewOfFile(v2);
        for(auto handle:{legacy,snapshot,gate,changed,held,release}) CloseHandle(handle);
        std::cout<<"Unchanged UI suppressed; contended v2 retries latest payload with matching identity\n";
        return 0;
    }
    catch(const std::exception& error) {std::cerr<<error.what()<<'\n';return 1;}
}
