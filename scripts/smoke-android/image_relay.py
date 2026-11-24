"""A disposable TLS relay and paired desktop fixture for the installed Android keyboard."""
import base64
import os
from pathlib import Path
import ssl
import struct
import subprocess
import sys
import threading
import time
import uuid
import zlib

from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF
from cryptography.hazmat.primitives import hashes

root = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(root))
from server.relay import Relay, Store, credential, digest


def run(work):
    store = Store(work / "image-smoke.sqlite", digest("fixture"))
    owner = store.bootstrap("fixture")["token"]
    peer = store.join(store.create_invite(owner)["invite"])["token"]
    store.set_image_capability(owner, True)
    secret = os.urandom(32)
    domain = b"ime-sync/v3/image"
    def cipher(direction):
        return AESGCM(HKDF(hashes.SHA256(), 32, domain, direction.encode()).derive(secret))
    def aad(sender, event, at, size, index, count):
        return domain + f"\n{sender}\n{event}\n{at}\n{size}\n{index}\n{count}".encode()

    with Relay(("127.0.0.1", 18766), store, work) as server:
        tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        tls.load_cert_chain(work / "cert.pem", work / "key.pem")
        server.socket = tls.wrap_socket(server.socket, server_side=True)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        der = ssl.PEM_cert_to_DER_cert((work / "cert.pem").read_text())
        app = subprocess.Popen(["adb","shell","am","instrument","-w","-e","cert",base64.b64encode(der).decode(),
            "-e","token",peer,"-e","key",base64.b64encode(secret).decode(),"io.github.winner798.keyboardpreview/.PairedImageSmoke"],
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        try:
            end = time.monotonic() + 80
            while time.monotonic() < end and app.poll() is None:
                if store.set_image_capability(owner, True)["peerEnabled"]:
                    with store.database() as db:
                        event = store.read_image(db, owner, 0)
                    if event: break
                time.sleep(.3)
            else:
                if app.poll() is not None: print(app.stdout.read())
                raise AssertionError("No Android image reached the isolated TLS relay")
            assert 262144 < event["size"] < 524288
            plain = bytearray()
            for i in range(2):
                stored = base64.b64decode(store.get_image_chunk(owner, event["id"], i)["envelope"], validate=True)
                plain.extend(cipher("peer-to-owner").decrypt(stored[:12], stored[12:],
                    aad("peer", event["id"], event["sentAt"], event["size"], i, 2)))
            assert plain[:8] == b"\x89PNG\r\n\x1a\n" and len(plain) == event["size"]
            assert struct.unpack_from(">II", plain, 16) == (320, 320)
            length = struct.unpack_from(">I", plain, 33)[0]
            assert plain[37:41] == b"IDAT" and len(zlib.decompress(plain[41:41+length])) == 320 * (1+320*4)
            eid = uuid.uuid4().hex
            stamp = max(event["sentAt"]+1, int(time.time()*1000))
            store.begin_image(owner,eid,stamp,len(plain))
            for i in range(2):
                nonce = os.urandom(12)
                block = bytes(plain[i*262144:(i+1)*262144])
                frame = nonce + cipher("owner-to-peer").encrypt(nonce, block, aad("owner",eid,stamp,len(plain),i,2))
                store.put_image_chunk(owner,eid,i,frame)
            output, _ = app.communicate(timeout=80)
            print(output)
            assert "Paired Android image upload, receive and rich paste verified" in output
        finally:
            if app.poll() is None: app.kill(); app.wait()
            server.shutdown()

if __name__ == "__main__":
    run(Path(sys.argv[1]))
