# TigerClaw query-library integration

The unmodified source inventory and fixed libime checkout revision are recorded
in UPSTREAM.json. The query build is next/TigerClaw.Pinyin.Native/CMakeLists.txt;
training and executable sources are excluded. Original licenses are retained.

Local upstream-source patch: util/file.cc, OpenReadOrThrow on Windows converts
the UTF-8 filename with MultiByteToWideChar(CP_UTF8) and opens it with _wopen.
This allows a model inside a Chinese-named scheme directory. POSIX behavior and
KenLM scoring/model binary formats are unchanged.

The C ABI adapter and build configuration are outside this vendored directory.
The Windows DLL uses a static MSVC CRT and retains the original LGPL exceptions.
The internal package includes this source directory and the adapter/build files
in kenlm-source.zip so the query DLL can be rebuilt and replaced independently.
