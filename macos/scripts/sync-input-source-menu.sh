#!/bin/zsh
set -euo pipefail

if [[ $# -ne 2 && $# -ne 3 ]]; then
  echo "usage: sync-input-source-menu.sh <bundle-id> <mode-id> [repair|activate]" >&2
  exit 64
fi

bundle_identifier="$1"
mode_identifier="$2"
operation="${3:-repair}"
script_dir="${0:A:h}"
work_dir="$(mktemp -d /private/tmp/tigerclaw-menu-sync.XXXXXX)"
helper="${work_dir}/input-source-menu-sync"

cleanup() {
  rm -rf "${work_dir}"
}
trap cleanup EXIT

clang \
  -fobjc-arc \
  -framework AppKit \
  -framework Foundation \
  "${script_dir}/input-source-menu-sync.m" \
  -o "${helper}"

echo "synchronizing menu roster for ${bundle_identifier}"
if [[ "${operation}" == "activate" ]]; then
  "${helper}" activate "${mode_identifier}"
  exit 0
fi
if [[ "${operation}" != "repair" ]]; then
  echo "unknown synchronization operation: ${operation}" >&2
  exit 64
fi
"${helper}" repair "${mode_identifier}" "${bundle_identifier}"
killall TextInputMenuAgent >/dev/null 2>&1 || true
killall SystemUIServer >/dev/null 2>&1 || true
sleep 1
"${helper}" verify "${mode_identifier}"
