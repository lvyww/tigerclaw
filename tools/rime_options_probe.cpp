// Isolated real librime option persistence across sessions and processes.
#include <rime_api.h>
#include <dlfcn.h>
#include <iostream>
#include <stdexcept>
#include <string>

static void check(bool ok, const char* message) {
    if (!ok) throw std::runtime_error(message);
}
int main(int argc, char** argv) {
    try {
        check(argc == 5, "usage: probe user shared plugin write|read|defaults|legacy");
        auto api = rime_get_api();
        check(dlopen(argv[3], RTLD_NOW | RTLD_GLOBAL), "Lua plugin load failed");
        const char* modules[] = {"default", "lua", nullptr};
        RIME_STRUCT(RimeTraits, traits);
        traits.user_data_dir=argv[1]; traits.shared_data_dir=argv[2]; traits.log_dir=argv[1];
        traits.app_name="rime.tiger.options"; traits.modules=modules;
        api->setup(&traits); api->initialize(&traits);
        if (api->start_maintenance(True)) api->join_maintenance_thread();
        auto create = [&]() {
            auto id=api->create_session();
            check(id && api->select_schema(id,"tiger_sentence"),"cannot create sentence session");
            return id;
        };
        auto expect = [&](RimeSessionId id, bool early, bool duplicate, bool buffer) {
            check(!!api->get_option(id,"tiger_sentence_early_commit")==early,"early-commit preference lost");
            check(!!api->get_option(id,"tiger_sentence_allow_duplicate_single")==duplicate,"duplicate preference lost");
            check(!!api->get_option(id,"tiger_sentence_early_commit_to_preedit")==buffer,"preedit preference lost");
        };
        auto a=create();
        const std::string mode=argv[4];
        if (mode=="read" || mode=="legacy") {
            expect(a,false,false,true);
            api->process_key(a,'a',0); expect(a,false,false,true);
            api->clear_composition(a);
        } else if (mode=="defaults") {
            expect(a,false,true,true); // custom first-use defaults
        } else {
            expect(a,true,true,false);
            auto b=create(); expect(b,true,true,false);
            api->set_option(a,"tiger_sentence_early_commit",False);
            api->set_option(a,"tiger_sentence_allow_duplicate_single",False);
            api->set_option(a,"tiger_sentence_early_commit_to_preedit",True);
            // Existing application catches up before its next input is decoded.
            api->process_key(b,'a',0); expect(b,false,false,true); api->clear_composition(b);
            // A stale session changes one flag: do not overwrite the other two.
            api->set_option(b,"tiger_sentence_early_commit",True);
            api->process_key(a,'a',0); expect(a,true,false,true); api->clear_composition(a);
            api->set_option(a,"tiger_sentence_early_commit",False);
            auto c=create(); expect(c,false,false,true);
            check(api->select_schema(c,"other"),"cannot change schema");
            check(api->select_schema(c,"tiger_sentence"),"cannot return to schema");
            expect(c,false,false,true);
            // Application restart creates a new session in the same host.
            api->destroy_session(b); b=create(); expect(b,false,false,true);
            // A key sequence must not cause a preference write (checked outside).
            api->destroy_session(b); api->destroy_session(c);
        }
        api->destroy_session(a); api->finalize();
        std::cout << "real Rime options " << mode << " passed\n";
        return 0;
    } catch (const std::exception& e) {
        std::cerr << e.what() << '\n'; return 1;
    }
}
