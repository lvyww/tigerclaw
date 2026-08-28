# Rust Core differential gate

Run the complete Windows gate from the repository root:

```batch
tools\run_core_diff.bat
```

The gate builds and tests both Core implementations, gives each adapter an
isolated copy of `fixture/`, generates the same tiny KN V2 model in both
sandboxes, and replays every JSONL file under `traces/`. It compares public
responses, raw/display composition state, candidates, annotations, selection,
sentence pending/settled state, UI commands and persistent config files. A
failure is written to `.core_diff/report.json` with the first JSON path that
differs; the two sandboxes remain under `.core_diff/work`.

## Capture a real TSF trace

Create an empty directory, set `BIME_TSF_TRACE_DIR` in the environment of the
target application, then start that application after installing a DLL built
from the current source. The TSF bridge writes the exact outgoing Core JSON to
one `core-trace-<pid>-<tid>.jsonl` file per host thread. Capture is completely
disabled when the variable is absent.

Before committing a capture, redact and validate it:

```batch
python tools\sanitize_core_trace.py C:\captures\core-trace-*.jsonl ^
  --output tools\core_diff\traces\notepad-basic.captured.jsonl
```

The sanitizer replaces session/event identities, HWND/PID values and window
titles while retaining physical scan, repeat, extended, NumLock and TSF-stage
metadata. Files named `*.captured.jsonl` are always subject to strict physical
metadata validation, even when synthetic fixtures are enabled for the suite.

Do not commit unsanitized traces: focus notifications may contain application
names and document titles.
