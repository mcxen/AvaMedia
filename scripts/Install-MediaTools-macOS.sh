#!/bin/bash
set -euo pipefail
if [[ "$(uname -s)" != Darwin ]]; then echo '此脚本只用于 macOS。' >&2; exit 1; fi
brew_path=$(command -v brew || true)
if [[ -z "$brew_path" ]]; then
  for candidate in /opt/homebrew/bin/brew /usr/local/bin/brew; do
    if [[ -x "$candidate" ]]; then brew_path="$candidate"; break; fi
  done
fi
if [[ -z "$brew_path" ]]; then echo '请先按 https://brew.sh 的说明安装 Homebrew，然后重新运行。' >&2; exit 1; fi
"$brew_path" install ffmpeg yt-dlp
tool_root="$HOME/Library/Application Support/AvaMedia/tools"
mkdir -p "$tool_root"
for tool_name in ffmpeg ffprobe yt-dlp; do
  prefix=$("$brew_path" --prefix "$([[ "$tool_name" == yt-dlp ]] && echo yt-dlp || echo ffmpeg)")
  ln -sfn "$prefix/bin/$tool_name" "$tool_root/$tool_name"
done
"$tool_root/ffmpeg" -version > "$tool_root/ffmpeg-build.txt"
printf '已配置 %s (%s) 的外部媒体工具。\n' "$(uname -s)" "$(uname -m)"
