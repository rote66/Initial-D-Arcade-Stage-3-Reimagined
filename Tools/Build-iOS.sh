#!/bin/bash
set -euo pipefail
task_root="$(cd "$(dirname "$0")/.." && pwd)"
task_unity="${IDAS3_UNITY_EDITOR:-/Applications/Unity/Hub/Editor/6000.6.4f1/Unity.app/Contents/MacOS/Unity}"
task_mode=core-small
task_jobs="${IDAS3_BUILD_JOBS:-8}"
task_build_number="${IDAS3_BUILD_NUMBER:-18}"
task_keep="${IDAS3_KEEP_BUILD:-0}"
task_force=0
task_dry=0
task_verify=0
task_clean=0
usage() {
  cat <<'HELP'
Usage: ./build-ios.sh [--mode MODE] [options]
       ./build-ios.sh MODE [options]
       ./build-ios.sh --clean [--dry-run]

Modes:
  full        Full IPA with original game data embedded; no import required.
  core-full   Universal core IPA + full data ZIP with original music.
  core-small  The SAME core IPA + data ZIP with 117 songs at 192 kbps MP3 (default).

Options:
  --unity PATH       Unity editor executable (or IDAS3_UNITY_EDITOR).
  --jobs N           Native build parallelism (default 8).
  --build-number N   iOS build number (default 18, or IDAS3_BUILD_NUMBER).
  --clean            Clean rebuildable caches and exit; retain IPA/data/saves.
  --keep-build       Keep Xcode exports and DerivedData after success.
  --force-data       Recreate the selected data ZIP even if unchanged.
  --dry-run          Print the commands and outputs without writing/building.
  -h, --help         Show this help.

Outputs: Builds/iOS/InitialD-iOS15-full.ipa or InitialD-iOS15-core.ipa;
         InitialD-GameData-v3-full.zip / InitialD-GameData-v3-mp3.zip.
Core supports BOTH data ZIPs; the IPA filename/audio support does not vary.
All IPAs use the existing ad-hoc signature and iOS 15 ARM64 settings.
HELP
}
need_value() { if [[ $# -lt 2 || -z "$2" ]]; then echo "Missing value for $1" >&2; exit 2; fi; }
while [[ $# -gt 0 ]]; do
  case "$1" in
    --mode) need_value "$@"; task_mode="$2"; shift 2 ;;
    full|core-full|core-small) task_mode="$1"; shift ;;
    --unity) need_value "$@"; task_unity="$2"; shift 2 ;;
    --jobs) need_value "$@"; task_jobs="$2"; shift 2 ;;
    --build-number) need_value "$@"; task_build_number="$2"; shift 2 ;;
    --keep-build) task_keep=1; shift ;;
    --force-data) task_force=1; shift ;;
    --dry-run) task_dry=1; shift ;;
    --verify-rom) task_verify=1; shift ;; # debug
    --clean|clean) task_clean=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown argument: $1 (see --help)" >&2; exit 2 ;;
  esac
done
case "$task_mode" in full|core-full|core-small) ;; *) echo "Unknown mode: $task_mode" >&2; exit 2 ;; esac
for task_number in "$task_jobs" "$task_build_number"; do
  if [[ ! "$task_number" =~ ^[1-9][0-9]*$ ]]; then echo "Jobs and build number must be positive integers" >&2; exit 2; fi
done
task_variant=full
if [[ "$task_mode" == core-small ]]; then task_variant=small; fi
task_data_args=(--variant "$task_variant")
if [[ "$task_force" == 1 ]]; then task_data_args+=(--force); fi
export IDAS3_BUILD_NUMBER="$task_build_number"
export IDAS3_VERIFY_ROM="$task_verify"
run() {
  if [[ "$task_dry" == 1 ]]; then printf '%q ' "$@"; printf '\n'; else "$@"; fi
}
run_xcode() {
  if [[ "$task_dry" == 1 ]]; then run "$@"; return; fi
  if ! "$@" > Logs/ios-xcode.log 2>&1; then tail -n 60 Logs/ios-xcode.log >&2; return 1; fi
}
cd "$task_root"
if [[ "$task_clean" == 1 ]]; then
  run python3 Tools/Clean-iOS.py all
  exit 0
fi
if [[ "$task_dry" != 1 ]]; then
  [[ "$(uname -s)" == Darwin ]] || { echo "iOS builds require macOS and Xcode" >&2; exit 1; }
  [[ -x "$task_unity" ]] || { echo "Unity executable not found: $task_unity (use --unity)" >&2; exit 1; }
  for task_tool in python3 cmake xcodebuild codesign; do
    command -v "$task_tool" >/dev/null || { echo "Required tool not found: $task_tool" >&2; exit 1; }
  done
  python3 -c 'import sys; assert sys.version_info >= (3, 11), "Python 3.11+ is required"'
  mkdir -p Logs Builds/iOS Assets/Plugins/iOS
fi
echo "iOS package mode: $task_mode; build: $task_build_number; ROM validation: $task_verify; core audio: original + MP3"
if [[ "$task_variant" == small ]]; then run python3 Tools/Compress-iOS-Music.py; fi
run python3 Tools/Package-iOS-Data.py "${task_data_args[@]}"
run cmake -S Native -B Native/build-ios -DCMAKE_SYSTEM_NAME=iOS -DCMAKE_OSX_DEPLOYMENT_TARGET=15.0 \
  -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_SYSROOT=iphoneos -DCMAKE_BUILD_TYPE=Release
run cmake --build Native/build-ios --parallel "$task_jobs"
run cp Native/build-ios/libIdas3Unity.a Assets/Plugins/iOS/libIdas3Unity.a
run "$task_unity" -batchmode -nographics -quit -projectPath "$task_root" -buildTarget iOS \
  -executeMethod Idas3IosBuild.Build -logFile "$task_root/Logs/ios-unity.log"
run_xcode xcodebuild -project Builds/iOS/Xcode/Unity-iPhone.xcodeproj -scheme Unity-iPhone \
  -configuration Release -sdk iphoneos -destination 'generic/platform=iOS' \
  -derivedDataPath Builds/iOS/DerivedData CODE_SIGNING_ALLOWED=NO
run python3 Tools/Package-iOS.py --mode "$task_mode"
run python3 Tools/Validate-iOS.py --mode "$task_mode"
if [[ "$task_keep" != 1 ]]; then run python3 Tools/Clean-iOS.py device; fi
if [[ "$task_mode" == full ]]; then
  echo "Output: $task_root/Builds/iOS/InitialD-iOS15-full.ipa (data embedded)"
else
  echo "Output: $task_root/Builds/iOS/InitialD-iOS15-core.ipa"
  if [[ "$task_variant" == small ]]; then task_data_name=InitialD-GameData-v3-mp3.zip;
  else task_data_name=InitialD-GameData-v3-full.zip; fi
  echo "Data: $task_root/Builds/iOS/$task_data_name"
fi
