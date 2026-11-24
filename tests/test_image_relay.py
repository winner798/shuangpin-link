import base64
import http.client
import json
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch

from server.relay import CLIP_TTL_MS, IMAGE_CHUNK, IMAGE_LIMIT, Relay, Store, credential, digest, epoch_ms


class ImageRelayTests(unittest.TestCase):
    first = "1" * 32
    second = "2" * 32

    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        code = credential()
        self.store = Store(Path(temp.name) / "state.sqlite", digest(code))
        self.owner = self.store.bootstrap(code)["token"]
        self.peer = self.store.join(self.store.create_invite(self.owner)["invite"])["token"]
        self.relay = Relay(("127.0.0.1", 0), self.store)
        threading.Thread(target=self.relay.serve_forever, daemon=True).start()
        self.addCleanup(self.relay.server_close)
        self.addCleanup(self.relay.shutdown)
        self.t = epoch_ms()

    def call(self, method, path, token, value=None):
        conn = http.client.HTTPConnection("127.0.0.1", self.relay.server_port, timeout=5)
        try:
            body = json.dumps(value) if value is not None else None
            conn.request(method, path, body=body, headers={"Authorization": "Bearer " + token})
            response = conn.getresponse()
            self.assertEqual(response.getheader("Cache-Control"), "no-store")
            content = response.read()
            return response.status, json.loads(content) if content else None
        finally:
            conn.close()

    def enable(self):
        cap = "/v3/image/capability?enabled=1"
        self.assertEqual(self.call("PUT", cap, self.owner), (200, {"peerEnabled": False}))
        self.assertEqual(self.call("PUT", cap, self.peer), (200, {"peerEnabled": True}))

    def begin(self, image, size, timestamp=None, token=None):
        return self.call("PUT", f"/v3/image/{image}?size={size}&sentAt={timestamp or self.t}", token or self.owner)[0]

    @staticmethod
    def frame(size, byte=b"x"):
        return {"envelope": base64.b64encode(byte * (size + 28)).decode("ascii")}

    def test_atomic_out_of_order_transfer_and_enrollment(self):
        self.assertEqual(self.begin(self.first, 42), 409)
        self.enable()
        self.assertEqual(self.begin(self.first, IMAGE_CHUNK + 1), 204)
        self.assertEqual(self.begin(self.first, IMAGE_CHUNK + 1), 204)
        self.assertEqual(self.begin(self.first, IMAGE_CHUNK), 409)
        url = "/v3/image/" + self.first
        head = self.frame(IMAGE_CHUNK)
        tail = self.frame(1, b"y")
        self.assertEqual(self.call("PUT", url + "/1", self.owner, tail)[0], 204)
        self.assertEqual(self.call("GET", "/v3/image", self.peer)[0], 204)
        self.assertEqual(self.call("GET", url + "/1", self.peer)[0], 404)
        self.assertEqual(self.call("PUT", url + "/0", self.peer, head)[0], 404)
        self.assertEqual(self.call("PUT", url + "/0", self.owner, self.frame(1))[0], 400)
        self.assertEqual(self.call("PUT", url + "/0", self.owner, head)[0], 204)
        self.assertEqual(self.call("PUT", url + "/1", self.owner, tail)[0], 204)
        self.assertEqual(self.call("PUT", url + "/1", self.owner, self.frame(1))[0], 409)
        self.assertEqual(self.call("GET", "/v3/image", self.owner)[0], 204)
        self.assertEqual(self.call("GET", url + "/0", self.owner)[0], 404)
        code, event = self.call("GET", "/v3/image", self.peer)
        self.assertEqual(code, 200)
        self.assertEqual(event, {"revision": 1, "id": self.first, "sender": "owner",
                                "size": IMAGE_CHUNK + 1, "sentAt": self.t})
        self.assertEqual(self.call("GET", url + "/0", self.peer), (200, head))
        self.assertEqual(self.call("GET", url + "/1", self.peer), (200, tail))
        self.assertEqual(self.call("GET", "/v3/image?after=1", self.peer)[0], 204)

    def test_cancel_replay_expiry_permissions_and_disable(self):
        self.enable()
        url = "/v3/image/" + self.first
        self.assertEqual(self.begin(self.first, 1, token=credential()), 401)
        self.assertEqual(self.begin(self.first, 1, token=self.peer), 204)
        self.call("DELETE", url, self.owner)  # Wrong direction cannot cancel a transfer.
        self.assertEqual(self.call("PUT", url + "/0", self.peer, self.frame(1))[0], 204)
        self.assertEqual(self.call("GET", url + "/0", self.owner)[0], 200)
        self.assertEqual(self.call("DELETE", url, self.peer)[0], 204)
        self.assertEqual(self.call("GET", url + "/0", self.owner)[0], 404)
        self.assertEqual(self.begin(self.first, 1, token=self.peer), 409)
        self.assertEqual(self.begin(self.second, 1, self.t + 1), 204)
        with patch("server.relay.epoch_ms", return_value=self.t + CLIP_TTL_MS + 2):
            self.assertEqual(self.call("PUT", "/v3/image/" + self.second + "/0", self.owner, self.frame(1))[0], 404)
        self.assertEqual(self.call("PUT", "/v3/image/capability?enabled=0", self.peer)[0], 200)
        self.assertEqual(self.call("GET", "/v3/image", self.owner)[0], 409)
        self.assertEqual(self.call("POST", "/v1/reset-peer", self.owner)[0], 204)
        self.assertEqual(self.call("GET", "/v3/image", self.peer)[0], 401)
        with self.store.database() as db:
            for table in ("image_event", "image_chunk", "image_receipt", "image_clock", "image_capability"):
                self.assertEqual(db.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0], 0)

    def test_supersession_between_text_and_images(self):
        self.enable()
        self.assertEqual(self.begin(self.first, 1), 204)
        text = {"id": "a" * 32, "sentAt": self.t + 2, **self.frame(1)}
        self.assertEqual(self.call("PUT", "/v2/clip", self.owner, text)[0], 204)
        self.assertEqual(self.call("PUT", f"/v3/image/{self.first}/0", self.owner, self.frame(1))[0], 404)
        self.assertEqual(self.begin(self.second, 1, self.t + 1), 409)
        self.assertEqual(self.begin(self.second, 1, self.t + 3), 204)
        self.assertEqual(self.call("GET", "/v2/clip", self.peer)[0], 200)
        self.assertEqual(self.call("PUT", f"/v3/image/{self.second}/0", self.owner, self.frame(1))[0], 204)
        self.assertEqual(self.call("GET", "/v2/clip", self.peer)[0], 204)
        self.assertEqual(self.call("PUT", "/v2/clip", self.owner, {**text, "id": "b" * 32})[0], 204)
        self.assertEqual(self.call("GET", "/v2/clip", self.peer)[0], 204)
        self.assertEqual(self.call("GET", "/v3/image", self.peer)[0], 200)

    def test_enforced_limits_and_purge(self):
        self.enable()
        self.assertEqual(self.begin(self.first, IMAGE_LIMIT + 1), 413)
        self.assertEqual(self.begin(self.first, 1, self.t - CLIP_TTL_MS), 400)
        self.assertEqual(self.begin(self.first, IMAGE_LIMIT), 204)
        chunk = self.frame(IMAGE_CHUNK)
        for index in range(IMAGE_LIMIT // IMAGE_CHUNK):
            self.assertEqual(self.call("PUT", f"/v3/image/{self.first}/{index}", self.owner, chunk)[0], 204)
        self.assertEqual(self.call("GET", "/v3/image", self.peer)[1]["size"], IMAGE_LIMIT)
        with patch("server.relay.DAILY_IMAGE_BYTES", IMAGE_LIMIT):
            self.assertEqual(self.begin(self.second, 1, self.t + 1), 429)
        with self.store.database(write=True) as db:
            self.store.purge(db, self.t + CLIP_TTL_MS + 1)
            self.assertEqual(db.execute("SELECT COUNT(*) FROM image_chunk").fetchone()[0], 0)
            self.assertEqual(db.execute("SELECT COUNT(*) FROM image_receipt").fetchone()[0], 1)
