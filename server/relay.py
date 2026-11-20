"""Ciphertext sync and private release downloads for a pair of input methods."""

import argparse
import base64
import hashlib
import hmac
import json
import os
import re
import secrets
import sqlite3
import stat
import threading
import time
from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit


CLIP_LIMIT = 64 * 1024
DICTIONARY_LIMIT = 8 * 1024 * 1024
CLIP_TTL = 10 * 60
RECEIPT_TTL = 24 * 3600
CLIP_TTL_MS = CLIP_TTL * 1000
CLIP_CLOCK_AHEAD_MS = 120 * 1000
DAILY_CLIP_LIMIT = 20000
CLEANUP_INTERVAL = 30
INVITE_TTL = 5 * 60
ID_PATTERN = re.compile(r"[a-f0-9]{32}\Z")
UPDATE_DIRECTORY = Path("/opt/shuangpin-link/updates")
UPDATE_MANIFEST_LIMIT = 32 * 1024
UPDATE_CHUNK_SIZE = 128 * 1024
UPDATE_FILES = {
    "windows-x64": "windows-x64.exe",
    "android-arm64-v8a": "android-arm64-v8a.apk",
    "android-x86_64": "android-x86_64.apk",
}
UPDATE_FILE_PATH = re.compile(r"/v1/update/files/([1-9][0-9]{0,9})/([^/]+)\Z")


class RequestError(Exception):
    def __init__(self, status, message):
        super().__init__(message)
        self.status = status


def digest(value):
    return hashlib.sha256(value.encode("ascii")).hexdigest()


def credential():
    return secrets.token_urlsafe(32)


def epoch_ms():
    return time.time_ns() // 1_000_000


