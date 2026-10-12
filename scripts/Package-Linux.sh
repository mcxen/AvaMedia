#!/usr/bin/env bash
# Package the Linux x64 release: self-contained .NET 8 publish of AvaMedia.Desktop as a .tar.gz.
# Usage: scripts/Package-Linux.sh <version> [--skip-browser]
# FFmpeg / FFprobe are not bundled on Linux; see docs/LINUX.md.
set -euo pipefail
version="${1:?version required (MAJOR.MINOR.PATCH)}"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Invalid version: $version" >&2; exit 1; }
runtime=linux-x64
root="$(cd "$(dirname "$0")/.." && pwd)"
dotnet="${DOTNET:-dotnet}"
name="AvaMedia-$version-$runtime"
stage="$root/artifacts/release/$version/$runtime-stage"
app="$stage/$name"
archive="$root/artifacts/$name.tar.gz"
rm -rf "$stage"; mkdir -p "$app" "$root/artifacts"
if [[ "${2:-}" != "--skip-browser" ]]; then
  # The WiFi transfer page is the WebAssembly browser app embedded as desktop content.
  "$dotnet" publish "$root/src/AvaMedia.Browser/AvaMedia.Browser.csproj" -c Release "-p:Version=$version" --verbosity minimal
fi
"$dotnet" publish "$root/src/AvaMedia.Desktop/AvaMedia.Desktop.csproj" -c Release -r "$runtime" --self-contained true \
  -o "$app/lib" "-p:Version=$version" -p:DebugType=None -p:DebugSymbols=false --verbosity minimal
for file in Branding.props LICENSE COPYRIGHT README.md UISPEC.MD THIRD-PARTY-NOTICES.md; do cp "$root/$file" "$app/"; done
cp -R "$root/licenses" "$app/licenses"
mkdir -p "$app/docs"; cp "$root"/docs/*.md "$app/docs/"
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
