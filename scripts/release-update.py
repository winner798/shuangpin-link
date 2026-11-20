"""Prepare and verify a signed application update release."""

import argparse
import base64
import hashlib
import json
import os
import re
import shutil
import subprocess
import tempfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
ARTIFACTS = {
    "windows-x64": "windows-x64.exe",
    "android-arm64-v8a": "android-arm64-v8a.apk",
    "android-x86_64": "android-x86_64.apk",
}
MAX_VERSION = 2_100_000_000
PACKET_LIMIT = 16 * 1024


def openssl(*args):
    return subprocess.run(["openssl", *map(str, args)], check=True, stdout=subprocess.PIPE).stdout


def key_der(path):
    return base64.b64decode(path.read_text(encoding="ascii").strip(), validate=True)


def version(info):
    code, name = info["versionCode"], info["versionName"]
    if type(code) is not int or not 1 <= code <= MAX_VERSION:
        raise ValueError("Invalid version code")
    if not isinstance(name, str) or not re.fullmatch(r"(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*)){2}", name):
        raise ValueError("Invalid version name")
    return code, name


def file_info(path):
    hashed = hashlib.sha256()
    size = 0
    with path.open("rb") as input_file:
        for chunk in iter(lambda: input_file.read(1024 * 1024), b""):
            hashed.update(chunk)
            size += len(chunk)
    return {"size": size, "sha256": hashed.hexdigest()}


def checked_files(directory, expected=None):
    if expected is not None and set(expected) != set(ARTIFACTS):
        raise ValueError("The release must contain all three platforms")
    found = {}
    for platform, filename in ARTIFACTS.items():
        path = directory / filename
        if path.is_symlink() or not path.is_file():
            raise ValueError(f"Missing regular file: {filename}")
        info = file_info(path)
        limit = (300 if platform == "windows-x64" else 100) * 1024 * 1024
        if not 0 < info["size"] <= limit or (expected is not None and info != expected[platform]):
            raise ValueError(f"Invalid file size or checksum: {filename}")
        found[platform] = info
    return found


def check_package_versions(packages, aapt, code, name):
    windows = packages / ARTIFACTS["windows-x64"]
    product_version = subprocess.check_output(
        ["pwsh", "-NoProfile", "-NonInteractive", "-Command",
         "(Get-Item -LiteralPath $env:SHUANGPIN_STAGED_WINDOWS).VersionInfo.FileVersion"],
        env={**os.environ, "SHUANGPIN_STAGED_WINDOWS": str(windows)}, text=True,
    ).strip()
    if product_version != f"{name}.0":
        raise ValueError(f"Windows installer file version must be {name}.0")
    for arch in ("arm64-v8a", "x86_64"):
        apk = ARTIFACTS[f"android-{arch}"]
        details = subprocess.check_output([str(aapt.resolve()), "dump", "badging", apk],
                                          cwd=packages, encoding="utf-8", errors="replace")
        package = rf"^package: name='io\.github\.winner798\.shuangpinlink' versionCode='{code}' versionName='{re.escape(name)}'"
        detected_arch = re.search(r"(?m)^native-code:\s+(.+)$", details)
        if (not re.search(package, details, re.MULTILINE)
                or not detected_arch or detected_arch[1].strip() != f"'{arch}'"
                or re.search(r"(?m)^application-debuggable\b", details)):
            raise ValueError(f"The {arch} release APK must match the app ID, version and architecture")


def verify(path, public_key):
    with path.open("rb") as source:
        envelope = source.read(PACKET_LIMIT + 1)
    if len(envelope) > PACKET_LIMIT:
        raise ValueError("Release manifest is too large")
    signed = json.loads(envelope)
    manifest = base64.b64decode(signed["manifest"], validate=True)
    signature = base64.b64decode(signed["signature"], validate=True)
    with tempfile.TemporaryDirectory() as temp:
        work = Path(temp)
        (work / "public.der").write_bytes(key_der(public_key))
        (work / "manifest.json").write_bytes(manifest)
        (work / "manifest.sig").write_bytes(signature)
        openssl("dgst", "-sha256", "-keyform", "DER", "-verify", work / "public.der",
                "-signature", work / "manifest.sig", work / "manifest.json")
    release = json.loads(manifest)
    version(release)
    return release, envelope


