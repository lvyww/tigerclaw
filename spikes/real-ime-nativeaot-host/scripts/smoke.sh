#!/bin/zsh
set -euo pipefail

script_dir="${0:A:h}"
app_path="$("${script_dir}/build_package.sh")"
lsregister="/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"

# LaunchServices can register an app whenever its executable is run.  The
# smoke bundle intentionally shares the production input-source identifier,
# so it must be unregistered again even after a successful test run; otherwise
# macOS can select this unsigned development copy instead of the installed IME.
cleanup() {
  "${lsregister}" -u "${app_path}" >/dev/null 2>&1 || true
}
trap cleanup EXIT

host_path="${app_path}/Contents/MacOS/TigerClawRealImeNativeAotHost"
"${host_path}" --nativeaot-smoke
"${host_path}" --nativeaot-windows-parity-smoke
