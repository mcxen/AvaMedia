#!/usr/bin/env bash
set -euo pipefail
capture_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
repo_dir=$(cd -- "$capture_dir/../.." && pwd)
dotnet_bin=${DOTNET_BIN:-$HOME/.dotnet/dotnet}
publish_dir=${CAPTURE_PUBLISH_DIR:-/tmp/avam-web}
capture_port=${CAPTURE_PORT:-8765}
export CAPTURE_URL="http://127.0.0.1:$capture_port"
export CAPTURE_OUTPUT=${CAPTURE_OUTPUT:-/workspace/AvaMedia-sidebar-review/ai-progress/v2/impl}
log_dir=$(mktemp -d /tmp/avam-web-capture.XXXXXX)
echo "Logs: $log_dir"
server_pid=''
cleanup() { if [[ -n "$server_pid" ]]; then kill "$server_pid" 2>/dev/null || true; wait "$server_pid" 2>/dev/null || true; fi; }
trap cleanup EXIT
# Fail early if the requested destination is read-only.
mkdir -p -- "$CAPTURE_OUTPUT"
if [[ ${CAPTURE_SKIP_PUBLISH:-0} != 1 ]]; then
  publish_args=()
  if [[ ${CAPTURE_NO_RESTORE:-0} == 1 ]]; then publish_args+=(--no-restore); fi
  "$dotnet_bin" publish "$repo_dir/src/AvaMedia.Browser" -c Release -p:EnableWindowsTargeting=true \
    -m:1 -p:NuGetAudit=false -o "$publish_dir" "${publish_args[@]}" > "$log_dir/publish.log" 2>&1 || { cat "$log_dir/publish.log"; exit 1; }
fi
python3 "$capture_dir/mock_server.py" --root "$publish_dir/wwwroot" --port "$capture_port" > "$log_dir/server.log" 2>&1 &
server_pid=$!
if ! python3 - "$server_pid" "$CAPTURE_URL" <<'PY'
import json, os, sys, time, urllib.request
pid, url = int(sys.argv[1]), sys.argv[2]
for attempt in range(100):
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        raise SystemExit('Mock server exited before startup; see server.log.')
    try:
        with urllib.request.urlopen(url + '/__capture/health', timeout=1) as response:
            if json.load(response)['ready']:
                break
    except OSError:
        time.sleep(.1)
else:
    raise SystemExit('Timed out starting capture server.')
PY
then
  cat "$log_dir/server.log"
  exit 1
fi
node "$capture_dir/capture.cjs" > "$log_dir/capture.log" 2>&1 || { cat "$log_dir/capture.log"; exit 1; }
cat "$log_dir/capture.log"
echo "Logs: $log_dir"
