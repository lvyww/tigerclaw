#!/bin/zsh
set -euo pipefail

script_dir="${0:A:h}"
app_path="$("${script_dir}/build_package.sh")"
host_path="${app_path}/Contents/MacOS/TigerClawRealImeNativeAotHost"
"${host_path}" --nativeaot-smoke
"${host_path}" --nativeaot-windows-parity-smoke