def stage(args):
    detail = json.loads((ROOT / "updates/version.json").read_text(encoding="utf-8"))
    code, name = version(detail)
    public = ROOT / "updates/public-key.spki.b64"
    if openssl("pkey", "-in", args.key, "-pubout", "-outform", "DER") != key_der(public):
        raise ValueError("The signing key does not match the key embedded in the clients")

    destination = args.output.resolve()
    destination.parent.mkdir(parents=True, exist_ok=True)
    if destination.exists():
        raise ValueError(f"Staging destination already exists: {destination}")
    sources = dict(zip(ARTIFACTS, (args.windows, args.arm64, args.x86_64), strict=True))
    with tempfile.TemporaryDirectory(dir=destination.parent) as temp:
        work = Path(temp) / "release"
        packages = work / str(code)
        packages.mkdir(parents=True)
        for platform, filename in ARTIFACTS.items():
            shutil.copyfile(sources[platform], packages / filename)
        metadata = checked_files(packages)
        check_package_versions(packages, args.aapt, code, name)
        manifest = json.dumps({"versionCode": code, "versionName": name, "files": metadata}, separators=(",", ":")).encode("utf-8")
        payload = Path(temp) / "manifest.json"
        payload.write_bytes(manifest)
        signature = openssl("dgst", "-sha256", "-sign", args.key, payload)
        envelope = {"manifest": base64.b64encode(manifest).decode("ascii"),
                    "signature": base64.b64encode(signature).decode("ascii")}
        pointer = work / "manifest.json"
        pointer.write_text(json.dumps(envelope, separators=(",", ":")), encoding="ascii")
        verify(pointer, public)
        work.rename(destination)
    print(f"Staged signed release {name} ({code}).")


def publish(args):
    import fcntl  # The publication directory is on Ubuntu.

    source = args.source.resolve()
    target = args.target.resolve()
    release, envelope = verify(source / "manifest.json", args.public_key)
    code, name = version(release)
    target.mkdir(parents=True, exist_ok=True, mode=0o755)
    target.chmod(0o755)
    with (target / ".publish.lock").open("a+b") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        current_path = target / "manifest.json"
        if current_path.is_file():
            current, _ = verify(current_path, args.public_key)
            current_code, _ = version(current)
            if code < current_code or (code == current_code and release != current):
                raise ValueError("Use a higher version for different release contents")
        packages = target / str(code)
        with tempfile.TemporaryDirectory(dir=target) as temp:
            work = Path(temp)
            if not packages.exists():
                staged_packages = work / str(code)
                staged_packages.mkdir(mode=0o755)
                staged_packages.chmod(0o755)
                for filename in ARTIFACTS.values():
                    shutil.copyfile(source / str(code) / filename, staged_packages / filename)
                    (staged_packages / filename).chmod(0o644)
                checked_files(staged_packages, release["files"])
                staged_packages.rename(packages)
            else:
                checked_files(packages, release["files"])
            checked_manifest = work / "manifest.json"
            checked_manifest.write_bytes(envelope)
            checked_manifest.chmod(0o644)
            os.replace(checked_manifest, current_path)
    print(f"Published signed release {name} ({code}).")


def verify_staged(args):
    release, _ = verify(args.source / "manifest.json", ROOT / "updates/public-key.spki.b64")
    code, name = version(release)
    expected = json.loads((ROOT / "updates/version.json").read_text(encoding="utf-8"))
    if (code, name) != version(expected):
        raise ValueError("The staged version does not match updates/version.json")
    checked_files(args.source / str(code), release["files"])
    print(f"Verified signed release {name} ({code}).")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    staging = commands.add_parser("stage")
    for option in ("key", "windows", "arm64", "x86-64", "aapt", "output"):
        staging.add_argument(f"--{option}", type=Path, required=True)
    hosting = commands.add_parser("publish")
    for option in ("source", "public-key", "target"):
        hosting.add_argument(f"--{option}", type=Path, required=True)
    verification = commands.add_parser("verify")
    verification.add_argument("--source", type=Path, required=True)
    args = parser.parse_args()
    try:
        {"stage": stage, "publish": publish, "verify": verify_staged}[args.command](args)
    except (OSError, ValueError, KeyError, TypeError, subprocess.CalledProcessError) as error:
        parser.exit(1, f"{error}\n")


if __name__ == "__main__":
    main()