class Store:
    def __init__(self, filename, setup_hash):
        self.filename = filename
        self.setup_hash = setup_hash
        self.changed = threading.Condition()
        with self.database() as db:
            db.executescript("""
                CREATE TABLE IF NOT EXISTS device (
                    id TEXT PRIMARY KEY, token TEXT UNIQUE NOT NULL,
                    role TEXT UNIQUE NOT NULL CHECK (role IN ('owner', 'peer'))
                );
                CREATE TABLE IF NOT EXISTS invite (token TEXT PRIMARY KEY, expiry INTEGER);
                CREATE TABLE IF NOT EXISTS clipboard (
                    revision INTEGER PRIMARY KEY AUTOINCREMENT,
                    event_id TEXT NOT NULL, sender TEXT NOT NULL,
                    expiry INTEGER NOT NULL, envelope TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS received (event_id TEXT PRIMARY KEY, expiry INTEGER);
                CREATE TABLE IF NOT EXISTS clipboard_v2 (
                    revision INTEGER PRIMARY KEY AUTOINCREMENT,
                    event_id TEXT NOT NULL, sender TEXT NOT NULL CHECK (sender IN ('owner', 'peer')),
                    sent_at INTEGER NOT NULL, expiry INTEGER NOT NULL, envelope TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS received_v2 (event_id TEXT PRIMARY KEY, expiry INTEGER);
                CREATE TABLE IF NOT EXISTS dictionary (
                    sender TEXT PRIMARY KEY REFERENCES device(id) ON DELETE CASCADE,
                    version INTEGER NOT NULL, envelope TEXT NOT NULL
                );
            """)
        with self.database(write=True) as db:
            # Older relays gave clipboard data the same lifetime as its retry receipt.
            # Reuse the receipt to recover the original send time without adding timestamps.
            db.execute("""UPDATE clipboard SET expiry = MIN(expiry, COALESCE((
                            SELECT expiry - ? FROM received WHERE event_id = clipboard.event_id
                          ), 0))""", (RECEIPT_TTL - CLIP_TTL,))
            self.purge(db, epoch_ms())

    @contextmanager
    def database(self, write=False, *, timeout=5):
        db = sqlite3.connect(self.filename, timeout=timeout)
        db.row_factory = sqlite3.Row
        try:
            db.execute("PRAGMA foreign_keys = ON")
            if write:
                db.execute("PRAGMA secure_delete = ON")
                db.execute("BEGIN IMMEDIATE")
            with db:
                yield db
        finally:
            db.close()

    @staticmethod
    def identify(db, token):
        row = db.execute("SELECT id, role FROM device WHERE token = ?", (digest(token),)).fetchone()
        if row is None:
            raise RequestError(401, "invalid_credential")
        return row

    def paired_role(self, db, token):
        role = self.identify(db, token)["role"]
        if db.execute("SELECT 1 FROM device WHERE role != ?", (role,)).fetchone() is None:
            raise RequestError(409, "peer_required")
        return role

    @staticmethod
    def enroll(db, role):
        device_id, token = secrets.token_hex(16), credential()
        db.execute("INSERT INTO device VALUES (?, ?, ?)", (device_id, digest(token), role))
        return {"id": device_id, "token": token}

    def bootstrap(self, token):
        if not hmac.compare_digest(digest(token), self.setup_hash):
            raise RequestError(401, "invalid_setup_code")
        with self.database(write=True) as db:
            if db.execute("SELECT 1 FROM device").fetchone():
                raise RequestError(409, "already_initialized")
            return self.enroll(db, "owner")

    def create_invite(self, token):
        with self.database(write=True) as db:
            if self.identify(db, token)["role"] != "owner":
                raise RequestError(403, "owner_required")
            if db.execute("SELECT 1 FROM device WHERE role = 'peer'").fetchone():
                raise RequestError(409, "peer_already_paired")
            invite = credential()
            db.execute("DELETE FROM invite")
            db.execute("INSERT INTO invite VALUES (?, ?)", (digest(invite), int(time.time()) + INVITE_TTL))
            return {"invite": invite, "expiresIn": INVITE_TTL}

    def join(self, token):
        with self.database(write=True) as db:
            invite = db.execute("SELECT expiry FROM invite WHERE token = ?", (digest(token),)).fetchone()
            if invite is None or invite["expiry"] <= time.time():
                raise RequestError(401, "invite_unavailable")
            db.execute("DELETE FROM invite")
            return self.enroll(db, "peer")

    def reset_peer(self, token):
        with self.database(write=True) as db:
            if self.identify(db, token)["role"] != "owner":
                raise RequestError(403, "owner_required")
            self.clear_pair(db)
        with self.changed:
            self.changed.notify_all()

    def recover(self, token):
        if not hmac.compare_digest(digest(token), self.setup_hash):
            raise RequestError(401, "invalid_setup_code")
        with self.database(write=True) as db:
            self.clear_pair(db)
            db.execute("DELETE FROM device")
            result = self.enroll(db, "owner")
        with self.changed:
            self.changed.notify_all()
        return result

    @staticmethod
    def clear_pair(db):
        db.execute("DELETE FROM device WHERE role = 'peer'")
        for table in ("invite", "clipboard", "received", "clipboard_v2", "received_v2", "dictionary"):
            db.execute(f"DELETE FROM {table}")

    @staticmethod
    def purge(db, now_ms):
        now = now_ms // 1000
        db.execute("DELETE FROM clipboard WHERE expiry <= ?", (now,))
        db.execute("DELETE FROM received WHERE expiry <= ?", (now,))
        db.execute("DELETE FROM clipboard_v2 WHERE expiry <= ?", (now_ms,))
        db.execute("DELETE FROM received_v2 WHERE expiry <= ?", (now_ms,))
        db.execute("DELETE FROM invite WHERE expiry <= ?", (now,))

    def put_clip(self, token, event_id, envelope):
        now_ms = epoch_ms()
        now = now_ms // 1000
        with self.database(write=True) as db:
            sender = self.identify(db, token)["id"]
            self.purge(db, now_ms)
            if db.execute("SELECT 1 FROM received WHERE event_id = ?", (event_id,)).fetchone():
                return
            if db.execute("SELECT COUNT(*) FROM received").fetchone()[0] >= DAILY_CLIP_LIMIT:
                raise RequestError(429, "daily_clip_limit")
            db.execute("INSERT INTO received VALUES (?, ?)", (event_id, now + RECEIPT_TTL))
            db.execute("DELETE FROM clipboard")
            db.execute("INSERT INTO clipboard (event_id, sender, expiry, envelope) VALUES (?, ?, ?, ?)",
                       (event_id, sender, now + CLIP_TTL, envelope))
        with self.changed:
            self.changed.notify_all()

    def put_clip_v2(self, token, event_id, sent_at, envelope):
        with self.database(write=True) as db:
            sender = self.paired_role(db, token)
            now = epoch_ms()
            if not now - CLIP_TTL_MS < sent_at <= now + CLIP_CLOCK_AHEAD_MS:
                raise RequestError(400, "invalid_timestamp")
            self.purge(db, now)
            if db.execute("SELECT 1 FROM received_v2 WHERE event_id = ?", (event_id,)).fetchone():
                return
            if db.execute("SELECT COUNT(*) FROM received_v2").fetchone()[0] >= DAILY_CLIP_LIMIT:
                raise RequestError(429, "daily_clip_limit")
            expiry = min(now, sent_at) + CLIP_TTL_MS
            db.execute("INSERT INTO received_v2 VALUES (?, ?)", (event_id, now + RECEIPT_TTL * 1000))
            db.execute("DELETE FROM clipboard_v2")
            db.execute("""INSERT INTO clipboard_v2 (event_id, sender, sent_at, expiry, envelope)
                          VALUES (?, ?, ?, ?, ?)""", (event_id, sender, sent_at, expiry, envelope))
        with self.changed:
            self.changed.notify_all()

    def read_clip(self, db, token, after):
        sender = self.identify(db, token)["id"]
        row = db.execute("""SELECT revision, sender, envelope FROM clipboard
                          WHERE revision > ? AND expiry > ?""", (after, epoch_ms() // 1000)).fetchone()
        if row is None:
            return None
        return {"revision": row["revision"], "envelope": row["envelope"] if row["sender"] != sender else None}

    def read_clip_v2(self, db, token, after):
        receiver = self.paired_role(db, token)
        row = db.execute("""SELECT revision, event_id AS id, sender, sent_at AS sentAt, envelope
                          FROM clipboard_v2 WHERE revision > ? AND expiry > ?""", (after, epoch_ms())).fetchone()
        if row is None:
            return None
        if row["sender"] == receiver:
            return {"revision": row["revision"], "envelope": None}
        return dict(row)

    def get_clip(self, token, after, wait, *, v2=False):
        read = self.read_clip_v2 if v2 else self.read_clip
        deadline = time.monotonic() + wait
        with self.changed:
            while True:
                with self.database() as db:
                    # Read authorization and the clipboard from the same database snapshot.
                    db.execute("BEGIN")
                    result = read(db, token, after)
                if result is not None:
                    return result
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return None
                self.changed.wait(timeout=remaining)

    def put_dictionary(self, token, version, envelope):
        with self.database(write=True) as db:
            sender = self.identify(db, token)["id"]
            db.execute("""INSERT INTO dictionary VALUES (?, ?, ?)
                          ON CONFLICT(sender) DO UPDATE SET version = excluded.version, envelope = excluded.envelope
                          WHERE excluded.version > dictionary.version""", (sender, version, envelope))

    def get_dictionary(self, token, after):
        with self.database() as db:
            sender = self.identify(db, token)["id"]
            row = db.execute("""SELECT sender, version, envelope FROM dictionary
                                WHERE sender != ? AND version > ?""", (sender, after)).fetchone()
            return dict(row) if row else None


class Handler(BaseHTTPRequestHandler):
    server_version = "ImeSync/1"

    def setup(self):
        self.request.settimeout(28)
        super().setup()

    def log_message(self, *_):
        pass

    def end_headers(self):
        self.send_header("Cache-Control", "no-store")
        super().end_headers()

    def reply_headers(self, status, size, content_type=None):
        self.send_response(status)
        self.send_header("Content-Length", str(size))
        if content_type:
            self.send_header("Content-Type", content_type)
        self.end_headers()

    def reply(self, status, value=None):
        body = json.dumps(value, separators=(",", ":")).encode("utf-8") if value is not None else b""
        self.reply_headers(status, len(body), "application/json" if body else None)
        if body:
            self.wfile.write(body)

    def token(self):
        authorization = self.headers.get("Authorization", "")
        if not re.fullmatch(r"Bearer [A-Za-z0-9_-]{43}", authorization):
            raise RequestError(401, "credential_required")
        return authorization[7:]

    def payload(self, limit):
        size = self.headers.get("Content-Length", "")
        if not size.isascii() or not size.isdecimal() or len(size) > 10:
            raise RequestError(411, "length_required")
        if int(size) > ((limit + 30) * 4 + 2) // 3 + 256:
            raise RequestError(413, "payload_too_large")
        try:
            value = json.loads(self.rfile.read(int(size)))
            envelope = value["envelope"]
            decoded = base64.b64decode(envelope, validate=True)
            if not isinstance(envelope, str) or not 28 <= len(decoded) <= limit + 28:
                raise ValueError
            return value
        except (ValueError, TypeError, KeyError, UnicodeError) as exc:
            raise RequestError(400, "invalid_payload") from exc

    @staticmethod
    def integer(value, maximum):
        if len(value) > 18 or not value.isascii() or not value.isdecimal() or int(value) > maximum:
            raise RequestError(400, "invalid_number")
        return int(value)

    def send_update(self, path):
        token = self.token()
        with self.server.store.database() as db:
            self.server.store.identify(db, token)
        envelope, manifest = self.server.current_release()
        if path == "/v1/update":
            self.reply_headers(200, len(envelope), "application/json")
            self.wfile.write(envelope)
            return

        match = UPDATE_FILE_PATH.fullmatch(path)
        if not match or match[1] != str(manifest["versionCode"]):
            raise RequestError(404, "update_unavailable")
        platform = match[2]
        filename = UPDATE_FILES.get(platform)
        release_file = manifest["files"].get(platform)
        if filename is None or release_file is None:
            raise RequestError(404, "update_unavailable")
        size = release_file.get("size") if isinstance(release_file, dict) else None
        if type(size) is not int or size < 1:
            raise RequestError(503, "invalid_release")

        try:
            root = self.server.updates_dir.resolve()
            artifact = root / match[1] / filename
            if artifact.resolve(strict=True) != artifact or not artifact.is_file():
                raise RequestError(404, "update_unavailable")
            source = artifact.open("rb")
        except (FileNotFoundError, NotADirectoryError) as exc:
            raise RequestError(404, "update_unavailable") from exc
        except (OSError, RuntimeError) as exc:
            raise RequestError(503, "release_unavailable") from exc

        with source:
            info = os.fstat(source.fileno())
            if not stat.S_ISREG(info.st_mode) or size != info.st_size:
                raise RequestError(503, "invalid_release")
            self.reply_headers(200, size, "application/octet-stream")
            try:
                remaining = size
                while remaining:
                    chunk = source.read(min(remaining, UPDATE_CHUNK_SIZE))
                    if not chunk:
                        return
                    self.wfile.write(chunk)
                    remaining -= len(chunk)
            except OSError:
                # An interrupted response closes without appending a second HTTP response.
                return

    def dispatch(self):
        url = urlsplit(self.path)
        if self.command == "GET" and (url.path == "/v1/update" or url.path.startswith("/v1/update/files/")):
            self.send_update(url.path)
            return None
        query = parse_qs(url.query, max_num_fields=4)
        store = self.server.store
        route = self.command, url.path
        if route == ("GET", "/health"):
            return 200, {"status": "ok", "protocol": 1}
        if route == ("POST", "/v1/bootstrap"):
            return 201, store.bootstrap(self.token())
        if route == ("POST", "/v1/invite"):
            return 201, store.create_invite(self.token())
        if route == ("POST", "/v1/join"):
            return 201, store.join(self.token())
        if route == ("POST", "/v1/reset-peer"):
            store.reset_peer(self.token())
            return 204, None
        if route == ("POST", "/v1/recover"):
            return 201, store.recover(self.token())
        if self.command == "GET" and url.path in ("/v1/clip", "/v2/clip"):
            after = self.integer(query.get("after", ["0"])[0], 10**18 - 1)
            wait = self.integer(query.get("wait", ["0"])[0], 20)
            result = store.get_clip(self.token(), after, wait, v2=url.path == "/v2/clip")
            return (200, result) if result else (204, None)
        if self.command == "PUT" and url.path in ("/v1/clip", "/v2/clip"):
            token = self.token()
            value = self.payload(CLIP_LIMIT)
            event_id = value.get("id")
            if not isinstance(event_id, str) or not ID_PATTERN.fullmatch(event_id):
                raise RequestError(400, "invalid_event")
            if url.path == "/v2/clip":
                sent_at = value.get("sentAt")
                if type(sent_at) is not int:
                    raise RequestError(400, "invalid_timestamp")
                store.put_clip_v2(token, event_id, sent_at, value["envelope"])
            else:
                store.put_clip(token, event_id, value["envelope"])
            return 204, None
        if route == ("PUT", "/v1/dictionary"):
            token = self.token()
            value = self.payload(DICTIONARY_LIMIT)
            version = value.get("version")
            if type(version) is not int or not 0 < version < 10**18:
                raise RequestError(400, "invalid_version")
            store.put_dictionary(token, version, value["envelope"])
            return 204, None
        if route == ("GET", "/v1/dictionary"):
            after = self.integer(query.get("after", ["0"])[0], 10**18 - 1)
            result = store.get_dictionary(self.token(), after)
            return (200, result) if result else (204, None)
        return 404, {"error": "not_found"}

    def handle_request(self):
        try:
            try:
                response = self.dispatch()
            except RequestError as exc:
                response = exc.status, {"error": str(exc)}
            except ValueError:
                response = 400, {"error": "invalid_request"}
            except sqlite3.Error:
                response = 503, {"error": "storage_unavailable"}
            if response is not None:
                self.reply(*response)
        except (BrokenPipeError, ConnectionResetError, TimeoutError):
            return

    do_GET = do_POST = do_PUT = handle_request


class Relay(ThreadingHTTPServer):
    def __init__(self, address, store, updates_dir=UPDATE_DIRECTORY):
        self.store = store
        self.updates_dir = updates_dir
        self.slots = threading.BoundedSemaphore(12)
        self.next_cleanup = 0.0
        super().__init__(address, Handler)

    def current_release(self):
        try:
            with (self.updates_dir / "manifest.json").open("rb") as file:
                envelope = file.read(UPDATE_MANIFEST_LIMIT + 1)
        except (FileNotFoundError, NotADirectoryError) as exc:
            raise RequestError(404, "update_unavailable") from exc
        except OSError as exc:
            raise RequestError(503, "release_unavailable") from exc

        try:
            if len(envelope) > UPDATE_MANIFEST_LIMIT:
                raise ValueError
            signed = json.loads(envelope)
            if not base64.b64decode(signed["signature"], validate=True):
                raise ValueError
            manifest = json.loads(base64.b64decode(signed["manifest"], validate=True))
            version = manifest["versionCode"]
            if type(version) is not int or not 1 <= version <= 0x7fffffff or not isinstance(manifest["files"], dict):
                raise ValueError
        except (ValueError, KeyError, TypeError, RecursionError) as exc:
            raise RequestError(503, "invalid_release") from exc
        return envelope, manifest

    def process_request(self, request, client_address):
        if self.slots.acquire(blocking=False):
            super().process_request(request, client_address)
        else:
            request.close()

    def process_request_thread(self, request, client_address):
        try:
            super().process_request_thread(request, client_address)
        finally:
            self.slots.release()

    def service_actions(self):
        now = time.monotonic()
        if now < self.next_cleanup:
            return
        try:
            with self.store.database(write=True, timeout=0.2) as db:
                self.store.purge(db, epoch_ms())
            self.next_cleanup = now + CLEANUP_INTERVAL
        except sqlite3.Error:
            self.next_cleanup = now + 5


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=18765)
    parser.add_argument("--db", type=Path, required=True)
    parser.add_argument("--updates-dir", type=Path, default=UPDATE_DIRECTORY)
    credentials = os.environ.get("CREDENTIALS_DIRECTORY")
    default_hash = Path(credentials) / "setup-hash" if credentials else None
    parser.add_argument("--setup-hash-file", type=Path, default=default_hash)
    args = parser.parse_args()
    os.umask(0o077)
    if args.setup_hash_file is None:
        parser.error("--setup-hash-file is required outside systemd")
    setup_hash = args.setup_hash_file.read_text(encoding="ascii").strip()
    if not re.fullmatch(r"[a-f0-9]{64}", setup_hash):
        parser.error("setup hash must be a SHA-256 hex digest")
    with Relay(("127.0.0.1", args.port), Store(args.db, setup_hash), args.updates_dir) as server:
        server.serve_forever()


if __name__ == "__main__":
    main()
