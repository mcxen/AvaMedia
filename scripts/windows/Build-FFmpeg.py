#!/usr/bin/env python3
"""Cross-build the bundled Windows media engine and its corresponding sources."""
import argparse
import concurrent.futures
import hashlib
import importlib.util
import inspect
import json
import os
from pathlib import Path
import re
import shutil
import sys
import tarfile
import tempfile
import zipfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
SHARED = HERE.parent / "macos"
spec = importlib.util.spec_from_file_location("ffmpeg_sources", SHARED / "Build-FFmpeg.py")
shared = importlib.util.module_from_spec(spec)
spec.loader.exec_module(shared)
CROSS = "x86_64-w64-mingw32-"


def dependency(name, source, prefix, work, env, jobs):
    def invoke(*arguments, cwd=source):
        return shared.run(arguments, cwd=cwd, env=env, log=work / (name + ".log"))

    def autotools(*options):
        if not (source / "configure").exists():
            invoke("autoreconf", "-fi")
        invoke("./configure", f"--prefix={prefix}", "--host=x86_64-w64-mingw32",
               "--disable-shared", "--enable-static", *options)
        invoke("make", f"-j{jobs}")
        invoke("make", "install")

    def cmake(*options, directory=source):
        build = work / (name + "-cmake")
        invoke("cmake", "-S", directory, "-B", build, "-G", "Ninja",
               "-DCMAKE_SYSTEM_NAME=Windows", f"-DCMAKE_C_COMPILER={CROSS}gcc",
               f"-DCMAKE_CXX_COMPILER={CROSS}g++", f"-DCMAKE_RC_COMPILER={CROSS}windres",
               "-DCMAKE_BUILD_TYPE=Release", f"-DCMAKE_INSTALL_PREFIX={prefix}",
               f"-DCMAKE_PREFIX_PATH={prefix}", "-DCMAKE_INSTALL_LIBDIR=lib",
               "-DCMAKE_POLICY_VERSION_MINIMUM=3.5", "-DCMAKE_POSITION_INDEPENDENT_CODE=ON",
               "-DCMAKE_C_FLAGS_RELEASE=-Os -DNDEBUG", "-DCMAKE_CXX_FLAGS_RELEASE=-Os -DNDEBUG",
               "-DBUILD_SHARED_LIBS=OFF", *options)
        invoke("cmake", "--build", build, "--parallel", jobs)
        invoke("cmake", "--install", build)

    if name == "zlib":
        invoke("./configure", f"--prefix={prefix}", "--static")
        invoke("make", f"-j{jobs}")
        invoke("make", "install")
    elif name == "bzip2":
        invoke("make", f"-j{jobs}", f"CC={env['CC']}", f"AR={env['AR']}",
               f"RANLIB={env['RANLIB']}", "CFLAGS=-Os", "libbz2.a")
        shutil.copy2(source / "libbz2.a", prefix / "lib")
        shutil.copy2(source / "bzlib.h", prefix / "include")
    elif name == "libiconv":
        autotools("--disable-nls")
    elif name == "nv-codec-headers":
        invoke("make", "install", f"PREFIX={prefix}")
    elif name == "AMF":
        shutil.copytree(source / "amf/public/include", prefix / "include/AMF")
    elif name == "libvpl":
        # Upstream's dispatcher MSVC version guard also needs to accept MinGW.
        header = source / "libvpl/src/windows/mfx_dispatcher_defs.h"
        header.write_text(header.read_text().replace("#if _MSC_VER < 1400",
                          "#if defined(_MSC_VER) && _MSC_VER < 1400"))
        cmake("-DBUILD_DISPATCHER=ON", "-DBUILD_DEV=ON", "-DBUILD_PREVIEW=OFF",
              "-DBUILD_TOOLS=OFF", "-DBUILD_TOOLS_ONEVPL_EXPERIMENTAL=OFF",
              "-DINSTALL_EXAMPLE_CODE=OFF", "-DBUILD_TESTS=OFF")
        with (prefix / "lib/pkgconfig/vpl.pc").open("a") as stream:
            stream.write("\nLibs.private: -lstdc++\n")
    elif name == "freetype":
        autotools("--without-harfbuzz", "--without-bzip2", "--without-png", "--without-brotli", "--with-zlib")
    elif name in {"harfbuzz", "fribidi"}:
        cross_file = work / "mingw.ini"
        cross_file.write_text("[binaries]\nc = '" + env["CC"] + "'\ncpp = '" + env["CXX"] +
                              "'\nar = '" + env["AR"] + "'\nstrip = '" + CROSS + "strip'\n"
                              "pkg-config = 'pkg-config'\n[host_machine]\nsystem = 'windows'\n"
                              "cpu_family = 'x86_64'\ncpu = 'x86_64'\nendian = 'little'\n")
        build = work / (name + "-meson")
        options = (["-Dbin=false", "-Dtests=false", "-Ddocs=false"] if name == "fribidi" else
                   ["-Dcoretext=disabled", "-Dfreetype=enabled", "-Dglib=disabled", "-Dgobject=disabled",
                    "-Dcairo=disabled", "-Dicu=disabled", "-Dgraphite2=disabled", "-Dintrospection=disabled",
                    "-Dtests=disabled", "-Ddocs=disabled", "-Dutilities=disabled", "-Dchafa=disabled",
                    "-Dpng=disabled", "-Dzlib=disabled", "-Dsubset=disabled", "-Draster=disabled",
                    "-Dvector=disabled", "-Dgpu=disabled", "-Dgpu_demo=disabled"])
        invoke("meson", "setup", build, source, "--cross-file", cross_file,
               f"--prefix={prefix}", "--libdir=lib", "--default-library=static", "--buildtype=minsize",
               "-Db_staticpic=true", *options)
        invoke("meson", "compile", "-C", build, "-j", jobs)
        invoke("meson", "install", "-C", build)
    elif name == "libass":
        autotools("--disable-fontconfig", "--enable-libunibreak")
    elif name == "lame":
        autotools("--disable-frontend", "--disable-decoder")
    elif name == "opus":
        autotools("--disable-extra-programs", "--disable-doc")
    elif name == "zimg":
        autotools("--disable-testapp", "--disable-example", "--disable-unit-test", "STL_LIBS=-lstdc++")
    elif name == "libvorbis":
        cmake(f"-DOGG_INCLUDE_DIR={prefix}/include", f"-DOGG_LIBRARY={prefix}/lib/libogg.a")
    elif name in {"libunibreak", "libogg"}:
        autotools()
    elif name == "libvpx":
        invoke("./configure", f"--prefix={prefix}", "--target=x86_64-win64-gcc", "--as=nasm",
               "--enable-static", "--disable-shared", "--disable-examples", "--disable-tools",
               "--disable-docs", "--disable-unit-tests")
        invoke("make", f"-j{jobs}")
        invoke("make", "install")
    elif name == "aom":
        cmake("-DENABLE_TESTS=OFF", "-DENABLE_DOCS=OFF", "-DENABLE_EXAMPLES=OFF",
              "-DENABLE_TOOLS=OFF", "-DCONFIG_TUNE_VMAF=0")
    elif name == "webp":
        cmake(*["-DWEBP_BUILD_" + component + "=OFF" for component in
                ("ANIM_UTILS", "CWEBP", "DWEBP", "GIF2WEBP", "IMG2WEBP", "VWEBP", "WEBPINFO", "WEBPMUX", "EXTRAS")])
    elif name == "x264":
        invoke("./configure", f"--prefix={prefix}", "--host=x86_64-w64-mingw32",
               f"--cross-prefix={CROSS}", "--enable-static", "--disable-cli", "--disable-opencl")
        invoke("make", f"-j{jobs}")
        invoke("make", "install")
    elif name == "x265":
        cmake("-DENABLE_SHARED=OFF", "-DENABLE_CLI=OFF", "-DENABLE_LIBNUMA=OFF", directory=source / "source")
    else:
        raise RuntimeError(f"No Windows recipe for {name}")


