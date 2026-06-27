# Reference Sources

This directory stores upstream/reference source trees for study and comparison.

Current contents:

- `bime-master/`
- `SampleIME/`
- `weasel/`

Guidelines:

1. These trees are reference-only and not part of the active runtime/publish path.
2. Active implementation lives in `next/`.
3. Do not wire build/publish scripts to this directory.
4. If a reference change needs to be adopted, port it explicitly into `next/`.
