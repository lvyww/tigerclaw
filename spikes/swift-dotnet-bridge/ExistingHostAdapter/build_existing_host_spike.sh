#!/bin/zsh
set -euo pipefail

task_root="$(cd "$(dirname "$0")/.." && pwd)"
repo_root="$(cd "$task_root/../.." && pwd)"
output_root="${1:-/tmp/TigerClaw.app}"
native_output="/tmp/tigerclaw-existing-host-nativeaot"

DOTNET_ROOT=/Users/wuzz/.dotnet /Users/wuzz/.dotnet/dotnet publish "$task_root/DummyEngine/DummyEngine.csproj" -c Release -r osx-arm64 --self-contained true -o "$native_output"
rm -rf "$output_root"
mkdir -p "$output_root/Contents/MacOS" "$output_root/Contents/Frameworks" "$output_root/Contents/Resources"
cp "$repo_root/macos/TigerClaw/Info.plist" "$output_root/Contents/Info.plist"
plutil -replace CFBundleExecutable -string TigerClaw "$output_root/Contents/Info.plist"
plutil -replace CFBundleIdentifier -string net.tigerclaw.inputmethod.TigerClaw "$output_root/Contents/Info.plist"
plutil -replace CFBundleName -string TigerClaw "$output_root/Contents/Info.plist"
plutil -replace CFBundleDisplayName -string TigerClaw "$output_root/Contents/Info.plist"
plutil -replace CFBundleVersion -string 1 "$output_root/Contents/Info.plist"
plutil -replace InputMethodConnectionName -string net.tigerclaw.inputmethod.TigerClaw_Connection "$output_root/Contents/Info.plist"
plutil -replace NSPrincipalClass -string TigerClaw.TigerClawApplication "$output_root/Contents/Info.plist"
cp "$repo_root/macos/TigerClaw/Resources/TigerClaw.pdf" "$output_root/Contents/Resources/TigerClaw.pdf"
cp "$repo_root/macos/TigerClaw/Resources/TigerClaw.icns" "$output_root/Contents/Resources/TigerClaw.icns"
cp "$native_output/DummyEngine.dylib" "$output_root/Contents/Frameworks/DummyEngine.dylib"

swiftc -parse-as-library -module-name TigerClaw \
  "$repo_root/macos/TigerClaw/Sources/AppDelegate.swift" \
  "$repo_root/macos/TigerClaw/Sources/InputSourceInstaller.swift" \
  "$repo_root/macos/TigerClaw/Sources/LifecycleTrace.swift" \
  "$task_root/HostAdapter/NativeAotBridge.swift" \
  "$task_root/ExistingHostAdapter/TigerClawInputController.swift" \
  "$native_output/DummyEngine.dylib" \
  -framework AppKit -framework Carbon -framework InputMethodKit \
  -Xlinker -rpath -Xlinker '@executable_path/../Frameworks' \
  -o "$output_root/Contents/MacOS/TigerClaw"

signing_identity="$(security find-identity -v -p codesigning | sed -n 's/.*"\(Apple Development:.*\)"/\1/p' | head -n 1)"
codesign --force --sign "$signing_identity" --entitlements "$repo_root/macos/TigerClaw/TigerClaw.entitlements" "$output_root"
