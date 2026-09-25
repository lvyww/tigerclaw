// Real librime deployment/cleanup + Lua resource loading, isolated user data only.
#include <rime_api.h>
#include <dlfcn.h>
#include <filesystem>
#include <iostream>
#include <stdexcept>
#include <string>
static void check(bool value, const char* message) {
    if (!value) throw std::runtime_error(message);
}
int main(int argc, char** argv) {
    try {
        check(argc == 4, "usage: probe owned-user shared lua-plugin");
        auto* api = rime_get_api();
        check(dlopen(argv[3], RTLD_NOW | RTLD_GLOBAL), "Lua plugin load failed");
        const char* modules[] = {"default", "lua", nullptr};
        RIME_STRUCT(RimeTraits, traits);
        traits.user_data_dir = argv[1]; traits.shared_data_dir = argv[2];
        traits.log_dir = argv[1]; traits.app_name = "rime.tiger.resources";
        traits.modules = modules;
        api->setup(&traits); api->initialize(&traits);
        if (api->start_maintenance(True)) api->join_maintenance_thread();
        // Invoke the registered deployment task even if this frontend's
        // maintenance sequence does not schedule it itself.
        check(api->run_task("cleanup_trash"), "cleanup_trash failed");
        const auto root = std::filesystem::path(argv[1]);
        check(!std::filesystem::exists(root / "tiger_sentence.lexical.bin"), "legacy top-level bin survived cleanup");
        check(std::filesystem::exists(root / "trash/tiger_sentence.lexical.bin"), "cleanup negative control not moved");
        check(std::filesystem::exists(root / "models/tiger_sentence.lexical.bin"), "canonical lexical resource lost");
        check(std::filesystem::exists(root / "models/sentence-fivegram-mobile.bin"), "fivegram lost");
        auto session = api->create_session();
        check(api->select_schema(session, "tiger_sentence"), "schema selection failed");
        char value[4096] = {};
        api->get_property(session, "review_lexical_loaded", value, sizeof(value));
        check(std::string(value) == "true", "Lua lexical resource not loaded after cleanup");
        api->get_property(session, "review_lexical_path", value, sizeof(value));
        check(std::string(value) == (root / "models/tiger_sentence.lexical.bin").string(), "Lua did not select canonical models path");
        api->set_option(session, "ascii_mode", False);
        api->set_option(session, "tiger_sentence_early_commit", False);
        for (auto c : std::string("iejryfenahbmsp")) api->process_key(session, c, 0);
        RIME_STRUCT(RimeContext, context);
        check(api->get_context(session, &context), "no context");
        check(context.menu.num_candidates > 0, "no candidates after deployment");
        std::cout << "lexical=" << value << " first=" << context.menu.candidates[0].text << '\n';
        api->free_context(&context);
        api->destroy_session(session); api->finalize();
        std::cout << "PASS real cleanup and post-deployment lexical loading\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n'; return 1;
    }
}
