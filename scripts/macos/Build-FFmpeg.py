#!/usr/bin/env python3
"""Build AvaMedia's ARM64 FFmpeg and its exact corresponding source package."""
import argparse
import concurrent.futures
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile

HERE = Path(__file__).resolve().parent
LOCK = HERE / "ffmpeg-sources.lock.json"


def sha256(path):
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def run(arguments, cwd=None, env=None, log=None):
    arguments = [str(value) for value in arguments]
    result = subprocess.run(arguments, cwd=cwd, env=env, text=True,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    if log:
        with Path(log).open("a", encoding="utf-8") as stream:
            stream.write(json.dumps(arguments) + "\n" + result.stdout + "\n")
    if result.returncode:
        raise RuntimeError(f"Command failed: {arguments}\n" + "\n".join(result.stdout.splitlines()[-30:]))
    return result.stdout.strip()


def fetch_source(source, root):
    if source["kind"] == "git":
        destination = root / "git" / (source["name"] + "-" + source["revision"])
        if not destination.exists():
            destination.mkdir(parents=True)
            run(["git", "init", destination])
            run(["git", "-C", destination, "remote", "add", "origin", source["url"]])
        try:
            revision = run(["git", "-C", destination, "rev-parse", "HEAD"])
        except RuntimeError:
            run(["git", "-C", destination, "fetch", "--depth=1", "--no-tags", "origin", source["revision"]])
            run(["git", "-C", destination, "checkout", "--detach", "FETCH_HEAD"])
            revision = run(["git", "-C", destination, "rev-parse", "HEAD"])
        if revision != source["revision"]:
            raise RuntimeError(f"Incorrect {source['name']} source revision")
        if run(["git", "-C", destination, "status", "--porcelain"]):
            raise RuntimeError(f"Modified {source['name']} source cache")
        if re.search(r"^160000 ", run(["git", "-C", destination, "ls-tree", "-r", "HEAD"]), re.M):
            raise RuntimeError(f"Uncaptured submodule in {source['name']}")
    else:
        suffix = ".tar.xz" if source["url"].endswith(".tar.xz") else ".tar.gz"
        destination = root / "archives" / (source["name"] + "-" + source["version"] + suffix)
        destination.parent.mkdir(parents=True, exist_ok=True)
        if not destination.exists():
            temporary = destination.with_suffix(destination.suffix + ".part")
            urls = [source["url"], *source.get("mirrors", [])]
            for index, url in enumerate(urls):
                try:
                    run(["curl", "--fail", "--location", "--connect-timeout", "20", "--max-time", "180",
                         "--retry", "2", "--retry-all-errors", "--retry-max-time", "90", "--proto", "=https",
                         "--proto-redir", "=https", "--output", temporary, url])
                except RuntimeError:
                    temporary.unlink(missing_ok=True)
                    if index == len(urls) - 1:
                        raise
                    print(f"Source unavailable: {url}; trying the pinned mirror", flush=True)
                    continue
                if sha256(temporary) != source["sha256"]:
                    temporary.unlink()
                    raise RuntimeError(f"Incorrect {source['name']} source checksum from {url}")
                temporary.replace(destination)
                break
        if sha256(destination) != source["sha256"]:
            raise RuntimeError(f"Incorrect cached {source['name']} source checksum")
    print(f"Verified source: {source['name']} {source['version']}", flush=True)
    return destination


def unpack(source, cached, destination):
    destination.mkdir(parents=True)
    if source["kind"] == "git":
        shutil.copytree(cached, destination, dirs_exist_ok=True)
    else:
        with tarfile.open(cached) as archive:
            archive.extractall(destination, filter="data")
        roots = list(destination.iterdir())
        if len(roots) != 1 or not roots[0].is_dir():
            raise RuntimeError(f"Unexpected source layout: {source['name']}")
        return roots[0]
    return destination


def build_dependency(name, source, prefix, work, env, jobs, log):
    def invoke(*arguments, cwd=source):
        return run(arguments, cwd=cwd, env=env, log=log)

    def autotools(*options):
        if not (source / "configure").exists():
            invoke("autoreconf", "-fi")
        invoke("./configure", f"--prefix={prefix}", "--disable-shared", "--enable-static", *options)
        invoke("make", f"-j{jobs}")
        invoke("make", "install")

    def cmake(*options, source_directory=source):
        build = work / (name + "-cmake")
        invoke("cmake", "-S", source_directory, "-B", build, "-G", "Ninja",
               f"-DCMAKE_INSTALL_PREFIX={prefix}", f"-DCMAKE_PREFIX_PATH={prefix}",
               "-DCMAKE_BUILD_TYPE=Release", "-DCMAKE_OSX_ARCHITECTURES=arm64",
               f"-DCMAKE_OSX_DEPLOYMENT_TARGET={env['MACOSX_DEPLOYMENT_TARGET']}",
               "-DCMAKE_POSITION_INDEPENDENT_CODE=ON", "-DCMAKE_POLICY_VERSION_MINIMUM=3.5",
               "-DCMAKE_C_FLAGS_RELEASE=-Os -DNDEBUG", "-DCMAKE_CXX_FLAGS_RELEASE=-Os -DNDEBUG",
               "-DBUILD_SHARED_LIBS=OFF", *options)
        invoke("cmake", "--build", build, "--parallel", jobs)
        invoke("cmake", "--install", build)

    if name == "freetype":
        autotools("--without-harfbuzz", "--without-bzip2", "--without-png", "--without-brotli", "--with-zlib")
    elif name == "harfbuzz":
        build = work / "harfbuzz-meson"
        invoke("meson", "setup", build, source, f"--prefix={prefix}", "--libdir=lib",
               "--default-library=static", "--buildtype=minsize", "-Db_staticpic=true",
               "-Dcoretext=enabled", "-Dfreetype=enabled", "-Dglib=disabled", "-Dgobject=disabled",
               "-Dcairo=disabled", "-Dicu=disabled", "-Dgraphite2=disabled", "-Dintrospection=disabled",
               "-Dtests=disabled", "-Ddocs=disabled", "-Dutilities=disabled", "-Dchafa=disabled",
               "-Dpng=disabled", "-Dzlib=disabled", "-Dsubset=disabled", "-Draster=disabled",
               "-Dvector=disabled", "-Dgpu=disabled", "-Dgpu_demo=disabled")
        invoke("meson", "compile", "-C", build, "-j", jobs)
        invoke("meson", "install", "-C", build)
    elif name == "libass":
        autotools("--disable-fontconfig", "--enable-coretext", "--enable-libunibreak")
    elif name == "lame":
        autotools("--disable-frontend")
    elif name == "opus":
        autotools("--disable-extra-programs", "--disable-doc")
    elif name == "zimg":
        autotools("--disable-testapp", "--disable-example", "--disable-unit-test", "STL_LIBS=-lc++")
    elif name in {"fribidi", "libunibreak", "libogg", "libvorbis"}:
        autotools()
    elif name == "libvpx":
        invoke("./configure", f"--prefix={prefix}", "--target=arm64-darwin20-gcc",
               "--enable-static", "--disable-shared", "--enable-pic", "--disable-examples",
               "--disable-tools", "--disable-docs", "--disable-unit-tests")
        invoke("make", f"-j{jobs}")
        invoke("make", "install")
    elif name == "aom":
        cmake("-DENABLE_TESTS=OFF", "-DENABLE_DOCS=OFF", "-DENABLE_EXAMPLES=OFF",
              "-DENABLE_TOOLS=OFF", "-DCONFIG_TUNE_VMAF=0")
    elif name == "webp":
        cmake(*["-DWEBP_BUILD_" + component + "=OFF" for component in
                ("ANIM_UTILS", "CWEBP", "DWEBP", "GIF2WEBP", "IMG2WEBP", "VWEBP", "WEBPINFO", "WEBPMUX", "EXTRAS")])
    elif name == "x264":
        invoke("./configure", f"--prefix={prefix}", "--enable-static", "--enable-pic",
               "--disable-cli", "--disable-opencl")
        invoke("make", f"-j{jobs}")
        invoke("make", "install")
    elif name == "x265":
        cmake("-DENABLE_SHARED=OFF", "-DENABLE_CLI=OFF", "-DENABLE_LIBNUMA=OFF",
              "-DAARCH64_RUNTIME_CPU_DETECT=ON", source_directory=source / "source")
    else:
        raise RuntimeError(f"No build recipe for {name}")


def package_runtime(prefix, bundle, extracted, source_lock):
    bundle.mkdir()
    (bundle / "lib").mkdir()
    for name in ("ffmpeg", "ffprobe"):
        shutil.copy2(prefix / "bin" / name, bundle / name)
    for library in (prefix / "lib").glob("*.dylib"):
        if not library.is_symlink():
            install_name = run(["otool", "-D", library]).splitlines()[-1].strip()
            shutil.copy2(library, bundle / "lib" / Path(install_name).name)
    binaries = [bundle / "ffmpeg", bundle / "ffprobe", *sorted((bundle / "lib").glob("*.dylib"))]
    for binary in binaries:
        if run(["lipo", "-archs", binary]) != "arm64":
            raise RuntimeError(f"Unexpected architecture: {binary}")
        dependencies = run(["otool", "-L", binary]).splitlines()[1:]
        for line in dependencies:
            dependency = line.strip().split(" (", 1)[0]
            library = bundle / "lib" / Path(dependency).name
            if library.exists():
                relative = "@loader_path/" + ("lib/" if binary.parent == bundle else "") + library.name
                run(["install_name_tool", "-change", dependency, relative, binary])
            elif not dependency.startswith(("/usr/lib/", "/System/Library/")):
                raise RuntimeError(f"Unbundled external dependency: {dependency}")
        if binary.suffix == ".dylib":
            run(["install_name_tool", "-id", "@rpath/" + binary.name, binary])
        run(["strip", "-x", binary])
        run(["codesign", "--force", "--sign", "-", "--timestamp=none", binary])
        run(["codesign", "--verify", "--strict", binary])
    notices = bundle / "licenses"
    for name, root in extracted.items():
        for path in root.rglob("*"):
            if path.is_file() and not path.is_symlink() and ".git" not in path.parts and re.match(
                    r"^(LICENSE|LICENCE|COPYING|COPYRIGHT|NOTICE|PATENTS|FTL\.TXT)([._-].*)?$", path.name, re.I):
                target = notices / name / path.relative_to(root)
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(path, target)
    (bundle / "NOTICE.txt").write_text(
        "AvaMedia's external FFmpeg runtime: GPL-3.0-or-later.\n"
        "Includes x264/x265 GPL components. No nonfree components.\n"
        "Exact corresponding sources, dependency notices and rebuild recipes are supplied "
        "in the matching AvaMedia-FFmpeg source archive.\n", encoding="utf-8")
    shutil.copy2(LOCK, bundle / LOCK.name)
    (bundle / "ffmpeg-build.txt").write_text(run([bundle / "ffmpeg", "-version"]) + "\n", encoding="utf-8")
    manifest = {"schemaVersion": 1, "ffmpegVersion": source_lock["ffmpegVersion"],
                "architecture": "arm64", "license": source_lock["runtimeLicense"],
                "deploymentTarget": source_lock["deploymentTarget"],
                "runtimeBytes": sum(path.stat().st_size for path in binaries),
                "recipeSha256": sha256(__file__), "sourceLockSha256": sha256(LOCK),
                "clang": run(["clang", "--version"]), "sdk": run(["xcrun", "--show-sdk-version"]), "python": sys.version,
                "buildTools": {tool: run([tool, "--version"]) for tool in ("cmake", "meson", "ninja", "pkg-config", "autoconf", "automake", "make")},
                "files": [{"name": str(path.relative_to(bundle)), "bytes": path.stat().st_size,
                           "sha256": sha256(path)} for path in binaries]}
    (bundle / "build.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=HERE.parent.parent / "artifacts")
    parser.add_argument("--source-root", type=Path, default=HERE.parent.parent / ".tools/mac-ffmpeg-sources")
    parser.add_argument("--fetch-only", action="store_true", help="Verify pinned sources without compiling")
    args = parser.parse_args()
    source_lock = json.loads(LOCK.read_text(encoding="utf-8"))
    if not args.fetch_only and (sys.platform != "darwin" or platform.machine() != "arm64"):
        parser.error("Native macOS ARM64 is required; use --fetch-only to audit sources elsewhere")
    source_root = args.source_root.resolve()
    source_root.mkdir(parents=True, exist_ok=True)
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as executor:
        cached = dict(zip((source["name"] for source in source_lock["sources"]),
                          executor.map(lambda source: fetch_source(source, source_root), source_lock["sources"])))
    if args.fetch_only:
        return
    for tool in ("clang", "cmake", "ninja", "meson", "pkg-config", "autoreconf", "automake", "make", "lipo", "codesign"):
        if not shutil.which(tool):
            parser.error(f"Missing build tool: {tool}")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix="ffmpeg-build-", dir=output))
    prefix = work / "dependencies"
    prefix.mkdir()
    env = os.environ.copy()
    env.update(CC="clang", CXX="clang++", MACOSX_DEPLOYMENT_TARGET=source_lock["deploymentTarget"],
               CFLAGS="-Os -fPIC", CXXFLAGS="-Os -fPIC", CPPFLAGS=f"-I{prefix}/include",
               LDFLAGS=f"-L{prefix}/lib", PKG_CONFIG_LIBDIR=f"{prefix}/lib/pkgconfig:{prefix}/share/pkgconfig",
               PKG_CONFIG_PATH="", SOURCE_DATE_EPOCH="0")
    jobs = str(os.cpu_count() or 2)
    extracted = {}
    for source in source_lock["sources"]:
        name = source["name"]
        extracted[name] = unpack(source, cached[name], work / "src" / name)
        if name != "ffmpeg":
            print(f"Building {name} {source['version']}", flush=True)
            build_dependency(name, extracted[name], prefix, work, env, jobs, work / (name + ".log"))
    ffmpeg_prefix = work / "ffmpeg"
    options = [f"--prefix={ffmpeg_prefix}", "--arch=arm64", "--target-os=darwin", "--cc=clang", "--cxx=clang++", "--extra-version=AvaMedia-arm64",
               "--enable-shared", "--disable-static", "--enable-small", "--disable-debug", "--disable-doc",
               "--disable-ffplay", "--disable-autodetect", "--enable-gpl", "--enable-version3",
               "--enable-pthreads", "--enable-neon", "--enable-zlib", "--enable-bzlib", "--enable-iconv",
               "--enable-securetransport", "--enable-videotoolbox", "--enable-audiotoolbox", "--enable-avfoundation",
               "--enable-libass", "--enable-libfreetype", "--enable-libharfbuzz", "--enable-libfribidi",
               "--enable-libaom", "--enable-libvpx", "--enable-libwebp", "--enable-libmp3lame",
               "--enable-libopus", "--enable-libvorbis", "--enable-libx264", "--enable-libx265", "--enable-libzimg",
               "--pkg-config-flags=--static", "--install-name-dir=@rpath",
               f"--extra-cflags=-I{prefix}/include", f"--extra-ldflags=-L{prefix}/lib -Wl,-headerpad_max_install_names"]
    print("Building FFmpeg", flush=True)
    run(["./configure", *options], extracted["ffmpeg"], env, work / "ffmpeg.log")
    run(["make", "-j" + jobs], extracted["ffmpeg"], env, work / "ffmpeg.log")
    run(["make", "install"], extracted["ffmpeg"], env, work / "ffmpeg.log")
    base = "AvaMedia-FFmpeg-" + source_lock["ffmpegVersion"]
    bundle = work / (base + "-osx-arm64")
    package_runtime(ffmpeg_prefix, bundle, extracted, source_lock)
    run([sys.executable, HERE / "Verify-FFmpeg.py", bundle, "--report", bundle / "verification.json"], log=work / "verification.log")
    sources_package = work / (base + "-source")
    for path in cached.values():
        target = sources_package / "sources" / path.relative_to(source_root)
        target.parent.mkdir(parents=True, exist_ok=True)
        if path.is_dir():
            shutil.copytree(path, target)
        else:
            shutil.copy2(path, target)
    shutil.copytree(HERE, sources_package / "scripts/macos", ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
    shutil.copy2(HERE.parent.parent / "LICENSE", sources_package / "LICENSE")
    shutil.copy2(HERE.parent.parent / "COPYRIGHT", sources_package / "COPYRIGHT")
    (sources_package / "configure-options.json").write_text(json.dumps(options, indent=2) + "\n", encoding="utf-8")
    (sources_package / "README.txt").write_text(
        "Rebuild on macOS ARM64 with Xcode command-line tools and Python 3.12+:\n"
        "brew install cmake meson ninja pkgconf autoconf automake libtool\n"
        "python3 scripts/macos/Build-FFmpeg.py --source-root sources --output artifacts\n"
        "AvaMedia build scripts: AGPL-3.0-only, see LICENSE.\n"
        "FFmpeg and dependency sources retain their original licenses within each source archive/repository.\n"
        "sources/ contains verified original archives and exact Git revisions; no Homebrew runtime libraries are used.\n",
        encoding="utf-8")
    archives = []
    for folder in (bundle, sources_package):
        target = output / (folder.name + ".tar.gz")
        with tarfile.open(target, "w:gz") as archive:
            archive.add(folder, arcname=folder.name)
        archives.append(target)
    (output / (base + "-SHA256SUMS.txt")).write_text(
        "".join(sha256(path) + "  " + path.name + "\n" for path in archives), encoding="utf-8")
    print(f"Runtime: {json.loads((bundle / 'build.json').read_text())['runtimeBytes'] / 1048576:.2f} MiB")
    print(f"Runtime and corresponding source archives: {output}")


if __name__ == "__main__":
    main()
