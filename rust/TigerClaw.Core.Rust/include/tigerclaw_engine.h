#ifndef TIGERCLAW_ENGINE_H
#define TIGERCLAW_ENGINE_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define TIGERCLAW_ABI_VERSION 1u

typedef struct tigerclaw_runtime_t tigerclaw_runtime_t;
typedef struct tigerclaw_session_t tigerclaw_session_t;
typedef struct tigerclaw_snapshot_t tigerclaw_snapshot_t;

typedef enum tigerclaw_status_t {
    TIGERCLAW_OK = 0,
    TIGERCLAW_ERR_INVALID_ARGUMENT = 1,
    TIGERCLAW_ERR_INVALID_UTF8 = 2,
    TIGERCLAW_ERR_INVALID_HANDLE = 3,
    TIGERCLAW_ERR_INVALID_STATE = 4,
    TIGERCLAW_ERR_OUT_OF_MEMORY = 5,
    TIGERCLAW_ERR_RESOURCE_UNAVAILABLE = 6,
    TIGERCLAW_ERR_STALE_GENERATION = 7,
    TIGERCLAW_ERR_INTERNAL_ERROR = 8,
} tigerclaw_status_t;

typedef struct tigerclaw_utf8_slice_t {
    const uint8_t *ptr;
    size_t len;
} tigerclaw_utf8_slice_t;

typedef struct tigerclaw_key_event_t {
    /* Canonical TigerClaw key values: A-Z are 0x41-0x5A, independent of host APIs. */
    uint32_t key;
    uint32_t action;
    uint32_t modifiers;
    uint64_t event_id;
} tigerclaw_key_event_t;

enum {
    TIGERCLAW_KEY_ACTION_DOWN = 1,
    TIGERCLAW_KEY_ACTION_UP = 2,
    TIGERCLAW_KEY_BACKSPACE = 0x08,
    TIGERCLAW_KEY_TAB = 0x09,
    TIGERCLAW_KEY_ENTER = 0x0D,
    TIGERCLAW_KEY_ESCAPE = 0x1B,
    TIGERCLAW_KEY_SPACE = 0x20,
    TIGERCLAW_KEY_A = 0x41,
    TIGERCLAW_KEY_Z = 0x5A,
};

enum {
    TIGERCLAW_MODIFIER_SHIFT = 1 << 0,
    TIGERCLAW_MODIFIER_CONTROL = 1 << 1,
    TIGERCLAW_MODIFIER_ALT = 1 << 2,
    TIGERCLAW_MODIFIER_META = 1 << 3,
    TIGERCLAW_MODIFIER_CAPS_LOCK = 1 << 4,
};

uint32_t tigerclaw_abi_version(void);
tigerclaw_status_t tigerclaw_runtime_create(tigerclaw_runtime_t **out_runtime);
void tigerclaw_runtime_destroy(tigerclaw_runtime_t *runtime);
tigerclaw_status_t tigerclaw_session_create(
    tigerclaw_runtime_t *runtime,
    tigerclaw_session_t **out_session);
void tigerclaw_session_destroy(tigerclaw_session_t *session);
tigerclaw_status_t tigerclaw_session_activate(tigerclaw_session_t *session);
tigerclaw_status_t tigerclaw_session_deactivate(tigerclaw_session_t *session);
tigerclaw_status_t tigerclaw_session_process_key(
    tigerclaw_session_t *session,
    const tigerclaw_key_event_t *event,
    tigerclaw_snapshot_t **out_snapshot);
void tigerclaw_snapshot_free(tigerclaw_snapshot_t *snapshot);
uint64_t tigerclaw_snapshot_revision(const tigerclaw_snapshot_t *snapshot);
tigerclaw_utf8_slice_t tigerclaw_snapshot_preedit(const tigerclaw_snapshot_t *snapshot);
tigerclaw_utf8_slice_t tigerclaw_snapshot_commit(const tigerclaw_snapshot_t *snapshot);
size_t tigerclaw_snapshot_candidate_count(const tigerclaw_snapshot_t *snapshot);
tigerclaw_utf8_slice_t tigerclaw_snapshot_candidate(
    const tigerclaw_snapshot_t *snapshot,
    size_t index);
size_t tigerclaw_snapshot_selected_index(const tigerclaw_snapshot_t *snapshot);
tigerclaw_utf8_slice_t tigerclaw_status_message(tigerclaw_status_t status);

#ifdef __cplusplus
}
#endif

#endif
