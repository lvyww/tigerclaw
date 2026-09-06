#!/bin/zsh
set -euo pipefail

script_dir="${0:A:h}"
spike_dir="${script_dir:h}"
repo_dir="${spike_dir:h:h}"
publish_dir="${spike_dir}/TigerClaw.Engine.NativeAot/bin/Release/net10.0/osx-arm64/publish"
library_path="${publish_dir}/TigerClaw.Engine.NativeAot.dylib"
output_path="${script_dir}/NativeAotSwiftSmoke"

if [[ ! -f "${library_path}" ]]; then
  /Users/wuzz/.dotnet/dotnet publish "${spike_dir}/TigerClaw.Engine.NativeAot/TigerClaw.Engine.NativeAot.csproj" -c Release -r osx-arm64
fi

swiftc "${script_dir}/main.swift" \
  -import-objc-header "${publish_dir}/tigerclaw_engine_nativeaot.h" \
  "${library_path}" \
  -Xlinker -rpath -Xlinker "${publish_dir}" \
  -o "${output_path}"

"${output_path}" "${repo_dir}/rime/tiger_sentence/tiger_sentence.dict.yaml"
