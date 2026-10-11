#!/bin/bash
set -euo pipefail
task_root="$(cd "$(dirname "$0")/.." && pwd)"
task_unity="${IDAS3_UNITY_EDITOR:-/Applications/Unity/Hub/Editor/6000.6.4f1/Unity.app/Contents/MacOS/Unity}"
export IDAS3_VERIFY_ROM=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --verify-rom) export IDAS3_VERIFY_ROM=1; shift ;; # debug
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done
cd "$task_root"
mkdir -p Logs Builds/iOS/Simulator Assets/Plugins/iOS
python3 Tools/Compress-iOS-Music.py
python3 Tools/Package-iOS-Data.py
cmake -S Native -B Native/build-ios-simulator -DCMAKE_SYSTEM_NAME=iOS -DCMAKE_OSX_DEPLOYMENT_TARGET=15.0 \
  -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_SYSROOT=iphonesimulator -DCMAKE_BUILD_TYPE=Release
cmake --build Native/build-ios-simulator --parallel 8
# Unity imports one archive at a time. Restore the device archive on every exit.
task_backup="$(mktemp -d "${TMPDIR:-/tmp}/idas3-simulator-plugin.XXXXXX")"
task_plugin="Assets/Plugins/iOS/libIdas3Unity.a"
if [[ -f "$task_plugin" ]]; then cp "$task_plugin" "$task_backup/device.a"; fi
restore_plugin() {
  if [[ -f "$task_backup/device.a" ]]; then cp "$task_backup/device.a" "$task_plugin";
  else rm -f "$task_plugin"; fi
  rm -rf "$task_backup"
}
trap restore_plugin EXIT
cp Native/build-ios-simulator/libIdas3Unity.a "$task_plugin"
"$task_unity" -batchmode -nographics -quit -projectPath "$task_root" -buildTarget iOS \
  -executeMethod Idas3IosBuild.BuildSimulator -logFile "$task_root/Logs/ios-simulator-unity.log"
xcodebuild -project Builds/iOS/Simulator/Xcode/Unity-iPhone.xcodeproj -scheme Unity-iPhone \
  -configuration Release -sdk iphonesimulator -destination 'generic/platform=iOS Simulator' \
  -derivedDataPath Builds/iOS/Simulator/DerivedData CODE_SIGNING_ALLOWED=NO \
  ONLY_ACTIVE_ARCH=YES ARCHS=arm64 > Logs/ios-simulator-xcode.log 2>&1
shopt -s nullglob
task_apps=("$task_root/Builds/iOS/Simulator/DerivedData/Build/Products/Release-iphonesimulator/"*.app)
if [[ ${#task_apps[@]} != 1 ]]; then
  echo "Expected one built Simulator app, found ${#task_apps[@]}" >&2; exit 1
fi
task_app="${task_apps[0]}"
if [[ -n "${IDAS3_SIMULATOR_DEVICE:-}" ]]; then
  xcrun simctl install "$IDAS3_SIMULATOR_DEVICE" "$task_app"
  if [[ "${IDAS3_KEEP_BUILD:-0}" != "1" ]]; then python3 Tools/Clean-iOS.py simulator; fi
else
  if [[ "${IDAS3_KEEP_BUILD:-0}" != "1" ]]; then python3 Tools/Clean-iOS.py simulator --keep-simulator-app; fi
  echo "Simulator app: $task_app"
fi
