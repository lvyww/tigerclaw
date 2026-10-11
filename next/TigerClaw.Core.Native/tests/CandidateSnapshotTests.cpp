#include "RuntimeProtocol.h"
#include "LearningText.h"
#include <nlohmann/json.hpp>
#include <iostream>
#include <source_location>
#include <thread>
#include <fstream>
using namespace tiger::core;
using Json=nlohmann::json;
static void Check(bool ok, std::source_location loc=std::source_location::current())
{ if (!ok) throw std::runtime_error("Candidate snapshot line "+std::to_string(loc.line())); }
int main()
{
    auto root=std::filesystem::temp_directory_path()/("tiger-candidates-"+learningId());
    try
    {
        std::filesystem::create_directories(root/"tables/Plain");
        auto config=root/"config.txt";
        WriteUtf8TextFile(config,u"码表存储位置\ttables\n当前码表\tPlain\n每页候选个数\t5\n");
        std::u16string entries=u"aa alpha\naa beta\naa gamma\naa delta\naa epsilon\naa zeta\naa \U00020000e\u0301\n;a symbol\n;a other\nj suffix\njg final\nvuj first\ntuja auto\n";
        entries+=u"rn {one|two|three|four}\nrn {five|six}\n";
        WriteUtf8TextFile(root/"tables/Plain/table.txt",entries);
        auto pinyin=root/std::filesystem::path(u"拼音反查码表");
        std::filesystem::create_directories(pinyin);
        WriteUtf8TextFile(pinyin/"test.txt",u"a reverse\na second\n");
        RuntimeLexicons runtime(root); runtime.Reload(config);
        RuntimeInput input(runtime,MakeUpperCaseServices([](auto){}));
        RuntimeProtocol protocol(runtime,input,config);
        auto send=[&](Json request) { auto response=protocol.Handle(request.dump(),{}); Check(response.has_value()); return Json::parse(*response); };
        auto query=[&] { return send({{"type","query_state"},{"candidate_ui_version",1}}).at("candidate_ui"); };
        auto key=[&](int vk) { return send({{"type","key"},{"action","down"},{"vk",vk},{"candidate_ui_version",1}}); };
        Check(!send({{"type","query_state"}}).contains("candidate_ui"));
        Check(query()["menu"]["strings"].empty());
        key('A'); auto result=key('A'); auto first=result["candidate_ui"];
        Check(first["menu"]["strings"].size()==7);
        auto state=protocol.CaptureSnapshot();
        auto overlay=Json::parse(protocol.CaptureUiState());
        for (size_t i=0;i<state.page.entries.size();++i)
            Check(first["menu"]["strings"][i]==overlay["Candidates"][i]);
        auto same=send({{"type","query_state"},{"candidate_ui_version",1},
            {"candidate_instance",first["instance"]},{"candidate_version",first["version"]}})["candidate_ui"];
        Check(!same.contains("menu") && same["version"]==first["version"]);
        Check(protocol.CaptureSnapshot().page.pageIndex==state.page.pageIndex && input.Raw()==u"aa");
        for (bool extended:{false,true})
        {
            std::vector<double> times;
            Json request={{"type","query_state"}};
            if (extended) { request["candidate_ui_version"]=1; request["candidate_instance"]=first["instance"]; request["candidate_version"]=first["version"]; }
            auto bytes=request.dump(); std::size_t size=0;
            for(int i=0;i<1000;++i)
            {
                auto start=std::chrono::steady_clock::now(); auto reply=protocol.Handle(bytes,{});
                times.push_back(std::chrono::duration<double,std::micro>(std::chrono::steady_clock::now()-start).count()); size=reply->size();
            }
            std::sort(times.begin(),times.end());
            std::cout<<(extended ? "snapshot unchanged" : "legacy query")<<" bytes="<<size<<" P50_us="<<times[500]<<" P95_us="<<times[950]<<'\n';
        }
        auto page=key(0xbb)["candidate_ui"]; // Default next-page key.
        Check(page["menu"]["page"]==1 && page["menu"]["selection"]==5);
        Check(page["menu"]["pages"]==Json::array({0,5}));
        Check(page["menu"]["strings"][6]==learningUtf8(u"\U00020000e\u0301"));
        Check(key('2')["commit_text"]==learningUtf8(u"\U00020000e\u0301"));
        Check(query()["menu"]["strings"].empty());
        key(0xba); key('A');
        auto symbol=query(); Check(symbol["menu"]["strings"]==Json::array({"symbol","other"}));
        Check(key(32)["commit_text"]=="symbol");
        key(0xc0); key('A');
        Check(query()["menu"]["strings"]==Json::array({"reverse","second"}));
        Check(key(32)["commit_text"]=="reverse");
        key('R'); key('N');
        auto randomMenu=query();
        for(int i=0;i<100;++i) Check(query()==randomMenu);
        Check(key(32)["commit_text"]==randomMenu["menu"]["strings"][0]);
        bool changed=false; std::u16string error;
        Check(input.SetConfig(u"最大码长",u"3",changed,error));
        Check(input.SetConfig(u"最大码长无重自动上屏",u"否",changed,error));
        key('V'); key('U'); key('J');
        auto transition=key('J'); Check(transition["commit_text"]=="first");
        Check(transition["candidate_ui"]["menu"]["strings"]==Json::array({"suffix"}));
        Check(key('G')["candidate_ui"]["menu"]["strings"]==Json::array({"final"}));
        key(27);
        Check(input.SetConfig(u"最大码长",u"4",changed,error));
        Check(input.SetConfig(u"最大码长无重自动上屏",u"是",changed,error));
        key('T'); key('U'); key('J');
        auto autoCommit=key('A'); Check(autoCommit["commit_text"]=="auto");
        Check(autoCommit["candidate_ui"]["menu"]["composing"]==false);
        key('A'); key(8); Check(query()["menu"]["strings"].empty());
        auto replayRequest=Json{{"type","key"},{"action","down"},{"vk",'A'},
            {"client_session","candidate-test"},{"event_id","1"},{"candidate_ui_version",1},{"candidate_context","old"}};
        auto original=send(replayRequest);
        protocol.Handle(R"({"type":"focus","hwnd":123,"processId":456})",{});
        Check(query()["menu"]["composing"]==false);
        replayRequest["candidate_context"]="new";
        Check(send(replayRequest)["candidate_ui"]["context"]=="old"); // TSF rejects this stale context.
        // Export bounds must not turn a key into a failed/unhandled response.
        input.Cancel();
        entries=u"aa "+std::u16string(910000,u'x')+u"\n";
        WriteUtf8TextFile(root/"tables/Plain/table.txt",entries);
        input.ReloadTables(); key('A'); auto oversized=key('A');
        Check(oversized["success"]==true && oversized["candidate_ui"]["menu"]["available"]==false);
        Check(oversized.dump().size()<1024*1024 && input.Raw()==u"aa");
        input.Cancel();
        entries=u"aaaa "+std::u16string(600000,u'x')+u"\nb "+std::u16string(600000,u'y')+u"\n";
        WriteUtf8TextFile(root/"tables/Plain/table.txt",entries); input.ReloadTables();
        input.ImportRaw(u"aaaa"); auto largeCommit=key('B');
        Check(largeCommit["success"]==true && largeCommit["commit_text"].get<std::string>().size()==600000);
        Check(largeCommit["candidate_ui"]["menu"]["available"]==false && largeCommit.dump().size()<1024*1024);
        // Real async sentence owner, synthetic mapped model, no neural process.
        std::filesystem::create_directories(root/"tables/testSentence");
        WriteUtf8TextFile(root/"tables/testSentence/table.txt",u"aa a\naa c\nbb b\n");
        // Sentence schemas are identified by the production schema-name rule.
        std::filesystem::rename(root/"tables/testSentence",root/std::filesystem::path(u"tables/test整句"));
        std::string model="TCSKNM01";
        auto number=[&](std::uint64_t n,int bytes) { for(int i=0;i<bytes;++i) model+=static_cast<char>(n>>(8*i)); };
        number(1,4); number(1,4); number(0,4); number(0x3f000000,4);
        number(0,8); number(0,4); number(0,8); number(0,8);
        { std::ofstream out(root/"model.bin",std::ios::binary); out<<model; }
        WriteUtf8TextFile(config,u"码表存储位置\ttables\n当前码表\ttest整句\n");
        RuntimeLexicons sentenceRuntime(root); sentenceRuntime.Reload(config);
        RuntimeInput sentenceInput(sentenceRuntime,MakeUpperCaseServices([](auto){}),root/"model.bin");
        RuntimeProtocol sentenceProtocol(sentenceRuntime,sentenceInput,config);
        sentenceInput.ImportRaw(u"aabb");
        Json menu;
        for(int i=0;i<1000;++i)
        {
            menu=Json::parse(*sentenceProtocol.Handle(R"({"type":"query_state","candidate_ui_version":1})",{}))["candidate_ui"]["menu"];
            if (!menu["pending"].get<bool>()) break;
            Check(menu["strings"].empty()); std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
        Check(!menu["pending"].get<bool>() && !menu["strings"].empty());
        Check(menu["pages"]==Json::array({0}));
        Check(menu["strings"].size()==sentenceProtocol.CaptureSnapshot().page.entries.size());
        sentenceInput.ImportRaw(u"aa");
        auto selected=Json::parse(*sentenceProtocol.Handle(R"({"type":"key","action":"down","vk":40,"candidate_ui_version":1})",{}));
        Check(selected["candidate_ui"]["menu"]["selection"]==1);
        Check(Json::parse(sentenceProtocol.CaptureUiState())["SelectedCandidateIndex"]==1);
        sentenceInput.Cancel(); Check(sentenceProtocol.CaptureSnapshot().raw.empty());
        std::cout<<"Candidate snapshot compatibility, paging, transitions, bounds and async checks passed\n";
    }
    catch(const std::exception& error) { std::cerr<<error.what()<<'\n'; return 1; }
    std::error_code ignored; std::filesystem::remove_all(root,ignored);
}
