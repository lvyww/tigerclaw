// Exercise real librime selection, submission, LevelDb and model ranking.
// All user data belongs to the test runner; no desktop IME is modified.
#include <rime_api.h>
#include <dlfcn.h>
#include <iostream>
#include <stdexcept>
#include <string>

static RimeApi* api;
static RimeSessionId session;
static void check(bool ok, const char* message) {
    if (!ok) throw std::runtime_error(message);
}
static std::string first() {
    RIME_STRUCT(RimeContext, ctx);
    check(api->get_context(session, &ctx), "missing context");
    std::string text = ctx.menu.num_candidates ? ctx.menu.candidates[0].text : "";
    api->free_context(&ctx);
    return text;
}
static void type() {
    for (char c : std::string("zhhbi"))
        check(api->process_key(session, c, 0), "code key rejected");
}
static void start() {
    session = api->create_session();
    check(session != 0, "session creation failed");
    check(api->select_schema(session, "tiger_sentence"), "schema selection failed");
    api->set_option(session, "ascii_mode", False);
    api->set_option(session, "tiger_sentence_early_commit", False);
}
int main(int argc, char** argv) {
    try {
        check(argc == 5, "usage: probe user shared plugin selection");
        const std::string selection = argv[4];
        const bool continuation = selection.find("continue") != std::string::npos;
        const bool comma = selection.find("comma") != std::string::npos;
        const bool period = selection.find("period") != std::string::npos;
        api = rime_get_api();
        check(dlopen(argv[3], RTLD_NOW | RTLD_GLOBAL), "Lua plugin load failed");
        const char* modules[] = {"default", "lua", nullptr};
        RIME_STRUCT(RimeTraits, traits);
        traits.user_data_dir = argv[1]; traits.shared_data_dir = argv[2];
        traits.log_dir = argv[1]; traits.app_name = "rime.tiger.learning";
        traits.modules = modules;
        api->setup(&traits); api->initialize(&traits);
        if (api->start_maintenance(True)) api->join_maintenance_thread();
        start(); type();
        check(first() == "其父", "unexpected model baseline");
        if (selection.find("buffer") != std::string::npos) {
            api->set_option(session, "tiger_sentence_early_commit", True);
            api->set_option(session, "tiger_sentence_early_commit_to_preedit", True);
        }
        for (int round = 0; round < 2; ++round) {
            RIME_STRUCT(RimeContext, ctx);
            check(api->get_context(session, &ctx), "missing correction menu");
            int index = -1;
            for (int i = 0; i < ctx.menu.num_candidates; ++i)
                if (std::string(ctx.menu.candidates[i].text) == "虎娘") index = i;
            api->free_context(&ctx);
            check(index >= 0 && (round > 0 || index > 0), "missing correction or unexpected initial rank");
            if (std::string(argv[4]) == "tap") {
                check(api->select_candidate(session, index), "candidate tap rejected");
            } else {
                for (int i = 0; i < index; ++i)
                    check(api->process_key(session, 0xff09, 0), "Tab rejected");
                if (continuation)
                    for (char c : std::string("tuja"))
                        check(api->process_key(session, c, 0), "continuation rejected");
                check(api->process_key(session, comma ? ',' : period ? '.' : ' ', 0), "commit key rejected");
            }
            RIME_STRUCT(RimeCommit, commit);
            check(api->get_commit(session, &commit), "selection did not submit");
            const std::string text = commit.text ? commit.text : "";
            api->free_commit(&commit);
            const std::string expected = std::string("虎娘") + (continuation ? "我们" : "") +
                (comma ? "，" : period ? "。" : "");
            check(text == expected, "submitted wrong text");
            type();
            std::cout << argv[4] << " correction " << round + 1 << " first=" << first() << std::endl;
        }
        check(first() == "虎娘", "repeated corrections failed to promote real-model candidate");
        api->destroy_session(session); session = 0;
        api->finalize(); api->initialize(&traits);
        start(); type();
        check(first() == "虎娘", "learning did not survive engine restart");
        api->destroy_session(session); session = 0; api->finalize();
        std::cout << "real Rime learning and restart passed\n";
        return 0;
    } catch (const std::exception& e) {
        std::cerr << e.what() << '\n';
        if (session) api->destroy_session(session);
        if (api) api->finalize();
        return 1;
    }
}
