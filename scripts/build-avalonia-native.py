#!/usr/bin/env python3
"""Build the ABI-compatible Avalonia 12.1.4 accessibility fix with Command Line Tools."""
import argparse
import concurrent.futures
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
REVISION = "2d79ba55fae1b1e71c69f93a668080c9029e6a31"
ARCHIVE_SHA256 = "7ef31cc7819ecbf4a1195bece4e5fe1117032a59ee0f47c0c1782ea618804a39"


def run(args, **kwargs):
    return subprocess.run([str(a) for a in args], check=True, **kwargs)


def build(output):
    patch = ROOT / "native/avalonia/accessibility-lifetime.patch"
    sdk = subprocess.check_output(["xcrun", "--show-sdk-path"], text=True).strip()
    compiler = subprocess.check_output(["xcrun", "clang++", "--version"], text=True)
    fingerprint = hashlib.sha256(patch.read_bytes() + (REVISION + sdk + compiler + Path(__file__).read_text()).encode()).hexdigest()
    cache = ROOT / ".tools/avalonia-native"
    work = cache / fingerprint
    library = work / "libAvaloniaNative.dylib"
    if not library.exists():
        cache.mkdir(parents=True, exist_ok=True)
        archive = cache / "source.tar.gz"
        if not archive.exists():
            temporary = archive.with_suffix(".download")
            for attempt in range(5):
                try:
                    request = urllib.request.Request(f"https://api.github.com/repos/AvaloniaUI/Avalonia/tarball/{REVISION}", headers={"User-Agent": "HerdrContinue-build"})
                    with urllib.request.urlopen(request, timeout=60) as response, temporary.open("wb") as stream:
                        shutil.copyfileobj(response, stream)
                    temporary.replace(archive)
                    break
                except OSError:
                    if attempt == 4:
                        raise
        if hashlib.sha256(archive.read_bytes()).hexdigest() != ARCHIVE_SHA256:
            raise RuntimeError("Avalonia source archive checksum mismatch; refusing to build.")
        # A failed build can be retried without applying a patch twice.
        if work.exists():
            shutil.rmtree(work)
        work.mkdir()
        with tarfile.open(archive) as source_archive:
            source_archive.extractall(work, filter="data")
        source = next(work.glob("AvaloniaUI-Avalonia-*"))
        run(["patch", "--batch", "-p1", "-i", patch], cwd=source)
        run([os.environ.get("DOTNET", "dotnet"), "build", source / "native/Avalonia.Native/Avalonia.Native.macOS.proj",
             "-t:GenerateMicroComItems", "-p:BuildAvaloniaNativeXcodeProject=false", "--source", "https://api.nuget.org/v3/index.json"], cwd=ROOT)
        native = source / "native/Avalonia.Native"
        project = json.loads(subprocess.check_output(["plutil", "-convert", "json", "-o", "-", str(native / "src/OSX/Avalonia.Native.OSX.xcodeproj/project.pbxproj")]))
        objects = project["objects"]
        phase = next(v for v in objects.values() if v["isa"] == "PBXSourcesBuildPhase")
        sources = [objects[objects[ref]["fileRef"]]["path"] for ref in phase["files"]]
        thin_libraries = []
        for arch in ("arm64", "x86_64"):
            target = work / arch
            target.mkdir()

            def compile_source(name):
                obj = target / (name + ".o")
                run(["xcrun", "clang++", "-c", native / "src/OSX" / name, "-o", obj,
                     "-arch", arch, "-isysroot", sdk, "-mmacosx-version-min=14.0", "-std=gnu++14", "-O2", "-fmodules",
                     "-fno-objc-arc" if name == "noarc.mm" else "-fobjc-arc", "-Wno-deprecated-declarations", "-I", native / "inc"])
                return obj

            with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
                compiled = list(pool.map(compile_source, sources))
            thin = target / "libAvaloniaNative.dylib"
            frameworks = ["AppKit", "QuartzCore", "IOSurface", "OpenGL", "Carbon", "Metal", "CoreGraphics", "UniformTypeIdentifiers"]
            run(["xcrun", "clang++", "-dynamiclib", "-arch", arch, "-isysroot", sdk, "-mmacosx-version-min=14.0",
                 "-install_name", "@rpath/libAvaloniaNative.dylib", "-o", thin, *compiled,
                 *[argument for framework in frameworks for argument in ("-framework", framework)]])
            thin_libraries.append(thin)
        temporary_library = work / "combined.dylib"
        run(["xcrun", "lipo", "-create", *thin_libraries, "-output", temporary_library])
        run(["codesign", "--force", "--sign", "-", temporary_library])
        temporary_library.replace(library)
    output.parent.mkdir(parents=True, exist_ok=True)
    if not output.exists() or output.read_bytes() != library.read_bytes():
        shutil.copy2(library, output)
    print(f"Patched Avalonia.Native: {output}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    build(parser.parse_args().output.resolve())
