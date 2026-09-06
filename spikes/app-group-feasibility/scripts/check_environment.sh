#!/usr/bin/env bash
set -euo pipefail

command_status() {
    if command -v "$1" >/dev/null 2>&1; then
        printf "%s: available\n" "$1"
    else
        printf "%s: missing\n" "$1"
    fi
}

profile_root="${HOME}/Library/MobileDevice/Provisioning Profiles"
profile_count=0
app_group_profile_count=0

if [[ -d "${profile_root}" ]]; then
    while IFS= read -r -d '' profile; do
        profile_count=$((profile_count + 1))
        if security cms -D -i "${profile}" 2>/dev/null | plutil -extract Entitlements.com.apple.security.application-groups raw -o - - >/dev/null 2>&1; then
            app_group_profile_count=$((app_group_profile_count + 1))
        fi
    done < <(find "${profile_root}" -maxdepth 1 -type f \( -name "*.provisionprofile" -o -name "*.mobileprovision" \) -print0 2>/dev/null)
fi

identity_count=0
if command -v security >/dev/null 2>&1; then
    identity_count="$(security find-identity -v -p codesigning 2>/dev/null | awk '/valid identities found/ {print $1 + 0}')"
fi

command_status swiftc
command_status codesign
command_status security
command_status plutil
printf "codesigning identities: %s valid identity/identities found (names redacted)\n" "${identity_count}"
printf "provisioning profiles: %s local profile/profile(s) found\n" "${profile_count}"
printf "app-group provisioning profiles: %s local profile/profile(s) advertise app groups\n" "${app_group_profile_count}"

if [[ "${app_group_profile_count}" == "0" ]]; then
    printf "blocking evidence: no local provisioning profile with App Group entitlements was found\n"
fi

