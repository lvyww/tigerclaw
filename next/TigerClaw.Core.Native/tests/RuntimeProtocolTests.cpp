#include "RuntimeProtocol.h"
#include "RuntimePipeServer.h"
#include "RuntimeUiPublisher.h"
#include <cstring>
#include <semaphore>
#include <windows.h>
#include <aclapi.h>
#include <sddl.h>
#include "OutputServices.h"
#include "LearningText.h"
#include <nlohmann/json.hpp>
#include <iostream>
#include <thread>
#include <source_location>
using namespace tiger::core;
using Json = nlohmann::json;
namespace
{
    void Check(bool ok, std::source_location at = std::source_location::current())
    { if (!ok) throw std::runtime_error("Protocol regression line " + std::to_string(at.line())); }
}
int main()
{
    auto root = std::filesystem::temp_directory_path() / ("tiger-protocol-" + learningId());
    try
    {
        std::filesystem::create_directories(root / "tables/Plain");
        std::filesystem::create_directories(root / std::filesystem::path(u"tables/test整句"));
        WriteUtf8TextFile(root / "tables/Plain/table.txt", u"aa alpha\naa beta\n");
        WriteUtf8TextFile(root / std::filesystem::path(u"tables/test整句/table.txt"), u"aa a\n");
        auto config = root / "config.txt";
        WriteUtf8TextFile(config, u"码表存储位置\ttables\n当前码表\tPlain\n");
        {
            RuntimeLexicons runtime(root); runtime.Reload(config);
            RuntimeInput input(runtime, MakeUpperCaseServices([](auto) {}));
            RuntimeProtocol protocol(runtime, input, config);
            auto send = [&](Json msg) { auto result = protocol.Handle(msg.dump(), CtrlSpaceState::Time{}); Check(result.has_value()); return Json::parse(*result); };
            auto key = [&](int vk, const char* id, int seq = 1) { return send({{"type","key"},{"seq",seq},{"vk",vk},{"action","down"},{"client_session","test"},{"event_id",id}}); };
            auto hello=send({{"type","hello"}});
            Check(hello["core_build"]=="next-dev" && !hello.contains("production_ready") && !hello.contains("transport"));
            Check(key(0x41,"a1")["input_buffer"] == "a");
            Check(key(0x41,"a1",9)["seq"] == 9);
            Check(protocol.CaptureSnapshot().raw == u"a");
            key(0x41,"a2");
            auto committed = key(0x20,"space"); Check(committed["commit_text"] == "alpha");
            Check(key(0x20,"space",18)["commit_text"] == "alpha");
            Check(protocol.CaptureSnapshot().raw.empty());
            std::vector<std::thread> requests;
            for (int i = 0; i < 16; ++i) requests.emplace_back([&,i] { key(0x41,"concurrent",i); });
            for (auto& thread : requests) thread.join();
            Check(protocol.CaptureSnapshot().raw == u"a");
            Check(!protocol.Handle(R"({"type":"focus"})",{}));
            Check(!protocol.Handle(R"({"type":"composition_canceled"})",{}));
            Check(protocol.CaptureSnapshot().raw.empty());
            auto state = send({{"type","query_state"}}); Check(!state["handled"].get<bool>() && state["keyboard_open"] == true);
            Check(send({{"type","get_schema_list"}})["current_schema"] == "Plain");
            Check(send({{"type","get_config"}}).contains("config_text"));
            Check(!send({{"type","not_implemented"}})["success"].get<bool>());
            Check(!Json::parse(*protocol.Handle("{",{}))["success"].get<bool>());
            Check(send({{"type","key"},{"vk","invalid"},{"action","down"}})["success"].get<bool>());
            Check(protocol.CaptureSnapshot().raw.empty());
            key(0x41,"before-reload");
            auto old = runtime.Read();
            WriteUtf8TextFile(config, u"码表存储位置\ttables\n当前码表\ttest整句\n");
            // Table-only reload must not activate unaccepted config edits or
            // cancel ordinary composition, but must reload selection bindings.
            SelectionBindings custom = DefaultSelectionBindings(); custom[2] = {0x70};
            WriteSelectionBindingsFile(root / std::filesystem::path(u"自定义选重键.txt"), custom);
            Check(send({{"type","reload_mb"}})["success"] == true);
            Check(runtime.Read()->schemaName == u"Plain" && protocol.CaptureSnapshot().raw == u"a");
            Check(runtime.Read()->selectionKeys.Number(0x70) == 2);
            old = runtime.Read();
            auto failed = send({{"type","reload_config"}});
            Check(!failed["success"].get<bool>() && failed["handled"]==true && runtime.Read() == old && protocol.CaptureSnapshot().raw.empty());
            WriteUtf8TextFile(root / "tables/Plain/table.txt", u"aa replacement\n");
            WriteUtf8TextFile(config, u"码表存储位置\ttables\n当前码表\tPlain\n");
            WriteSelectionBindingsFile(root / std::filesystem::path(u"自定义选重键.txt"), DefaultSelectionBindings());
            auto reloaded = send({{"type","reload_config"}});
            Check(reloaded["success"] == true && !reloaded.contains("cancel_composition"));
            Check(runtime.Read() != old && protocol.CaptureSnapshot().raw.empty());
            Check(!runtime.Read()->selectionKeys.Number(0x70));
            Check(key(0x41,"new-a1")["cancel_composition"] == true);
            Check(key(0x41,"new-a1",99)["cancel_composition"] == true);
            Check(!key(0x41,"new-a2").contains("cancel_composition"));
            Check(key(0x20,"new-space")["commit_text"] == "replacement");
            Check(key(0x10,"shift")["expect_keyup"] == true);
            // Actual Windows pipe transport: UTF-8 fragmented across messages,
            // multiple newline frames, notification without reply, reconnect,
            // and destruction while clients have outstanding reads.
            auto pipeName = L"TigerClaw.Core.Native.Test." + std::to_wstring(GetCurrentProcessId());
            auto server = std::make_unique<RuntimePipeServer>(protocol,pipeName);
            auto connect = [&]
            {
                auto path = L"\\\\.\\pipe\\" + pipeName;
                auto deadline = GetTickCount64()+5000;
                for (;;)
                {
                    HANDLE h = CreateFileW(path.c_str(),GENERIC_READ|GENERIC_WRITE,0,nullptr,OPEN_EXISTING,0,nullptr);
                    if (h != INVALID_HANDLE_VALUE) return h;
                    Check(GetTickCount64() < deadline); Sleep(10);
                }
            };
            auto read = [&](HANDLE h)
            {
                std::string line;
                for (char c;;)
                {
                    DWORD got = 0; Check(ReadFile(h,&c,1,&got,nullptr) && got == 1);
                    if (c == '\n') return Json::parse(line);
                    line += c;
                }
            };
            auto write = [&](HANDLE h, const std::string& bytes, bool fragment = false)
            {
                for (std::size_t offset=0; offset<bytes.size();)
                {
                    DWORD count = 0, size = fragment ? 1 : static_cast<DWORD>(bytes.size()-offset);
                    Check(WriteFile(h,bytes.data()+offset,size,&count,nullptr) && count>0); offset += count;
                }
            };
            HANDLE pipe = connect();
            PSECURITY_DESCRIPTOR descriptor=nullptr; PACL acl=nullptr;
            Check(GetSecurityInfo(pipe,SE_KERNEL_OBJECT,DACL_SECURITY_INFORMATION,nullptr,nullptr,&acl,nullptr,&descriptor)==ERROR_SUCCESS);
            for (auto sidText : {L"S-1-5-18",L"S-1-5-11",L"S-1-5-32-545",L"S-1-15-2-1",L"S-1-15-2-2"})
            {
                PSID sid=nullptr; Check(ConvertStringSidToSidW(sidText,&sid)); bool found=false;
                for (DWORD i=0; i<acl->AceCount; ++i)
                {
                    void* entry=nullptr; Check(GetAce(acl,i,&entry)); auto ace=static_cast<ACCESS_ALLOWED_ACE*>(entry);
                    if (ace->Header.AceType==ACCESS_ALLOWED_ACE_TYPE && EqualSid(sid,&ace->SidStart))
                        found=(ace->Mask & 0x12019f)==0x12019f;
                }
                LocalFree(sid); Check(found);
            }
            LocalFree(descriptor);
            write(pipe, Json({{"type","add_ci"},{"code","bb"},{"text",learningUtf8(u"汉字")},{"seq",200}}).dump()+"\n",true);
            Check(read(pipe)["success"] == true);
            write(pipe,"{\"type\":\"focus\"}\n{\"type\":\"query_state\",\"seq\":201}\n{\"type\":\"query_state\",\"seq\":202}\n");
            Check(read(pipe)["seq"] == 201 && read(pipe)["seq"] == 202);
            CloseHandle(pipe);
            pipe = connect();
            write(pipe,"{\"type\":\"query_state\",\"seq\":203}\n");
            Check(read(pipe)["seq"] == 203);
            auto stopped = GetTickCount64(); server.reset();
            Check(GetTickCount64()-stopped < 2000); CloseHandle(pipe);
            auto mapName=L"Local\\"+pipeName+L".UiState.v1";
            RuntimeUiPublisher publisher(mapName);
            auto payload=protocol.CaptureUiState(); Check(publisher.Publish(payload));
            auto readMap=[&](const std::wstring& name)
            {
                HANDLE map=OpenFileMappingW(FILE_MAP_READ,FALSE,name.c_str()); Check(map!=nullptr);
                auto view=static_cast<const char*>(MapViewOfFile(map,FILE_MAP_READ,0,0,128*1024)); Check(view!=nullptr);
                std::uint64_t seq=0,tick=0; std::int32_t length=0;
                std::memcpy(&seq,view,8); std::memcpy(&tick,view+8,8); std::memcpy(&length,view+16,4);
                Check(seq>0 && tick>0 && length>0 && length<=128*1024-20);
                auto json=Json::parse(std::string(view+20,length));
                UnmapViewOfFile(view); CloseHandle(map);
                return std::pair{seq,json};
            };
            auto legacy=readMap(mapName), snapshot=readMap(mapName+L".Snapshot.v2");
            Check(legacy==snapshot && legacy.second==Json::parse(payload));
            Check(!publisher.Publish(std::string(128*1024,'x')));
            std::binary_semaphore locked(0),release(0);
            std::thread held([&]
            {
                HANDLE gate=OpenMutexW(SYNCHRONIZE|MUTEX_MODIFY_STATE,FALSE,(mapName+L".Snapshot.v2.Lock").c_str());
                Check(gate && WaitForSingleObject(gate,1000)==WAIT_OBJECT_0);
                locked.release(); release.acquire(); ReleaseMutex(gate); CloseHandle(gate);
            });
            locked.acquire();
            // A changed state must reach v1 while v2 is busy; retrying the same
            // state after unlock completes v2 without inventing a new revision.
            auto changedUi=Json::parse(payload); changedUi["CaretX"]=12345; payload=changedUi.dump();
            auto started=GetTickCount64(); Check(publisher.Publish(payload));
            Check(GetTickCount64()-started<1000);
            release.release(); held.join();
            Check(readMap(mapName).first==2 && readMap(mapName+L".Snapshot.v2").first==1);
            Check(publisher.Publish(payload));
            Check(readMap(mapName+L".Snapshot.v2").first==2);
            Check(runtime.AdjustCandidate(CandidateAdjustment::Add,u"cc",u"展示=>有\\s空格"));
            std::filesystem::path exported;
            RuntimeProtocol exports(runtime,input,config,{},[&](auto target,bool select)
            { Check(select); exported=std::filesystem::path(target); return std::filesystem::is_regular_file(exported); });
            Check(Json::parse(*exports.Handle("{\"type\":\"export_mb\"}",{}))["success"]==true);
            auto imported=LoadSchemaLexicon(exported.parent_path());
            auto cc=imported.table.Find(u"cc"); Check(cc.has_value());
            auto packed=imported.table.Candidate(*cc,0);
            Check(CandidateDisplayText(packed)==u"展示" && CandidateCommitText(packed)==u"有 空格");
            Check(runtime.AdjustCandidate(CandidateAdjustment::Add,u"dd",u"{添加}"));
            RuntimeInput macroInput(runtime,MakeUpperCaseServices([](auto){})); int launches=0;
            RuntimeProtocol macroProtocol(runtime,macroInput,config,[&](auto){ ++launches; throw std::runtime_error("Missing frontend"); });
            auto macroKey=[&](int vk,const char* id,int seq)
            {
                auto request=Json{{"type","key"},{"vk",vk},{"action","down"},{"client_session","macro"},{"event_id",id},{"seq",seq}};
                return Json::parse(*macroProtocol.Handle(request.dump(),{}));
            };
            macroKey(0x44,"1",1); macroKey(0x44,"2",2);
            Check(macroKey(32,"3",3)["success"]==true);
            Check(macroKey(32,"3",4)["seq"]==4 && launches==1 && !macroProtocol.CaptureSnapshot().composing);
        }
        std::filesystem::remove_all(root);
        std::cout << "Runtime protocol replay/concurrency/state/reload/failure tests passed\n";
        return 0;
    }
    catch (const std::exception& e) { std::cerr << e.what() << " evidence: " << root << '\n'; return 1; }
}
