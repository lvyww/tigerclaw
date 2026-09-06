#!/bin/zsh
set -euo pipefail

task_root="$(cd "$(dirname "$0")/.." && pwd)"
output_root="${1:-/tmp/TigerClawHybridSpike.app}"
dotnet_root="/Users/wuzz/.dotnet"
native_output="/tmp/tigerclaw-hybrid-nativeaot"

DOTNET_ROOT="$dotnet_root" "$dotnet_root/dotnet" publish "$task_root/DummyEngine/DummyEngine.csproj" \
  -c Release -r osx-arm64 --self-contained true -o "$native_output"

rm -rf "$output_root"
mkdir -p "$output_root/Contents/MacOS" "$output_root/Contents/Frameworks" "$output_root/Contents/Resources"
cp "$task_root/HostBundle/Info.plist" "$output_root/Contents/Info.plist"
cp "$native_output/DummyEngine.dylib" "$output_root/Contents/Frameworks/DummyEngine.dylib"
cp "$task_root/../../macos/TigerClaw/Resources/TigerClaw.pdf" "$output_root/Contents/Resources/TigerClaw.pdf"
cp "$task_root/../../macos/TigerClaw/Resources/TigerClaw.icns" "$output_root/Contents/Resources/TigerClaw.icns"

swiftc -parse-as-library -module-name TigerClawHybridSpike \
  "$task_root/HostBundle/HostMain.swift" \
  "$task_root/HostAdapter/NativeAotBridge.swift" \
  "$task_root/HostAdapter/BridgeInputController.swift" \
  "$native_output/DummyEngine.dylib" \
  -framework AppKit -framework Carbon -framework InputMethodKit \
  -Xlinker -rpath -Xlinker '@executable_path/../Frameworks' \
  -o "$output_root/Contents/MacOS/TigerClawHybridSpike"

plutil -lint "$output_root/Contents/Info.plist"
signing_identity="$(security find-identity -v -p codesigning | sed -n 's/.*"\(Apple Development:.*\)"/\1/p' | head -n 1)"
if [[ -z "$signing_identity" ]]; then
  echo "No Apple Development signing identity is available" >&2
  exit 1
fi
codesign --force --sign "$signing_identity" \
  --entitlements "$task_root/HostBundle/TigerClawHybridSpike.entitlements" \
  "$output_root"
echo "$output_root"
