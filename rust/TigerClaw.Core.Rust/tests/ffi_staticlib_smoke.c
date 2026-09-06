#include "tigerclaw_engine.h"

int main(void) {
    tigerclaw_runtime_t *runtime = 0;
    tigerclaw_session_t *session = 0;
    tigerclaw_snapshot_t *snapshot = 0;
    const tigerclaw_key_event_t event = {0x41, 1, 0, 1};

    if (tigerclaw_abi_version() != TIGERCLAW_ABI_VERSION) return 1;
    if (tigerclaw_runtime_create(&runtime) != TIGERCLAW_OK) return 2;
    if (tigerclaw_session_create(runtime, &session) != TIGERCLAW_OK) return 3;
    if (tigerclaw_session_activate(session) != TIGERCLAW_OK) return 4;
    if (tigerclaw_session_process_key(session, &event, &snapshot) != TIGERCLAW_OK) return 5;
    if (tigerclaw_snapshot_revision(snapshot) != 2) return 6;
    {
        tigerclaw_utf8_slice_t preedit = tigerclaw_snapshot_preedit(snapshot);
        if (preedit.len != 1 || preedit.ptr[0] != 'a') return 7;
    }

    tigerclaw_snapshot_free(snapshot);
    tigerclaw_session_deactivate(session);
    tigerclaw_session_destroy(session);
    tigerclaw_runtime_destroy(runtime);
    return 0;
}
