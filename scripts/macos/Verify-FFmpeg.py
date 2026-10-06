#!/usr/bin/env python3
"""Verify the relocated native runtime, required capabilities and real media output."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import tempfile

ENCODERS = "mpeg4 mpeg2video flv wmv2 libx264 libx265 libvpx-vp9 libaom-av1 libwebp png mjpeg bmp tiff gif libmp3lame libvorbis libopus aac alac flac ac3 mp2 wmav2 pcm_s16le pcm_s24le pcm_f32le pcm_s16be pcm_s24be h264_videotoolbox hevc_videotoolbox".split()
FILTERS = "scale pad crop transpose hflip vflip trim setpts fade split gblur overlay xstack subtitles ass drawtext fps palettegen paletteuse setsar concat tile amix aformat aresample anullsrc anull atrim asetpts areverse afftdn atempo volume aecho afade testsrc2 sine".split()
MUXERS = "mp4 mov matroska webm avi asf mpeg flv mpegts gif image2 image2pipe avif ico mp3 flac wav ipod ogg adts ac3 opus aiff null rawvideo".split()
LEGACY = json.loads((Path(__file__).resolve().parents[1] / "legacy-video-capabilities.json").read_text(encoding="utf-8"))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("runtime", type=Path)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    root = args.runtime.resolve()
    ffmpeg, ffprobe = root / "ffmpeg", root / "ffprobe"
    checks, outputs = [], []

    def check(condition, message):
        if not condition:
            raise RuntimeError(message)
        checks.append(message)

    def invoke(program, *arguments, binary=False):
        result = subprocess.run([str(program), *map(str, arguments)], capture_output=True)
        if result.returncode:
            raise RuntimeError(result.stderr.decode(errors="replace")[-8000:])
        return result.stdout if binary else result.stdout.decode(errors="replace")

    def ff(*arguments, binary=False):
        return invoke(ffmpeg, "-hide_banner", "-v", "error", "-nostdin", "-y", *arguments, binary=binary)

    def listing(option):
        text = invoke(ffmpeg, "-hide_banner", option)
        return {name for line in text.splitlines() if len(fields := line.split()) > 1
                and re.fullmatch(r"[A-Z.]+", fields[0]) for name in fields[1].split(",")}

    manifest = json.loads((root / "build.json").read_text())
    for item in manifest["files"]:
        path = root / item["name"]
        check(hashlib.sha256(path.read_bytes()).hexdigest() == item["sha256"], "Recorded binary hash: " + item["name"])
        check(invoke("lipo", "-archs", path).strip() == "arm64", "ARM64 binary: " + item["name"])
        invoke("codesign", "--verify", "--strict", path)
        for line in invoke("otool", "-L", path).splitlines()[1:]:
            dependency = line.strip().split(" (", 1)[0]
            if dependency.startswith("@loader_path/"):
                check((path.parent / dependency.removeprefix("@loader_path/")).is_file(), "Relocated dependency: " + dependency)
            else:
                check(dependency.startswith(("/usr/lib/", "/System/Library/")) or
                      (path.suffix == ".dylib" and dependency == "@rpath/" + path.name), "System dependency: " + dependency)
    check(not list(root.rglob("*.a")) and not (root / "ffplay").exists(), "Runtime excludes static archives and ffplay")
    build = invoke(ffmpeg, "-version")
    check("--enable-gpl" in build and "--enable-version3" in build and "--enable-nonfree" not in build,
          "Declared GPL runtime without nonfree components")
    capabilities = {}
    for name, required in (("encoders", ENCODERS), ("filters", FILTERS), ("muxers", MUXERS),
                           ("decoders", []), ("demuxers", [])):
        capabilities[name] = sorted(listing("-" + name))
        check(not (missing := (set(required) | set(LEGACY.get(name, []))) - set(capabilities[name])),
              name + " covers client: " + ", ".join(sorted(missing)))
    capabilities["devices"] = sorted(listing("-devices"))
    check("avfoundation" not in capabilities["devices"], "Unused AVFoundation capture device is excluded")
    protocols = set(invoke(ffmpeg, "-hide_banner", "-protocols").split())
    check({"file", "pipe", "http", "https", "tcp", "udp"} <= protocols, "Local and network protocols are present")
    with tempfile.TemporaryDirectory(prefix="avamedia-ffmpeg-verify-") as temporary:
        work = Path(temporary)
        source = work / "source.mp4"
        ff("-f", "lavfi", "-i", "testsrc2=size=128x96:rate=25", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100",
           "-t", "1", "-c:v", "mpeg4", "-c:a", "aac", source)

        def output(path, *arguments):
            ff("-i", source, *arguments, path)
            info = json.loads(invoke(ffprobe, "-v", "error", "-show_streams", "-of", "json", path))
            check(path.stat().st_size > 0 and len(info["streams"]) > 0, "Probe real output: " + path.name)
            ff("-i", path, "-f", "null", "-")
            check(True, "Decode real output: " + path.name)
            outputs.append({"name": path.name, "bytes": path.stat().st_size,
                            "codecs": [stream["codec_name"] for stream in info["streams"]]})

        for codec, extension, options in (
            ("libx264", "mp4", ["-preset", "ultrafast"]),
            ("libx265", "mp4", ["-preset", "ultrafast"]),
            ("libvpx-vp9", "webm", ["-deadline", "realtime", "-cpu-used", "8"]),
            ("libaom-av1", "mkv", ["-cpu-used", "8", "-crf", "35"]),
            ("mpeg4", "avi", []), ("mpeg2video", "mpg", []),
            ("wmv2", "wmv", []), ("flv", "flv", [])):
            output(work / (codec + "." + extension), "-an", "-c:v", codec, *options)
        for extension, codec in (("mp3", "libmp3lame"), ("flac", "flac"), ("wav", "pcm_s16le"),
                                 ("m4a", "aac"), ("ogg", "libvorbis"), ("opus", "libopus"),
                                 ("aac", "aac"), ("ac3", "ac3"), ("wma", "wmav2"), ("aiff", "pcm_s16be")):
            output(work / ("audio." + extension), "-vn", "-c:a", codec)
        for extension, codec in (("png", "png"), ("jpg", "mjpeg"), ("bmp", "bmp"),
                                 ("tiff", "tiff"), ("webp", "libwebp"), ("ico", "png")):
            pixel_format = ["-pix_fmt", "rgba"] if extension == "ico" else []
            output(work / ("image." + extension), "-an", "-frames:v", "1", "-vf", "scale=64:64", "-c:v", codec, *pixel_format)
        output(work / "image.avif", "-an", "-frames:v", "1", "-c:v", "libaom-av1", "-still-picture", "1", "-cpu-used", "8")
        output(work / "animation.gif", "-an", "-filter_complex", "split[a][b];[a]palettegen[p];[b][p]paletteuse")
        output(work / "edits.mp4", "-vf", "crop=100:80:2:2,transpose=1,hflip,scale=64:80,fade=t=in:d=0.2,setpts=PTS/1.2",
               "-af", "afftdn,atempo=1.2,volume=0.5,afade=t=out:st=0.5:d=0.2", "-c:v", "mpeg4", "-c:a", "aac")
        subtitle = work / "subtitle.srt"
        subtitle.write_text("1\n00:00:00,000 --> 00:00:01,000\nAvaMedia 中文字幕\n", encoding="utf-8")
        def filter_path(path):
            return str(path).replace("\\", "\\\\").replace(":", "\\:").replace("'", "\\'")
        plain = ff("-i", source, "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1", binary=True)
        rendered = ff("-i", source, "-vf", "subtitles='" + filter_path(subtitle) + "'", "-frames:v", "1",
                      "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1", binary=True)
        check(len(rendered) == len(plain) and rendered != plain, "Subtitles change decoded pixels with CoreText fonts")
        font = next(path for path in (Path("/System/Library/Fonts/Supplemental/Arial.ttf"),
                                     Path("/System/Library/Fonts/Menlo.ttc")) if path.is_file())
        rendered = ff("-i", source, "-vf", "drawtext=fontfile='" + filter_path(font) + "':text='00.001':fontsize=16",
                      "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1", binary=True)
        check(len(rendered) == len(plain) and rendered != plain, "Contact-sheet text renders without Fontconfig")
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps({"status": "passed", "checks": len(checks), "results": checks,
                                      "capabilities": capabilities, "outputs": outputs}, indent=2) + "\n", encoding="utf-8")
    print(f"PASS {len(checks)} native checks; {len(outputs)} real outputs")


if __name__ == "__main__":
    main()
