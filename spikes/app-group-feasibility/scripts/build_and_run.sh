#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root_dir="$(cd "${script_dir}/.." && pwd)"
build_dir="${root_dir}/build"
derived_dir="${build_dir}/derived"
bin_path="${build_dir}/AppGroupProbe"

app_group_id="${APP_GROUP_ID:-group.tigerclaw.feasibility.local}"
sign_identity="${SIGN_IDENTITY:--}"
ime_bundle_id="${IME_BUNDLE_ID:-local.tigerclaw.feasibility.imeprobe}"
settings_bundle_id="${SETTINGS_BUNDLE_ID:-local.tigerclaw.feasibility.settingsprobe}"
probe_value="${PROBE_VALUE:-shared-from-settings}"
if [[ "${app_group_id}" == "group.tigerclaw.feasibility.local" ]]; then
    app_group_status="default-local"
else
    app_group_status="provided"
fi

if [[ "${app_group_id}" != group.* ]]; then
    printf "ERROR APP_GROUP_ID must start with 'group.'\n" >&2
    exit 2
fi

require_tool() {
    if ! command -v "$1" >/dev/null 2>&1; then
        printf "ERROR missing required tool: %s\n" "$1" >&2
        exit 2
    fi
}

render_template() {
    local input="$1"
    local output="$2"
    local bundle_name="${3:-}"
    local bundle_id="${4:-}"

    sed \
        -e "s|__APP_GROUP_ID__|${app_group_id}|g" \
        -e "s|__BUNDLE_NAME__|${bundle_name}|g" \
        -e "s|__BUNDLE_ID__|${bundle_id}|g" \
        "${input}" > "${output}"
}

make_bundle() {
    local app_name="$1"
    local bundle_id="$2"
    local app_dir="${build_dir}/${app_name}.app"

    rm -rf "${app_dir}"
    mkdir -p "${app_dir}/Contents/MacOS"
    cp "${bin_path}" "${app_dir}/Contents/MacOS/AppGroupProbe"
    render_template "${root_dir}/templates/Info.plist" "${app_dir}/Contents/Info.plist" "${app_name}" "${bundle_id}"
    plutil -lint "${app_dir}/Contents/Info.plist" >/dev/null
}

sign_bundle() {
    local app_name="$1"
    local entitlements="${derived_dir}/${app_name}.entitlements"
    local app_dir="${build_dir}/${app_name}.app"

    render_template "${root_dir}/templates/AppGroup.entitlements" "${entitlements}"
    plutil -lint "${entitlements}" >/dev/null
    codesign --force --sign "${sign_identity}" --options runtime --entitlements "${entitlements}" "${app_dir}" >/dev/null
    codesign --verify --strict --deep "${app_dir}"
}

run_probe() {
    local app_name="$1"
    shift
    "${build_dir}/${app_name}.app/Contents/MacOS/AppGroupProbe" "$@"
}

require_tool swiftc
require_tool codesign
require_tool plutil

rm -rf "${build_dir}"
mkdir -p "${derived_dir}"

swiftc "${root_dir}/Sources/AppGroupProbe/main.swift" -o "${bin_path}"
make_bundle "IMEProbe" "${ime_bundle_id}"
make_bundle "SettingsProbe" "${settings_bundle_id}"
sign_bundle "IMEProbe"
sign_bundle "SettingsProbe"

printf "BUILD_OK apps=%s,%s signing=%s app_group=%s\n" "IMEProbe.app" "SettingsProbe.app" "$(if [[ "${sign_identity}" == "-" ]]; then printf "ad-hoc"; else printf "provided"; fi)" "${app_group_status}"

run_probe "SettingsProbe" --role Settings --app-group "${app_group_id}" --write "${probe_value}"
run_probe "IMEProbe" --role IME --app-group "${app_group_id}" --read
