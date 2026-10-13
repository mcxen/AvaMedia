#!/usr/bin/env bash
# Package the Linux x64 release: self-contained .NET 8 publish of AvaMedia.Desktop as a .tar.gz.
# Usage: scripts/Package-Linux.sh <version> [--skip-browser --browser-licenses <directory>]
# FFmpeg / FFprobe are not bundled on Linux; see docs/LINUX.md.
set -euo pipefail
version="${1:?version required (MAJOR.MINOR.PATCH)}"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Invalid version: $version" >&2; exit 1; }
shift
skip_browser=false
browser_licenses=""
while (($#)); do
  case "$1" in
    --skip-browser) skip_browser=true; shift ;;
    --browser-licenses) browser_licenses="${2:?browser license directory required}"; shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 1 ;;
  esac
done
runtime=linux-x64
root="$(cd "$(dirname "$0")/.." && pwd)"
dotnet="${DOTNET:-dotnet}"
pwsh="${PWSH:-pwsh}"
name="AvaMedia-$version-$runtime"
stage="$root/artifacts/release/$version/$runtime-stage"
app="$stage/$name"
archive="$root/artifacts/$name.tar.gz"
rm -rf "$stage"; mkdir -p "$app" "$root/artifacts"
if [[ "$skip_browser" == false ]]; then
  # The WiFi transfer page is the WebAssembly browser app embedded as desktop content.
  "$dotnet" publish "$root/src/AvaMedia.Browser/AvaMedia.Browser.csproj" -c Release "-p:Version=$version" --verbosity minimal
  browser_licenses="$stage/browser-licenses"
  "$pwsh" -NoProfile -File "$root/scripts/Collect-Licenses.ps1" -Project src/AvaMedia.Browser/AvaMedia.Browser.csproj -Runtime browser-wasm -OutputDirectory "$browser_licenses"
fi
[[ -f "$root/src/AvaMedia.Browser/bin/Release/net8.0-browser/publish/wwwroot/index.html" && -n "$browser_licenses" && -f "$browser_licenses/manifest.json" ]] || {
  echo "Browser wwwroot and its generated licenses are required (download the shared browser artifact when using --skip-browser)." >&2; exit 1
}
"$dotnet" publish "$root/src/AvaMedia.Desktop/AvaMedia.Desktop.csproj" -c Release -r "$runtime" --self-contained true \
  -o "$app/lib" "-p:Version=$version" -p:DebugType=None -p:DebugSymbols=false --verbosity minimal
"$pwsh" -NoProfile -File "$root/scripts/Collect-Licenses.ps1" -Runtime "$runtime" -OutputDirectory "$app/licenses" -BrowserLicenseDirectory "$browser_licenses"
# Font content is represented once in the full application license bundle.
rm -rf "$app/lib/licenses"
"$pwsh" -NoProfile -File "$root/scripts/Bundle-UserDocuments.ps1" -Destination "$app"
cp "$root/src/AvaMedia.Desktop/Assets/AppIcon/v2/app-256.png" "$app/avamedia.png"
cat > "$app/avamedia" <<'SH'
#!/usr/bin/env sh
# Launch AvaMedia; FFmpeg / FFprobe come from PATH or AVAMEDIA_FFMPEG / AVAMEDIA_FFPROBE.
here="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
exec "$here/lib/AvaMedia.Desktop" "$@"
SH
cat > "$app/avamedia.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=AvaMedia
Name[zh_CN]=天池万象转换
Comment=Media conversion toolkit
Exec=avamedia %F
Icon=avamedia
Categories=AudioVideo;Video;Audio;
Terminal=false
DESKTOP
chmod +x "$app/avamedia" "$app/lib/AvaMedia.Desktop"
tar -C "$stage" -czf "$archive" "$name"
echo "$archive"
