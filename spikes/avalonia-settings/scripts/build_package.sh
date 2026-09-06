#!/bin/zsh
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "$0")" && pwd)"
project_dir="$(cd -- "$script_dir/.." && pwd)"
publish_dir="$project_dir/bin/Release/net10.0/osx-arm64/publish"
dist_dir="$project_dir/dist"
app_name="TigerClawSettingsSpike.app"
app_path="$dist_dir/$app_name"
temporary_dir="$(mktemp -d "${TMPDIR:-/tmp}/tigerclaw-settings-package.XXXXXX")"
temporary_app="$temporary_dir/$app_name"

cleanup() {
    rm -rf -- "$temporary_dir"
}
trap cleanup EXIT

dotnet_bin="$(command -v dotnet || true)"
if [[ -z "$dotnet_bin" ]]; then
    dotnet_bin="/Users/wuzz/.dotnet/dotnet"
fi
"$dotnet_bin" publish "$project_dir/TigerClawSettingsSpike.csproj" \
    -c Release \
    -r osx-arm64 \
    --self-contained true

mkdir -p "$temporary_app/Contents/MacOS" "$temporary_app/Contents/Resources" "$dist_dir"
ditto "$publish_dir" "$temporary_app/Contents/MacOS"
ditto "$project_dir/macos/Info.plist" "$temporary_app/Contents/Info.plist"
chmod +x "$temporary_app/Contents/MacOS/TigerClawSettingsSpike"
plutil -lint "$temporary_app/Contents/Info.plist" >/dev/null
file "$temporary_app/Contents/MacOS/TigerClawSettingsSpike" | grep -q "arm64"

if [[ -e "$app_path" ]]; then
    backup_path="$dist_dir/${app_name%.app}-previous-$(date +%Y%m%d%H%M%S).app"
    mv "$app_path" "$backup_path"
    print "Previous bundle preserved at: $backup_path"
fi

mv "$temporary_app" "$app_path"
print "Packaged: $app_path"
