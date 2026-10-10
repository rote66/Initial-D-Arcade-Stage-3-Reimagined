#!/bin/bash
set -euo pipefail
task_root="$(cd "$(dirname "$0")" && pwd)"
exec bash "$task_root/Tools/Build-iOS.sh" "$@"
