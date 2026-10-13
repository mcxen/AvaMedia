#!/bin/sh
set -eu
kind=$1
package=$2
target=$3
stage=$4
backup=$5
error=$6
version=$7
size=$8
sha256=$9
install_lock=${10}
work=$(/usr/bin/dirname "$package")
log="$work/install.log"
exec </dev/null >>"$log" 2>&1
guardian_lock="$work/guardian.lock"
guardian_owned=0
install_owned=0
moved=0
installed=0
log() { printf '%s %s\n' "$(/bin/date -u '+%Y-%m-%dT%H:%M:%SZ')" "$*"; }
release_lock() {
    if [ "$(/bin/cat "$1/pid" 2>/dev/null || :)" = "$$" ]; then
        /bin/rm -f "$1/pid" "$1/started"
        /bin/rmdir "$1" 2>/dev/null || :
    fi
}
acquire_lock() {
    if ! /bin/mkdir "$1" 2>/dev/null; then
        owner=$(/bin/cat "$1/pid" 2>/dev/null || :)
        original=$(/bin/cat "$1/started" 2>/dev/null || :)
        current=$(/bin/ps -p "${owner:-0}" -o lstart= 2>/dev/null || :)
        if [ -n "$original" ] && [ "$current" = "$original" ]; then return 1; fi
        /bin/rm -f "$1/pid" "$1/started"
        /bin/rmdir "$1" 2>/dev/null || return 1
        /bin/mkdir "$1" 2>/dev/null || return 1
    fi
    printf '%s\n' "$$" > "$1/pid"
    /bin/ps -p "$$" -o lstart= > "$1/started"
}
finish() {
    status=$?
    trap - EXIT
    if [ "$status" -ne 0 ]; then
        if [ "$moved" -eq 1 ] && [ "$installed" -eq 0 ] && [ -d "$backup" ]; then
            if [ -e "$target" ] && [ ! -e "$stage" ]; then /bin/mv "$target" "$stage" || :; fi
            if [ ! -e "$target" ]; then /bin/mv "$backup" "$target" || :; fi
        fi
        log "Update failed (exit $status); prepared files retained."
        printf '更新安装失败，待安装文件已保留。详见日志：%s\n' "$log" > "$error"
    fi
    if [ "$install_owned" -eq 1 ]; then release_lock "$install_lock"; fi
    if [ "$guardian_owned" -eq 1 ]; then release_lock "$guardian_lock"; fi
}
trap finish EXIT
cancelled() { [ -f "$work/cancel" ]; }
instances_running() {
    # Include the managed assembly, for launches through dotnet inside an application bundle.
    instances=$(/usr/sbin/lsof -t "$target/Contents/MacOS/AvaMedia.Desktop" "$target/Contents/MacOS/AvaMedia.Desktop.dll" 2>/dev/null || :)
    [ -n "$instances" ]
}
wait_for_instances() {
    while instances_running; do
        if cancelled; then log 'Update cancelled.'; exit 0; fi
        /bin/sleep 1
    done
}
complete() {
    printf '%s\n' "$version" > "$work/installed-version.tmp"
    /bin/mv "$work/installed-version.tmp" "$work/installed-version"
    /bin/rm -f "$error" "$package" "$work/pending.json" "$work/helper.json" "$work/ready" "$work/armed"
    log "Update $version complete."
    if [ -f "$work/restart" ]; then
        /bin/rm -f "$work/restart"
        # Release our locks before the relaunched app restores its update state.
        release_lock "$install_lock"
        install_owned=0
        release_lock "$guardian_lock"
        guardian_owned=0
        /usr/bin/open "$target" || log 'Update installed, but application relaunch failed.'
    fi
}
[ "$kind" = mac ] && [ -f "$package" ] && [ -d "$stage/Contents" ] && [ -d "$target/Contents" ] || exit 1
if ! acquire_lock "$guardian_lock"; then exit 0; fi
guardian_owned=1
printf '%s\n' "$$" > "$work/ready"
log "Update $version guardian ready; waiting for application exit."
# The application publishes durable preparation before arming this helper.
count=0
while [ ! -f "$work/armed" ]; do
    if cancelled; then exit 0; fi
    count=$((count + 1))
    [ "$count" -lt 30 ] || exit 1
    /bin/sleep 1
done
wait_for_instances
while ! acquire_lock "$install_lock"; do
    if cancelled; then exit 0; fi
    /bin/sleep 1
done
install_owned=1
wait_for_instances
if cancelled; then exit 0; fi
current_version=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$target/Contents/Info.plist")
if /usr/bin/awk -v current="$current_version" -v update="$version" 'BEGIN {
    if (current !~ /^[0-9]+\.[0-9]+\.[0-9]+$/) exit 1;
    split(current, a, "."); split(update, b, ".");
    for (i=1;i<=3;i++) { if (a[i]+0>b[i]+0) exit 0; if (a[i]+0<b[i]+0) exit 1; }
    exit 0;
}'; then
    log "Version $current_version already installed; skipping replacement."
    /bin/rm -rf "$stage"
    complete
    exit 0
fi
[ "$(/usr/bin/wc -c < "$package")" -eq "$size" ]
actual_hash=$(/usr/bin/shasum -a 256 "$package" | /usr/bin/awk '{print $1}')
[ "$actual_hash" = "$sha256" ]
/usr/bin/codesign --verify --deep --strict "$stage"
[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$stage/Contents/Info.plist")" = "$version" ]
[ ! -e "$backup" ]
if cancelled; then exit 0; fi
log "Installing $version into $target."
/bin/mv "$target" "$backup"
moved=1
/bin/mv "$stage" "$target"
/usr/bin/codesign --verify --deep --strict "$target"
installed=1
/bin/rm -rf "$backup" || :
complete
