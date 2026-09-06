#!/bin/zsh

set -euo pipefail

if [[ $# -lt 1 || $# -gt 2 || ( $# -eq 2 && "$2" != "--replace" ) ]]; then
    print -u2 "Usage: sudo $0 /absolute/path/to/TigerClaw.app [--replace]"
    exit 64
fi

source_app="$1"
target_app="/Library/Input Methods/TigerClaw.app"
lsregister="/System/Library/Frameworks/CoreServices.framework/Versions/Current/Frameworks/LaunchServices.framework/Versions/Current/Support/lsregister"

if [[ ! -d "$source_app" || ! -f "$source_app/Contents/Info.plist" ]]; then
    print -u2 "TigerClaw.app was not found at: $source_app"
    exit 66
fi

if [[ -e "$target_app" && "${2:-}" != "--replace" ]]; then
    print -u2 "Refusing to replace existing target: $target_app"
    exit 73
fi

/usr/bin/ditto "$source_app" "$target_app"
"$lsregister" -f -R -trusted "$target_app"
print "Installed $target_app"
