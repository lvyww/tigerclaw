#ifndef TIGERCLAW_ENGINE_NATIVEAOT_H
#define TIGERCLAW_ENGINE_NATIVEAOT_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#define TC_API __declspec(dllimport)
#else
#define TC_API __attribute__((visibility("default")))
#endif

typedef void* tc_runtime_t;
typedef void* tc_session_t;
typedef void* tc_snapshot_t;

/* UTF-8 byte slice. Strings are not required to be NUL-terminated. */
typedef struct tc_utf8_slice {
    const uint8_t* data;
    size_t length;
} tc_utf8_slice;

typedef enum tc_status {
    TC_STATUS_OK = 0,
    TC_STATUS_INVALID_ARGUMENT = 1,
    TC_STATUS_RUNTIME_ERROR = 2,
} tc_status;

typedef enum tc_input_key {
    TC_INPUT_KEY_UNKNOWN = 0,
    TC_INPUT_KEY_CHARACTER = 1,
    TC_INPUT_KEY_BACKSPACE = 2,
    TC_INPUT_KEY_ENTER = 3,
    TC_INPUT_KEY_ESCAPE = 4,
    TC_INPUT_KEY_SPACE = 5,
    TC_INPUT_KEY_TAB = 6,
    TC_INPUT_KEY_ARROW_UP = 7,
    TC_INPUT_KEY_ARROW_DOWN = 8,
    TC_INPUT_KEY_ARROW_LEFT = 9,
    TC_INPUT_KEY_ARROW_RIGHT = 10,
    TC_INPUT_KEY_DIGIT = 11,
    TC_INPUT_KEY_SEMICOLON = 12,
    TC_INPUT_KEY_QUOTE = 13,
    TC_INPUT_KEY_PAGE_PREVIOUS = 14,
    TC_INPUT_KEY_PAGE_NEXT = 15,
} tc_input_key;

typedef enum tc_key_action {
    TC_KEY_ACTION_KEY_DOWN = 0,
    TC_KEY_ACTION_KEY_UP = 1,
} tc_key_action;

typedef struct tc_input_event {
    int32_t key;
    tc_utf8_slice logical_text;
    int32_t modifiers;
    int32_t action;
    tc_utf8_slice physical_key;
    int32_t physical_scan_code;
    int32_t is_extended;
    int32_t is_repeat;
    int32_t repeat_count;
} tc_input_event;

/* Basic-input configuration is fixed for a runtime and shared by its sessions. */
typedef struct tc_engine_config {
    int32_t max_candidates;
    int32_t page_size;
    int32_t max_code_length;
    int32_t auto_commit_unique_terminal_code;
    int32_t second_candidate_semicolon;
    int32_t third_candidate_quote;
    int32_t unlimited_mixed_input;
    int32_t use_english_punctuation_in_chinese;
    int32_t slash_outputs_dunhao;
    int32_t tab_clears_composition;
    int32_t enter_clears_composition;
    int32_t pinyin_reverse_enabled;
    int32_t clear_on_no_code;
    int32_t sentence_input_enabled;
    int32_t sentence_neural_rerank_enabled;
    int32_t sentence_auto_commit_enabled;
    /* Optional UTF-8 path to a host-owned tab-separated user dictionary. */
    tc_utf8_slice user_dictionary_path;
    /* Optional candidate selection and previous/next page key sequences. */
    tc_utf8_slice selection_keys;
    tc_utf8_slice previous_page_keys;
    tc_utf8_slice next_page_keys;
    /* Optional UTF-8 path to the Windows-compatible 拼音.txt reverse-lookup table. */
    tc_utf8_slice pinyin_lexicon_path;
    /* Optional UTF-8 path to the TCSKNM01 sentence n-gram model. */
    tc_utf8_slice sentence_model_path;
    /* Optional paths to the macOS native Qwen scorer and Q8 GGUF model. */
    tc_utf8_slice sentence_qwen_native_library_path;
    tc_utf8_slice sentence_qwen_model_path;
    /* Optional UTF-8 path to the dedicated sentence-input lexicon. */
    tc_utf8_slice sentence_lexicon_path;
} tc_engine_config;

TC_API int32_t tc_runtime_create(tc_utf8_slice lexicon_path, tc_runtime_t* out_runtime);
TC_API int32_t tc_runtime_create_with_config(tc_utf8_slice lexicon_path, const tc_engine_config* config, tc_runtime_t* out_runtime);
TC_API void tc_runtime_release(tc_runtime_t runtime);

TC_API int32_t tc_session_create(tc_runtime_t runtime, tc_session_t* out_session);
TC_API int32_t tc_session_activate(tc_session_t session);
TC_API int32_t tc_session_process(tc_session_t session, const tc_input_event* input, tc_snapshot_t* out_snapshot);
/* Select a zero-based candidate from the current visible page. */
TC_API int32_t tc_session_select_candidate(tc_session_t session, int32_t page_index, tc_snapshot_t* out_snapshot);
TC_API int32_t tc_session_query_snapshot(tc_session_t session, tc_snapshot_t* out_snapshot);
TC_API int32_t tc_session_deactivate(tc_session_t session);
TC_API void tc_session_release(tc_session_t session);

/*
 * Snapshots are immutable per-call results owned by the library. String slices
 * returned from snapshot accessors remain valid only until tc_snapshot_release.
 */
TC_API int32_t tc_snapshot_get_handled(tc_snapshot_t snapshot);
TC_API int32_t tc_snapshot_get_is_composing(tc_snapshot_t snapshot);
TC_API int32_t tc_snapshot_get_selected_index(tc_snapshot_t snapshot);
TC_API int32_t tc_snapshot_get_caret(tc_snapshot_t snapshot);
TC_API int32_t tc_snapshot_get_candidate_total(tc_snapshot_t snapshot);
TC_API int32_t tc_snapshot_get_page_index(tc_snapshot_t snapshot);
TC_API int32_t tc_snapshot_get_page_count(tc_snapshot_t snapshot);
TC_API int32_t tc_snapshot_get_sentence_rerank_pending(tc_snapshot_t snapshot);
/* 0 = none, 1 = open add-word window, 2 = toggle candidate visibility. */
TC_API int32_t tc_snapshot_get_action(tc_snapshot_t snapshot);
TC_API tc_utf8_slice tc_snapshot_get_preedit(tc_snapshot_t snapshot);
TC_API tc_utf8_slice tc_snapshot_get_composition_prefix(tc_snapshot_t snapshot);
TC_API tc_utf8_slice tc_snapshot_get_active_input_code(tc_snapshot_t snapshot);
TC_API tc_utf8_slice tc_snapshot_get_commit(tc_snapshot_t snapshot);
TC_API int32_t tc_snapshot_get_candidate_count(tc_snapshot_t snapshot);
TC_API tc_utf8_slice tc_snapshot_get_candidate(tc_snapshot_t snapshot, int32_t index);
TC_API void tc_snapshot_release(tc_snapshot_t snapshot);

#ifdef __cplusplus
}
#endif

#endif
