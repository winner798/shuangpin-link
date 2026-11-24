"""Run the production relay on loopback with a disposable TLS certificate and pair."""
import base64
import json
import secrets
import ssl
import sys
import re
import threading
import time
from pathlib import Path

root = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(root / "server"))
from relay import Handler, Relay, Store, digest

scratch = Path(sys.argv[1]).resolve()
code = secrets.token_urlsafe(32)
store = Store(scratch / "relay.sqlite", digest(code))
owner = store.bootstrap(code)
peer = store.join(store.create_invite(owner["token"])["invite"])
tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
tls.load_cert_chain(scratch / "cert.pem", scratch / "key.pem")
with Relay(("127.0.0.1", 0), store) as server:
    log_lock = threading.Lock()
    class DiagnosticHandler(Handler):
        def send_response(self, code, message=None):
            route = re.sub(r"/[a-f0-9]{32}", "/<event>", self.path.split("?", 1)[0])
            auth = self.headers.get("Authorization", "")
            role = "owner" if auth == "Bearer " + owner["token"] else "peer" if auth == "Bearer " + peer["token"] else "unknown"
            with log_lock, (scratch / "routes.txt").open("a", encoding="utf-8") as log:
                print(round(time.monotonic(), 2), role, self.command, route, code, file=log)
            super().send_response(code, message)
    server.RequestHandlerClass = DiagnosticHandler
    server.socket = tls.wrap_socket(server.socket, server_side=True)
    pairing = {"server": f"https://127.0.0.1:{server.server_port}/", "owner": owner, "peer": peer,
               "key": base64.b64encode(secrets.token_bytes(32)).decode()}
    pending = scratch / "pair.tmp"
    pending.write_text(json.dumps(pairing), encoding="utf-8")
    pending.replace(scratch / "pair.json")
    server.serve_forever()
