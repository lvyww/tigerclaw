#!/bin/zsh
set -euo pipefail

script_dir="${0:A:h}"
project_dir="${script_dir:h}"
source_app="${project_dir}/dist/TigerClawSettingsSpike.app"
target_dir="${HOME}/Applications"
target_app="${target_dir}/TigerClaw Settings.app"

if [[ ! -d "${source_app}" ]]; then
    print -u2 "Build the settings app first: ${project_dir}/scripts/build_package.sh"
    exit 64
fi

mkdir -p "${target_dir}"
if [[ -e "${target_app}" ]]; then
    backup_app="${HOME}/.Trash/TigerClaw-Settings-before-$(date +%Y%m%d%H%M%S).app"
    mv "${target_app}" "${backup_app}"
    print "Previous settings app moved to: ${backup_app}"
fi

ditto "${source_app}" "${target_app}"
print "Installed: ${target_app}"