def dependency_environment(prefix):
    env = os.environ.copy()
    env.update(CC=CROSS + "gcc", CXX=CROSS + "g++", AR=CROSS + "ar", RANLIB=CROSS + "ranlib",
               CROSS=CROSS, CHOST="x86_64-w64-mingw32", CC_FOR_BUILD="gcc", CXX_FOR_BUILD="g++",
               CFLAGS="-Os", CXXFLAGS="-Os",
               CPPFLAGS=f"-I{prefix}/include", LDFLAGS=f"-L{prefix}/lib -static",
               PKG_CONFIG_LIBDIR=f"{prefix}/lib/pkgconfig:{prefix}/share/pkgconfig",
               PKG_CONFIG_PATH="", SOURCE_DATE_EPOCH="0")
    return env


def dependency_key(sources):
    # A configure/logging change in FFmpeg must not discard its compiled dependencies.
    tools = (CROSS + "gcc", CROSS + "g++", CROSS + "ld", "gcc", "cmake", "ninja",
             "meson", "pkg-config", "nasm", "make", "autoreconf", "automake", "libtoolize")
    inputs = {"schemaVersion": 1, "cross": CROSS,
              "sources": [item for item in sources if item["name"] != "ffmpeg"],
              "recipe": inspect.getsource(dependency),
              "environment": inspect.getsource(dependency_environment),
              "helpers": [inspect.getsource(getattr(shared, name)) for name in ("run", "fetch_source", "unpack")],
              "toolchain": {tool: shared.run([tool, "--version"]) for tool in tools}}
    return hashlib.sha256(json.dumps(inputs, sort_keys=True).encode()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts")
    parser.add_argument("--source-root", type=Path, default=ROOT / ".tools/ffmpeg-windows-sources")
    parser.add_argument("--dependency-root", type=Path, default=ROOT / ".tools/ffmpeg-windows-dependencies")
    parser.add_argument("--print-dependency-key", action="store_true", help="Print the exact dependency/toolchain cache key")
    parser.add_argument("--fetch-only", action="store_true", help="Verify pinned sources without compiling")
    parser.add_argument("--dependencies-only", action="store_true", help="Prepare dependencies before building FFmpeg")
    args = parser.parse_args()
    if sys.platform != "linux":
        parser.error("Build on Linux with the MinGW-w64 POSIX toolchain")
    source_lock = json.loads((SHARED / "ffmpeg-sources.lock.json").read_text())
    windows_lock = json.loads((HERE / "ffmpeg-sources.lock.json").read_text())
    sources = windows_lock["sources"] + source_lock["sources"]
    key = dependency_key(sources)
    if args.print_dependency_key:
        print(key)
        return
    args.output.mkdir(parents=True, exist_ok=True)
    source_root = args.source_root.resolve()
    source_root.mkdir(parents=True, exist_ok=True)
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as executor:
        cached = dict(zip((item["name"] for item in sources),
                          executor.map(lambda item: shared.fetch_source(item, source_root), sources)))
    if args.fetch_only:
        return
    work = Path(tempfile.mkdtemp(prefix="ffmpeg-win-build-", dir=args.output.resolve()))
    prefix = args.dependency_root.resolve() / key
    dependency_manifest = prefix / "build.json"
    identity = {"key": key, "prefix": str(prefix)}
    reuse_dependencies = dependency_manifest.is_file()
    if reuse_dependencies and json.loads(dependency_manifest.read_text()) != identity:
        raise RuntimeError("Windows dependency cache identity or install path mismatch")
    if not reuse_dependencies and prefix.exists():
        # Incomplete builds are never reused or saved as a successful cache.
        shutil.rmtree(prefix)
    for folder in ("include", "lib"):
        (prefix / folder).mkdir(parents=True, exist_ok=True)
    env = dependency_environment(prefix)
    jobs = str(os.cpu_count() or 2)
    if reuse_dependencies:
        print(f"Reusing Windows dependencies: {key}", flush=True)
        if args.dependencies_only:
            return
    extracted = {}
    for item in sources:
        name = item["name"]
        extracted[name] = shared.unpack(item, cached[name], work / "src" / name)
        if name != "ffmpeg" and not reuse_dependencies:
            print(f"Building Windows {name} {item['version']}", flush=True)
            dependency(name, extracted[name], prefix, work, env, jobs)
    if not reuse_dependencies:
        dependency_manifest.write_text(json.dumps(identity, indent=2) + "\n")
    if args.dependencies_only:
        print(f"Windows dependencies ready: {key}", flush=True)
        return
    install = work / "ffmpeg"
    # FFmpeg otherwise applies CROSS to pkg-config too. Use the host executable
    # with PKG_CONFIG_LIBDIR above confined to our Windows dependencies.
    options = [f"--prefix={install}", "--arch=x86_64", "--target-os=mingw32", "--enable-cross-compile",
               f"--cross-prefix={CROSS}", "--enable-shared", "--disable-static", "--enable-small",
               "--disable-debug", "--disable-doc", "--disable-ffplay", "--disable-autodetect",
               "--enable-gpl", "--enable-version3", "--enable-pthreads", "--disable-w32threads",
               "--enable-zlib", "--enable-bzlib", "--enable-iconv", "--enable-schannel",
               "--enable-d3d11va", "--enable-dxva2", "--enable-mediafoundation",
               "--enable-ffnvcodec", "--enable-cuda", "--enable-cuvid", "--enable-nvenc", "--enable-nvdec",
               "--enable-amf", "--enable-libvpl", "--enable-libass", "--enable-libfreetype",
               "--enable-libharfbuzz", "--enable-libfribidi", "--enable-libaom", "--enable-libvpx",
               "--enable-libwebp", "--enable-libmp3lame", "--enable-libopus", "--enable-libvorbis",
               "--enable-libx264", "--enable-libx265", "--enable-libzimg", "--pkg-config=pkg-config",
               "--pkg-config-flags=--static",
               f"--extra-cflags=-I{prefix}/include", f"--extra-ldflags=-L{prefix}/lib -static",
               "--extra-libs=-lstdc++ -lwinpthread -liconv", "--extra-version=AvaMedia-win-x64"]
    print("Building Windows FFmpeg", flush=True)
    for command in (["./configure", *options], ["make", "-j" + jobs], ["make", "install"]):
        try:
            shared.run(command, cwd=extracted["ffmpeg"], env=env, log=work / "ffmpeg.log")
        finally:
            # Keep configure's compiler/linker diagnostics in the existing log artifact,
            # including when configure fails before make can run.
            config_log = extracted["ffmpeg"] / "ffbuild/config.log"
            if config_log.is_file():
                shutil.copy2(config_log, work / "ffmpeg-config.log")
    base = "AvaMedia-FFmpeg-" + source_lock["ffmpegVersion"] + "-win-x64"
    bundle = work / base
    bundle.mkdir()
    binaries = [install / "bin/ffmpeg.exe", install / "bin/ffprobe.exe", *sorted((install / "bin").glob("*.dll"))]
    # VFW capture and Schannel use Windows system components AVICAP32 and NCRYPT.
    # https://learn.microsoft.com/en-us/windows/win32/api/vfw/nf-vfw-capcreatecapturewindowa
    # https://learn.microsoft.com/en-us/windows/win32/api/ncrypt/nf-ncrypt-ncryptfreeobject
    system_dlls = set("advapi32 avicap32 avrt bcrypt crypt32 gdi32 kernel32 mf mfplat mfuuid msvcrt ncrypt ntdll ole32 oleaut32 propsys psapi secur32 shell32 shlwapi user32 uuid version winmm ws2_32 ucrtbase".split())
    names = {path.name.lower() for path in binaries}
    unbundled = []
    for binary in binaries:
        shutil.copy2(binary, bundle / binary.name)
        shared.run([CROSS + "strip", bundle / binary.name])
        listing = shared.run([CROSS + "objdump", "-p", bundle / binary.name])
        imports = sorted(set(re.findall(r"DLL Name:\s*(\S+)", listing)), key=str.casefold)
        file_format = re.search(r"file format (\S+)", listing)
        with (work / "windows-imports.log").open("a") as stream:
            stream.write(f"{binary.name}: {file_format.group(1) if file_format else 'unknown format'}\n")
            stream.writelines(f"  DLL Name: {dll}\n" for dll in imports)
        if "pei-x86-64" not in listing:
            raise RuntimeError(f"Unexpected Windows architecture: {binary.name}")
        for dll in imports:
            name = dll.lower()
            if name not in names and name.removesuffix(".dll") not in system_dlls and not name.startswith("api-ms-win-"):
                unbundled.append(f"{binary.name}: {dll}")
    if unbundled:
        raise RuntimeError("Unbundled Windows dependencies:\n" + "\n".join(sorted(set(unbundled))))
    for name, root in extracted.items():
        for path in root.rglob("*"):
            if path.is_file() and not path.is_symlink() and ".git" not in path.parts and re.match(
                    r"^(LICENSE|LICENCE|COPYING|COPYRIGHT|NOTICE|PATENTS|FTL\.TXT)([._-].*)?$", path.name, re.I):
                target = bundle / "licenses" / name / path.relative_to(root)
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(path, target)
    manifest = {"schemaVersion": 1, "runtime": "win-x64", "ffmpegVersion": source_lock["ffmpegVersion"],
                "license": "GPL-3.0-or-later", "recipeSha256": shared.sha256(__file__),
                "compiler": shared.run([CROSS + "gcc", "--version"]), "configure": options,
                "files": [{"name": path.name, "bytes": path.stat().st_size, "sha256": shared.sha256(path)}
                          for path in bundle.iterdir() if path.is_file()]}
    (bundle / "build.json").write_text(json.dumps(manifest, indent=2) + "\n")
    (bundle / "NOTICE.txt").write_text("AvaMedia FFmpeg 8.1.3 Windows runtime: GPL-3.0-or-later.\n"
                                      "Includes GPL x264/x265; no nonfree components.\n"
                                      "Corresponding sources and rebuild recipes accompany the AvaMedia Release.\n")
    source_package = work / (base + "-source")
    for path in cached.values():
        target = source_package / "sources" / path.relative_to(source_root)
        target.parent.mkdir(parents=True, exist_ok=True)
        if path.is_dir():
            shutil.copytree(path, target)
        else:
            shutil.copy2(path, target)
    for folder in (HERE, SHARED):
        shutil.copytree(folder, source_package / "scripts" / folder.name,
                        ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
    for name in ("LICENSE", "COPYRIGHT"):
        shutil.copy2(ROOT / name, source_package / name)
    action = source_package / ".github/actions/native-media-windows"
    action.mkdir(parents=True)
    shutil.copy2(ROOT / ".github/actions/native-media-windows/action.yml", action)
    (source_package / "configure-options.json").write_text(json.dumps(options, indent=2) + "\n")
    (source_package / "README.txt").write_text("Rebuild on Ubuntu with the MinGW-w64 POSIX toolchain.\n"
                                             "Install the build packages listed in scripts/windows/Build-FFmpeg.py and the native-media-windows action.\n"
                                             "python3 scripts/windows/Build-FFmpeg.py --source-root sources --output artifacts\n"
                                             "Sources and patches are exact; their original licenses apply. Build scripts: AGPL-3.0-only.\n")
    runtime_archive = args.output / (base + ".zip")
    with zipfile.ZipFile(runtime_archive, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in bundle.rglob("*"):
            if path.is_file():
                archive.write(path, arcname=path.relative_to(work))
    sources_archive = args.output / (base + "-source.tar.gz")
    with tarfile.open(sources_archive, "w:gz") as archive:
        archive.add(source_package, arcname=source_package.name)
    (args.output / (base + "-SHA256SUMS.txt")).write_text(
        "".join(shared.sha256(path) + "  " + path.name + "\n" for path in (runtime_archive, sources_archive)))
    print(f"Windows runtime and corresponding source archives: {args.output}", flush=True)


if __name__ == "__main__":
    main()
