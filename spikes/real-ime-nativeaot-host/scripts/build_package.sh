#!/bin/zsh
set -euo pipefail

script_dir="${0:A:h}"
spike_dir="${script_dir:h}"
repo_dir="${spike_dir:h:h}"
engine_dir="${repo_dir}/spikes/engine-nativeaot"
settings_dir="${repo_dir}/spikes/avalonia-settings"
publish_dir="${engine_dir}/TigerClaw.Engine.NativeAot/bin/Release/net10.0/osx-arm64/publish"
vendor_dir="${spike_dir}/TigerClaw/Vendor"
resource_dir="${spike_dir}/TigerClaw/Resources"
menu_icon="${resource_dir}/TigerClaw.pdf"
build_dir="${spike_dir}/build"
dist_dir="${spike_dir}/dist"
configuration="${CONFIGURATION:-Release}"
sentence_model_source="${TIGERCLAW_SENTENCE_MODEL_PATH:-/Users/wuzz/Downloads/TigerClaw/Models/sentence-ngram-v2.bin}"
sentence_qwen_model_source="${TIGERCLAW_SENTENCE_QWEN_MODEL_PATH:-/Users/wuzz/Downloads/TigerClaw/sentence/Models/sentence-qwen-q8.gguf}"
sentence_native_dir="${repo_dir}/next/TigerClaw.Sentence.Native"
sentence_native_build_dir="${sentence_native_dir}/build-macos-arm64"
stable_bundle_identifier="net.tigerclaw.inputmethod.NativeAotImkSpike"
stable_mode_identifier="${stable_bundle_identifier}.Hans"

if [[ ! -f "${sentence_model_source}" ]]; then
  echo "missing sentence model: ${sentence_model_source}" >&2
  exit 1
fi
if [[ ! -f "${sentence_qwen_model_source}" ]]; then
  echo "missing sentence Qwen model: ${sentence_qwen_model_source}" >&2
  exit 1
fi

menu_icon_width="$(sips -g pixelWidth "${menu_icon}" | awk '/pixelWidth/ { print int($2) }')"
menu_icon_height="$(sips -g pixelHeight "${menu_icon}" | awk '/pixelHeight/ { print int($2) }')"
menu_icon_alpha="$(sips -g hasAlpha "${menu_icon}" | awk '/hasAlpha/ { print $2 }')"
if [[ "${menu_icon_width}" != "22" || "${menu_icon_height}" != "16" || "${menu_icon_alpha}" != "yes" ]]; then
  echo "invalid input-menu icon: expected 22x16 with alpha, got ${menu_icon_width}x${menu_icon_height} alpha=${menu_icon_alpha}" >&2
  exit 1
fi

cmake \
  -S "${sentence_native_dir}" \
  -B "${sentence_native_build_dir}" \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_OSX_ARCHITECTURES=arm64 >&2
cmake --build "${sentence_native_build_dir}" --config Release -j "$(sysctl -n hw.ncpu)" >&2

/Users/wuzz/.dotnet/dotnet publish "${engine_dir}/TigerClaw.Engine.NativeAot/TigerClaw.Engine.NativeAot.csproj" -c Release -r osx-arm64 >&2
zsh "${settings_dir}/scripts/build_package.sh" >&2

mkdir -p "${vendor_dir}" "${resource_dir}" "${build_dir}" "${dist_dir}"
cp "${publish_dir}/TigerClaw.Engine.NativeAot.dylib" "${vendor_dir}/TigerClaw.Engine.NativeAot.dylib"
cp "${publish_dir}/tigerclaw_engine_nativeaot.h" "${vendor_dir}/tigerclaw_engine_nativeaot.h"
cp "${repo_dir}/rime/tiger_sentence/tiger_sentence.dict.yaml" "${resource_dir}/tiger_sentence.dict.yaml"

xcodebuild \
  -project "${spike_dir}/TigerClawRealImeNativeAotHost.xcodeproj" \
  -scheme TigerClawRealImeNativeAotHost \
  -configuration "${configuration}" \
  -derivedDataPath "${build_dir}/DerivedData" \
  CODE_SIGNING_ALLOWED=NO \
  build >&2

app_source="${build_dir}/DerivedData/Build/Products/${configuration}/TigerClawRealImeNativeAotHost.app"
app_output="${dist_dir}/TigerClawRealImeNativeAotHost.app"
settings_app="${settings_dir}/dist/TigerClawSettingsSpike.app"
plist="${app_source}/Contents/Info.plist"
actual_bundle_identifier="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "${plist}")"
actual_parent_identifier="$(/usr/libexec/PlistBuddy -c 'Print :TISInputSourceID' "${plist}")"
actual_mode_identifier="$(/usr/libexec/PlistBuddy -c "Print :ComponentInputModeDict:tsVisibleInputModeOrderedArrayKey:0" "${plist}")"
if [[ "${actual_bundle_identifier}" != "${stable_bundle_identifier}" ||
      "${actual_parent_identifier}" != "${stable_bundle_identifier}" ||
      "${actual_mode_identifier}" != "${stable_mode_identifier}" ]]; then
  echo "input-source identity changed unexpectedly: bundle=${actual_bundle_identifier} parent=${actual_parent_identifier} mode=${actual_mode_identifier}" >&2
  exit 1
fi
ditto "${resource_dir}/拼音反查码表" "${app_source}/Contents/Resources/拼音反查码表"
mkdir -p "${app_source}/Contents/Resources/Models"
ditto "${sentence_model_source}" "${app_source}/Contents/Resources/Models/sentence-ngram-v2.bin"
ditto "${sentence_qwen_model_source}" "${app_source}/Contents/Resources/Models/sentence-qwen-q8.gguf"
mkdir -p "${app_source}/Contents/Frameworks"
ditto "${sentence_native_build_dir}/libTigerClaw.Sentence.Native.dylib" "${app_source}/Contents/Frameworks/libTigerClaw.Sentence.Native.dylib"
ditto "${settings_app}" "${app_source}/Contents/Resources/TigerClaw Settings.app"
rm -rf "${app_output}"
ditto "${app_source}" "${app_output}"
plutil -lint "${app_output}/Contents/Info.plist" >&2

# Xcode registers macOS application build products with LaunchServices even when
# they are only packaging intermediates. Keeping copies with the input method's
# bundle identifier registered outside ~/Library/Input Methods makes the input
# menu resolve an arbitrary copy and can hide the real installed source.
lsregister="/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"
"${lsregister}" -u "${app_source}" >/dev/null 2>&1 || true
"${lsregister}" -u "${app_output}" >/dev/null 2>&1 || true

echo "${app_output}"
