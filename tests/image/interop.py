"""Exercise the actual .NET and Kotlin image codecs through a disposable HTTP relay."""
import argparse
import base64
import json
import os
from pathlib import Path
import random
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request
import uuid

from PIL import Image, ImageSequence, PngImagePlugin

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from server.relay import IMAGE_CHUNK, Relay, Store, credential, digest

MARKER = b"synthetic GPS marker"


def pictures(directory):
    # Use a multi-chunk screenshot as well as palettes, semitransparency, and animations.
    pixels = random.Random(123).randbytes(360 * 360 * 4)
    screenshot = Image.frombytes("RGBA", (360, 360), pixels)
    first = Image.new("RGBA", (32, 24), (220, 0, 20, 255))
    second = Image.new("RGBA", (32, 24), (12, 43, 150, 125))
    png = PngImagePlugin.PngInfo()
    png.add_text("Location", MARKER.decode())
    screenshot.save(directory / "screen.png", pnginfo=png)
    exif = Image.Exif()
    exif[274] = 6  # portrait, rotated clockwise for display
    exif[270] = MARKER.decode()
    exif[34853] = {1: "N", 2: (12.0, 34.0, 0.0), 3: "E", 4: (56.0, 7.0, 0.0)}
    first.convert("RGB").save(directory / "photo.jpg", exif=exif, progressive=True)
    first.save(directory / "animation.gif", save_all=True, append_images=[second], duration=[100, 200],
               loop=0, comment=MARKER)
    first.save(directory / "animation.webp", save_all=True, append_images=[second], duration=[100, 200],
               loop=0, exif=exif, lossless=True)
    first.save(directory / "animation.png", save_all=True, append_images=[second], duration=[100, 200],
               loop=0, pnginfo=png, exif=exif)
    first.convert("RGB").save(directory / "lossy.webp", exif=exif)
    return sorted(directory.iterdir())


def frames(path):
    with Image.open(path) as image:
        return [(f.size, f.convert("RGBA").tobytes(), f.info.get("duration")) for f in ImageSequence.Iterator(image)]


def rejected(command):
    result = subprocess.run(command, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    if result.returncode == 0:
        raise AssertionError("Malformed image or invalid authentication data was accepted")


def exercise(dotnet, kotlin):
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        inputs = root / "original"
        inputs.mkdir()
        source_files = pictures(inputs)
        code = credential()
        store = Store(root / "test.sqlite", digest(code))
        owner = store.bootstrap(code)["token"]
        peer = store.join(store.create_invite(owner)["invite"])["token"]
        with Relay(("127.0.0.1", 0), store) as relay:
            threading.Thread(target=relay.serve_forever, daemon=True).start()
            def call(method, route, token, body=None):
                payload = None if body is None else json.dumps(body).encode()
                req = urllib.request.Request(f"http://127.0.0.1:{relay.server_port}{route}", method=method,
                                             data=payload, headers={"Authorization": "Bearer " + token})
                with urllib.request.urlopen(req, timeout=10) as response:
                    data = response.read()
                    return json.loads(data) if data else None
            try:
                for token in (owner, peer):
                    call("PUT", "/v3/image/capability?enabled=1", token)
                for file in source_files:
                    original = file.read_bytes()
                    assert MARKER in original
                    stripped = []
                    for i, impl in enumerate((dotnet, kotlin)):
                        dest = root / f"clean-{i}"
                        subprocess.run([*impl, "strip", str(file), str(dest)], check=True)
                        stripped.append(dest.read_bytes())
                        assert MARKER not in stripped[-1] and frames(file) == frames(dest), file.name
                        with Image.open(dest) as image, Image.open(file) as input_image:
                            assert 34853 not in image.getexif()
                            assert image.getexif().get(274) == input_image.getexif().get(274)
                        damaged = root / "truncated"
                        damaged.write_bytes(original[:-2])
                        rejected([*impl, "strip", str(damaged), str(dest)])
                    assert stripped[0] == stripped[1]
                    for role, sender, receiver, send, receive in (
                            ("owner", owner, peer, dotnet, kotlin), ("peer", peer, owner, kotlin, dotnet)):
                        eid, ts = uuid.uuid4().hex, str(time.time_ns() // 1_000_000)
                        url = "/v3/image/" + eid
                        args = [str(file), role, eid, ts]
                        tx, rx = root / "tx", root / "rx"
                        tx.mkdir(exist_ok=True)
                        rx.mkdir(exist_ok=True)
                        subprocess.run([*send, "seal", *args, str(tx)], check=True)
                        call("PUT", f"{url}?size={len(original)}&sentAt={ts}", sender)
                        parts = range((len(original) + IMAGE_CHUNK - 1) // IMAGE_CHUNK)
                        for i in reversed(parts):
                            call("PUT", f"{url}/{i}", sender, {"envelope": (tx/f"{i}.txt").read_text()})
                        event = call("GET", "/v3/image", receiver)
                        assert event["id"] == eid and event["sender"] == role
                        for i in parts:
                            (rx/f"{i}.txt").write_text(call("GET", f"{url}/{i}", receiver)["envelope"])
                        subprocess.run([*receive, "open", *args, str(rx)], check=True)
                        first = rx / "0.txt"
                        frame = bytearray(base64.b64decode(first.read_text()))
                        frame[-1] ^= 1
                        first.write_text(base64.b64encode(frame).decode())
                        rejected([*receive, "open", *args, str(rx)])
                    print(file.name, "passed metadata, frame fidelity, both transfer directions and tamper checks", flush=True)
            finally:
                relay.shutdown()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--java", default="java")
    parser.add_argument("--jar", type=Path, required=True)
    parser.add_argument("--stdlib", type=Path)
    args = parser.parse_args()
    classpath = str(args.jar) + (os.pathsep + str(args.stdlib) if args.stdlib else "")
    exercise([args.dotnet, str(args.dll)], [args.java, "-cp", classpath, "com.osfans.trime.link.ImageFixtureKt"])
