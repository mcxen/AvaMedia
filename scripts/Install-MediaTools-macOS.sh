#!/bin/bash
set -euo pipefail
if [[ "$(uname -s)" != Darwin ]]; then echo '此脚本只用于 macOS。' >&2; exit 1; fi
if [[ "$(uname -m)" != arm64 && "$(sysctl -n hw.optional.arm64 2>/dev/null || true)" != 1 ]]; then
  echo '定制媒体工具仅支持 Apple Silicon（ARM64）。' >&2; exit 1
fi
tool_root="$HOME/Library/Application Support/AvaMedia/tools"
archive_path=''
checksums_path=''
install_ytdlp=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --archive) archive_path="$2"; shift 2 ;;
    --checksums) checksums_path="$2"; shift 2 ;;
    --destination) tool_root="$2"; shift 2 ;;
    --with-yt-dlp) install_ytdlp=true; shift ;;
    *) echo "未知参数：$1" >&2; exit 1 ;;
  esac
done
mkdir -p "$tool_root"
tool_root=$(cd "$tool_root" && pwd -P)
stage=$(mktemp -d "$tool_root/.avamedia-ffmpeg.XXXXXXXX")
cleanup() {
  case "$stage" in "$tool_root"/.avamedia-ffmpeg.*) rm -rf -- "$stage" ;; esac
}
trap cleanup EXIT
download() { curl --fail --location --retry 3 --proto '=https' --proto-redir '=https' --output "$2" "$1"; }
if [[ -z "$archive_path" ]]; then
  metadata="$stage/release.json"
  download 'https://api.github.com/repos/mcxen/AvaMedia/releases/latest' "$metadata"
  archive_name=''
  archive_url=''
  index=0
  while name=$(plutil -extract "assets.$index.name" raw -o - "$metadata" 2>/dev/null); do
    if [[ "$name" =~ ^AvaMedia-FFmpeg-[0-9]+\.[0-9]+\.[0-9]+-osx-arm64\.tar\.gz$ ]]; then
      archive_name="$name"
      archive_url=$(plutil -extract "assets.$index.browser_download_url" raw -o - "$metadata")
      break
    fi
    index=$((index + 1))
  done
  if [[ -z "$archive_url" ]]; then echo '此 Release 尚未提供定制 ARM64 FFmpeg。' >&2; exit 1; fi
  checksums_name="${archive_name%-osx-arm64.tar.gz}-SHA256SUMS.txt"
  checksums_url=''
  index=0
  while name=$(plutil -extract "assets.$index.name" raw -o - "$metadata" 2>/dev/null); do
    if [[ "$name" == "$checksums_name" ]]; then
      checksums_url=$(plutil -extract "assets.$index.browser_download_url" raw -o - "$metadata")
      break
    fi
    index=$((index + 1))
  done
  if [[ -z "$checksums_url" ]]; then echo 'Release 缺少媒体工具校验清单。' >&2; exit 1; fi
  archive_path="$stage/$archive_name"
  checksums_path="$stage/$checksums_name"
  download "$archive_url" "$archive_path"
  download "$checksums_url" "$checksums_path"
fi
if [[ -z "$checksums_path" ]]; then echo '本地安装需要同时提供 --checksums。' >&2; exit 1; fi
archive_name=$(basename "$archive_path")
if [[ ! "$archive_name" =~ ^AvaMedia-FFmpeg-[0-9]+\.[0-9]+\.[0-9]+-osx-arm64\.tar\.gz$ ]]; then
  echo '请选择 AvaMedia 定制 ARM64 FFmpeg 运行包。' >&2; exit 1
fi
expected=$(awk -v name="$archive_name" '$2 == name {print $1}' "$checksums_path")
actual=$(shasum -a 256 "$archive_path" | awk '{print $1}')
if [[ ! "$expected" =~ ^[0-9a-f]{64}$ || "$actual" != "$expected" ]]; then echo 'FFmpeg 运行包校验失败。' >&2; exit 1; fi
tar -xzf "$archive_path" -C "$stage"
runtime="$stage/${archive_name%.tar.gz}"
for tool in ffmpeg ffprobe; do
  [[ -x "$runtime/$tool" ]]
  file -b "$runtime/$tool" | grep -q 'Mach-O 64-bit executable arm64'
  codesign --verify --strict "$runtime/$tool"
done
"$runtime/ffmpeg" -version > "$stage/ffmpeg-build.txt"
"$runtime/ffprobe" -version > /dev/null
managed_root="$tool_root/ffmpeg-runtime"
installed="$managed_root/$actual"
mkdir -p "$managed_root"
if [[ -d "$installed" ]] && ! diff -qr "$runtime" "$installed" > /dev/null; then
  mv "$installed" "$stage/replaced-runtime"
fi
if [[ ! -d "$installed" ]]; then mv "$runtime" "$installed"; fi
for tool in ffmpeg ffprobe; do ln -sfn "ffmpeg-runtime/$actual/$tool" "$tool_root/$tool"; done
cp "$stage/ffmpeg-build.txt" "$tool_root/ffmpeg-build.txt"
if [[ "$install_ytdlp" == true ]]; then
  brew_path=$(command -v brew || true)
  if [[ -z "$brew_path" && -x /opt/homebrew/bin/brew ]]; then brew_path=/opt/homebrew/bin/brew; fi
  if [[ -z "$brew_path" ]]; then echo 'FFmpeg 已安装；安装 yt-dlp 需要先安装 Homebrew：https://brew.sh' >&2; exit 1; fi
  "$brew_path" install yt-dlp
  prefix=$("$brew_path" --prefix yt-dlp)
  ln -sfn "$prefix/bin/yt-dlp" "$tool_root/yt-dlp"
fi
printf '已安装定制 ARM64 FFmpeg / FFprobe：%s\n' "$tool_root"
