// Real librime preedit/commit and Unicode deletion regression. Isolated data.
#include <rime_api.h>
#include <dlfcn.h>
#include <iostream>
#include <stdexcept>
#include <string>
#include <chrono>

static RimeApi* api;
static RimeSessionId session;
static std::string committed;
static void check(bool ok, const char* message) {
    if (!ok) throw std::runtime_error(message);
}
static void drain() {
    RIME_STRUCT(RimeCommit, c);
    if (api->get_commit(session, &c)) {
        if (c.text) committed += c.text;
        api->free_commit(&c);
    }
}
static void key(int code) { api->process_key(session, code, 0); drain(); }
static void type(const std::string& raw) { for (unsigned char c : raw) key(c); }
static std::string property(const char* name) {
    char value[4096] = {};
    api->get_property(session, name, value, sizeof(value));
    return value;
}
static std::string first() {
    RIME_STRUCT(RimeContext, c);
    check(api->get_context(session, &c), "no context");
    std::string text = c.menu.num_candidates ? c.menu.candidates[0].text : "";
    const std::string preedit = c.composition.preedit ? c.composition.preedit : "";
    api->free_context(&c);
    if (preedit.find('~') != std::string::npos)
        throw std::runtime_error("private marker leaked to preedit: " + preedit + " candidate=" + text);
    const auto prefix = property("tiger_sentence_buffered_text");
    check(prefix.empty() || preedit.find(prefix) == 0, "buffer disappeared from preedit");
    if (!prefix.empty() && std::string(api->get_input(session)) == "~") {
        check(preedit == prefix, "text-only preedit retained deleted characters");
        RIME_STRUCT(RimeContext, hidden);
        check(api->get_context(session, &hidden), "no text-only context");
        const int count = hidden.menu.num_candidates;
        api->free_context(&hidden);
        check(count == 0, "text-only preedit leaked internal candidates");
    }
    return text;
}
static void reset(bool buffered = true) {
    api->clear_composition(session); drain(); committed.clear();
    api->set_option(session, "ascii_mode", False);
    api->set_option(session, "tiger_sentence_early_commit", True);
    api->set_option(session, "tiger_sentence_early_commit_to_preedit", buffered);
}
static void held(const char* text) {
    check(committed.empty(), "buffered prefix reached application");
    check(property("tiger_sentence_buffered_text") == text, "wrong buffered text");
    first();
}
int main(int argc, char** argv) {
    try {
        check(argc >= 4 && argc <= 6, "usage: probe user shared plugin [production | raw mode]");
        api = rime_get_api();
        check(dlopen(argv[3], RTLD_NOW | RTLD_GLOBAL), "Lua plugin load failed");
        const char* modules[] = {"default", "lua", nullptr};
        RIME_STRUCT(RimeTraits, traits);
        traits.user_data_dir = argv[1]; traits.shared_data_dir = argv[2];
        traits.log_dir = argv[1]; traits.app_name = "rime.tiger.preedit"; traits.modules = modules;
        api->setup(&traits); api->initialize(&traits);
        if (api->start_maintenance(True)) api->join_maintenance_thread();
        session = api->create_session();
        check(api->select_schema(session, "tiger_sentence"), "schema selection failed");
        if (argc == 6) {
            for (int run = 0; run < 3; ++run) {
                reset(std::string(argv[5]) == "buffer");
                const std::string raw = argv[4];
                for (size_t i = 0; i < raw.size(); ++i) {
                    auto started = std::chrono::steady_clock::now();
                    key(raw[i]); first();
                    auto us = std::chrono::duration_cast<std::chrono::microseconds>(
                        std::chrono::steady_clock::now() - started).count();
                    std::cout << "BENCH," << argv[5] << ',' << run << ',' << i + 1 << ',' << us << '\n';
                }
            }
            api->destroy_session(session); session = 0; api->finalize(); return 0;
        }
        if (argc == 5) {
            reset(); type("kospfify");
            check(!property("tiger_sentence_buffered_text").empty(), "production sample did not buffer");
            const auto pending = property("tiger_sentence_buffered_text");
            while (std::string(api->get_input(session)).size() > 1) key(0xff08);
            held(pending.c_str());
            check(first().empty(), "production boundary leaked placeholder");
            key(0xff08); held("测"); key(' ');
            check(committed == "测", "production deleted display and submission diverged");
            reset(); type("kospfify");
            key(',');
            check(committed == "测试一下，", "production comma lost or reordered text");
            check(std::string(api->get_input(session)).empty(), "production comma left composition");
            api->destroy_session(session); session = 0; api->finalize();
            std::cout << "production kospfify comma passed\n";
            return 0;
        }

        reset(false); type("vpa"); check(committed == "刘", "default early commit changed");
        reset(); type("vpa"); held("刘");
        check(first() == "a", "incomplete tail should not repeat buffered text");
        key(0xff08); held("刘"); check(first().empty(), "text-only preedit repeated prefix in menu");
        key(0xff54); key(0xff09); held("刘");
        key(0xff08); check(committed.empty() && std::string(api->get_input(session)).empty(), "buffer delete did not finish");
        reset(); type("vpa"); key(0xff08); key(' ');
        check(committed == "刘", "text-only confirmation failed");
        reset(); type("vpa"); key(0xff08); type("ab");
        check(first() == "甲", "candidate hiding survived resumed input");
        key(' '); check(committed == "刘甲", "resumed text-only composition lost prefix");
        reset(); type("gha"); held("𠀀"); key(0xff08); held("𠀀"); key(0xff08);
        check(committed.empty() && std::string(api->get_input(session)).empty(), "supplementary Unicode deletion split bytes");

        reset(); type("vpab"); held("刘"); check(first() == "甲", "candidate repeated buffered prefix");
        key(' '); check(committed == "刘甲", "space lost or duplicated prefix");
        type("ab"); check(first() == "甲", "submitted prefix resurrected");
        reset(); type("vpab");
        check(api->select_candidate(session, 0), "tap rejected"); drain();
        check(committed == "刘甲", "tap lost or duplicated prefix");
        reset(); type("vpab"); key(',');
        check(committed == "刘甲，", "comma lost or reordered prefix");
        check(std::string(api->get_input(session)).empty(), "comma left raw code");
        reset(); type("vpa"); key(',');
        check(committed == "刘a，", "incomplete tail comma lost buffered text");
        reset(); type("vpa"); key(0xff08); key(',');
        check(committed == "刘，", "text-only comma lost buffered prefix");
        reset(); type("vpab"); key('.');
        check(committed == "刘甲。", "period bypassed punctuation table");
        reset(); api->set_option(session, "ascii_punct", True); type("vpab");
        const bool handled_ascii_comma = api->process_key(session, ',', 0); drain();
        check(committed == "刘甲," || (!handled_ascii_comma && committed == "刘甲"),
              "ASCII punctuation was neither submitted nor passed through");
        api->set_option(session, "ascii_punct", False);
        reset(); type("vpij");
        check(api->select_candidate(session, 1), "non-first tap rejected"); drain();
        check(committed == "刘乙", "non-first tap lost or duplicated prefix");

        reset(); type("cda"); held("团圆");
        key(0xff08); held("团圆"); key(0xff08); held("团");
        type("ab"); check(first() == "甲", "candidate repeated partially deleted prefix");
        key(' '); check(committed == "团甲", "partial word deletion commit failed");
        reset(); type("qra"); held("甲乙丙"); key(0xff08); held("甲乙丙");
        key(0xff08); held("甲乙"); key(0xff08); held("甲"); key(0xff08);
        check(committed.empty() && std::string(api->get_input(session)).empty(),
              "consecutive buffered deletion failed to clear composition");

        reset(); type("vpa"); key(0xff0d); check(committed == "刘a", "Return lost buffered text");
        reset(); type("vpa"); key(0xffe1);
        api->process_key(session, 0xffe1, 1 << 30); drain();
        check(committed == "刘a", "ASCII switching leaked marker or lost buffer");
        check(api->get_option(session, "ascii_mode"), "native Shift toggle stopped working");
        reset(false); type("ab"); key(0xffe1);
        api->process_key(session, 0xffe1, 1 << 30); drain();
        check(committed == "ab", "ordinary-mode commit_code binding changed");
        reset(); type("vpa"); api->set_option(session, "ascii_mode", True); drain();
        check(committed == "刘a", "menu ASCII toggle lost buffered composition");
        reset(); type("vpa"); key(0xffe5);
        check(committed.empty() && property("tiger_sentence_buffered_text").empty(), "Caps clear did not cancel buffer");
        reset(); type("vpab"); key(0xffe1);
        api->process_key(session, 0xff09, 1); drain();
        api->process_key(session, 0xffe1, 1 << 30); drain();
        held("刘"); check(!api->get_option(session, "ascii_mode"), "Shift+Tab toggled ASCII");
        reset(); type("vpa"); key(0xff50); key('b');
        held("刘"); check(std::string(api->get_input(session)).front() == '~', "Home insertion escaped marker");
        reset(); type("vpa"); key(0xff1b);
        check(committed.empty() && property("tiger_sentence_buffered_text").empty(), "Escape did not cancel buffer");
        type("ab"); check(first() == "甲", "cancelled text resurrected");

        reset(); type("vpa");
        api->set_option(session, "tiger_sentence_early_commit_to_preedit", False);
        type("b"); key(' '); check(committed == "刘甲", "option toggle lost pending text");

        reset(); type("ef");
        const int learned_before = std::stoi(property("review_learning_count"));
        key(0xff09); type("a"); held("丙");
        check(property("review_learning_count") == std::to_string(learned_before), "staging learned before host submission");
        key(0xff08); held("丙"); key(0xff08);
        check(property("review_learning_count") == std::to_string(learned_before), "deleted correction learned");
        reset(); type("ef"); key(0xff09); type("a"); held("丙");
        type("b"); key(' '); check(committed == "丙甲", "Tab buffered commit failed");
        check(property("review_learning_count") == std::to_string(learned_before + 1), "final submission did not learn once");

        reset(); type("vpa");
        check(api->select_schema(session, "other"), "schema switch failed"); drain();
        check(committed.empty(), "schema switch submitted buffered text");
        check(api->select_schema(session, "tiger_sentence"), "schema return failed");
        check(property("tiger_sentence_buffered_text").empty(), "schema switch retained old buffer");

        api->destroy_session(session); session = 0; api->finalize();
        std::cout << "real Rime buffered preedit, deletion, submission and learning passed\n";
        return 0;
    } catch (const std::exception& e) {
        std::cerr << e.what() << " committed=" << committed << '\n';
        if (session) api->destroy_session(session);
        if (api) api->finalize();
        return 1;
    }
}
