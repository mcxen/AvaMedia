#!/bin/sh
set -eu
parent_pid=$1
kind=$2
package=$3
target=$4
stage=$5
backup=$6
error=$7
moved=0
installed=0
fail() {
    status=$?
    if [ "$status" -ne 0 ]; then
        if [ "$moved" -eq 1 ] && [ "$installed" -eq 0 ] && [ ! -e "$target" ]; then
            /bin/mv "$backup" "$target" || true
        fi
        printf '%s\n' '更新安装失败，原版本已保留。请从发布页手动安装。' > "$error"
    fi
}
trap fail EXIT
# Wait for the application to finish saving and release all its files.
count=0
while kill -0 "$parent_pid" 2>/dev/null; do
    count=$((count + 1))
    [ "$count" -lt 300 ] || exit 1
    sleep 1
done
[ "$kind" = mac ] && [ -d "$stage/Contents" ] && [ -d "$target/Contents" ]
# Other instances must also have exited before the application is replaced.
if /usr/sbin/lsof -t "$target/Contents/MacOS/AvaMedia.Desktop" >/dev/null 2>&1; then exit 1; fi
/bin/mv "$target" "$backup"
moved=1
/bin/mv "$stage" "$target"
installed=1
/bin/rm -rf "$backup" || true
/bin/rm -f "$error" || true
/bin/rm -rf "$(/usr/bin/dirname "$package")" || true
