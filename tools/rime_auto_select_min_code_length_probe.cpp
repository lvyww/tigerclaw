// Real headless librime candidate-path checks; never installs a frontend.
#include <rime_api.h>
#include <dlfcn.h>
#include <iostream>
#include <stdexcept>
#include <string>
static void check(bool ok, const std::string& message) { if (!ok) throw std::runtime_error(message); }
int main(int argc, char** argv) {
    RimeApi* api = nullptr;
    RimeSessionId id = 0;
    try {
        check(argc == 5, "usage: probe user shared plugin effective-min");
        const int minimum = std::stoi(argv[4]);
        check(dlopen(argv[3], RTLD_NOW | RTLD_GLOBAL) != nullptr, "Lua plugin failed");
        api = rime_get_api();
        const char* modules[] = {"default", "lua", nullptr};
        RIME_STRUCT(RimeTraits, traits);
        traits.user_data_dir=argv[1]; traits.shared_data_dir=argv[2]; traits.log_dir=argv[1];
        traits.app_name="rime.tiger.auto-select-min"; traits.modules=modules;
        api->setup(&traits); api->initialize(&traits);
        if (api->start_maintenance(True)) api->join_maintenance_thread();
        id=api->create_session(); check(id && api->select_schema(id,"tiger_sentence"),"schema failed");
        api->set_option(id,"ascii_mode",False);
        api->set_option(id,"tiger_sentence_early_commit",False);
        auto type = [&](const std::string& raw) { for (unsigned char c : raw) check(api->process_key(id,c,0),"unhandled key"); };
        auto has = [&](const std::string& text) {
            RIME_STRUCT(RimeContext, context);
            check(api->get_context(id,&context),"context missing");
            bool found=false;
            for (int i=0;i<context.menu.num_candidates;++i) if (text==context.menu.candidates[i].text) found=true;
            api->free_context(&context); return found;
        };
        int checks=0;
        auto expect = [&](const std::string& raw,const std::string& text,bool expected) {
            api->clear_composition(id); type(raw);
            check(has(text)==expected,"candidate mismatch "+raw+" / "+text+" min="+std::to_string(minimum));
            ++checks;
        };
        expect("ab","乙",true);
        expect("abxy","甲戊",true);
        expect("abxy","乙戊",minimum>0 && minimum<=2);
        expect("abcxy","丁戊",minimum>0 && minimum<=3);
        expect("abcdxy","庚戊",minimum>0 && minimum<=4);
        expect("xyabc","戊丁",minimum>0 && minimum<=3);
        expect("ab2xy","乙戊",true);
        expect("ab;xy","乙戊",true);
        expect("abc'xy","甲乙戊",true);
        expect("abcxy","甲乙戊",false);
        // Removed frontend switches have no effect on the numeric setting.
        for (bool old : {false,true}) {
            api->set_option(id,"tiger_sentence_allow_duplicate_single",old?True:False);
            expect("abxy","乙戊",minimum>0 && minimum<=2);
        }
        api->clear_composition(id); type("ab");
        check(api->process_key(id,0xff09,0),"Tab was not handled"); type("xy");
        check(has("乙戊"),"manual Tab lock lost short selected prefix"); ++checks;
        api->destroy_session(id); id=0; api->finalize();
        std::cout << "{\"status\":\"passed\",\"auto_select_min_code_length\":" << minimum
                  << ",\"checks\":" << checks << ",\"real_librime\":true,\"physical_input\":false}\n";
        return 0;
    } catch (const std::exception& e) {
        std::cerr << e.what() << '\n';
        if (id) api->destroy_session(id); if (api) api->finalize(); return 1;
    }
}
