# Engine Golden Fixtures

These JSON files are the portable behavioral contract for the C# reference, the
TigerClaw.Engine .NET runtime and, where applicable, the macOS adapter. They are intentionally
platform-neutral: no Windows virtual key code or macOS key code appears in a
fixture.

## Status

The fixture format and seed cases exist. The Phase 1 experimental runner in
`spikes/real-engine-slice/TigerClaw.Engine.Experimental.Tests/` consumes
`basic_real_code_commit.json` and verifies that single basic slice. The rest of
the fixture set is **not currently executable test evidence**; do not report
mixed-input, sentence or replay fixtures as passing until a C# reference
exporter and broader TigerClaw.Engine runner consume the same schema.

## Schema

\`\`\`json
{
  "schema_version": 1,
  "name": "stable-case-name",
  "baseline": {
    "commit": "full C# baseline SHA",
    "source_test": "C# test method"
  },
  "resources": {
    "model_sha256": null,
    "lexicon": { "code": ["candidate"] }
  },
  "initial_config": { "config key": "value" },
  "events": [
    { "type": "key", "key": "a", "action": "KeyDown", "modifiers": [] }
  ],
  "expected": {
    "updates": [
      { "after_event": 0, "preedit": "a", "commit": null }
    ]
  }
}
\`\`\`

\`events\` represent semantic keys. Platform adapters map Windows VK and macOS
\`NSEvent\` data to these semantic keys before Engine comparison. Fixtures may
also carry host evidence such as \`physical_key\`, \`physical_scan_code\`,
\`is_extended\`, and \`is_repeat\`. The Phase 1 runner parses those fields;
basic composition assertions use the semantic key and logical text.

An expected update may assert \`preedit\`, \`raw_input\`, \`active_code\`,
\`candidates\`, \`selected_index\`, \`handled\`, \`cancel_composition\` and
\`commit\`. \`commit\` is an event result: it must be delivered at most once for
the matching input event and must never reappear in a later read-only snapshot.

## Fixture conventions

- Use inline lexicons for deterministic unit cases.
- Set \`model_sha256\` for every model-backed sentence case; \`null\` is valid
  only for cases using the neutral test model.
- Keep every expected candidate list in exact order.
- Include a replay identity for protocol idempotency cases.
- Add lifecycle cases only after Phase 1 fixes the Windows/session mapping and
  commit-or-clear policy.
