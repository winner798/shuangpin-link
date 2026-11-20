import base64
import hashlib
import http.client
import json
import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest.mock import patch

from server.relay import CLIP_CLOCK_AHEAD_MS, CLIP_TTL, CLIP_TTL_MS, RECEIPT_TTL, Relay, Store, credential, digest, epoch_ms


class RelayTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.setup_code = credential()
        self.updates = Path(temp.name) / 'updates'
        self.relay = Relay(("127.0.0.1", 0), Store(Path(temp.name) / 'state.sqlite', digest(self.setup_code)), self.updates)
        thread = threading.Thread(target=self.relay.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(self.relay.server_close)
        self.addCleanup(self.relay.shutdown)

    def call(self, method, path, token=None, body=None, raw_response=False):
        conn = http.client.HTTPConnection('127.0.0.1', self.relay.server_port, timeout=5)
        try:
            headers = {'Authorization': f'Bearer {token}'} if token else {}
            payload = json.dumps(body).encode() if body is not None else None
            conn.request(method, path, body=payload, headers=headers)
            response = conn.getresponse()
            self.assertEqual(response.headers.get_all('Cache-Control'), ['no-store'])
            raw = response.read()
            if raw_response:
                return response.status, raw
            return response.status, json.loads(raw) if raw else None
        finally:
            conn.close()

    def pair(self):
        status, owner = self.call('POST', '/v1/bootstrap', self.setup_code)
        self.assertEqual(status, 201)
        self.assertEqual(self.call('POST', '/v1/bootstrap', self.setup_code)[0], 409)
        _, invite = self.call('POST', '/v1/invite', owner['token'])
        status, peer = self.call('POST', '/v1/join', invite['invite'])
        self.assertEqual(status, 201)
        self.assertEqual(self.call('POST', '/v1/join', invite['invite'])[0], 401)
        return owner['token'], peer['token']

    def test_wait_idempotency_filter_and_revocation(self):
        owner, peer = self.pair()
        ciphertext = base64.b64encode(b'x' * 47).decode()
        body = {'id': '1' * 32, 'envelope': ciphertext}
        received = []
        waiter = threading.Thread(target=lambda: received.append(self.call('GET', '/v1/clip?wait=2', peer)))
        waiter.start()
        time.sleep(0.05)
        self.assertEqual(self.call('PUT', '/v1/clip', owner, body)[0], 204)
        waiter.join(timeout=3)
        self.assertEqual(received, [(200, {'revision': 1, 'envelope': ciphertext})])
        self.assertEqual(self.call('GET', '/v1/clip', owner), (200, {'revision': 1, 'envelope': None}))
        second_body = {'id': '2' * 32, 'envelope': base64.b64encode(b'y' * 47).decode()}
        self.assertEqual(self.call('PUT', '/v1/clip', peer, second_body)[0], 204)
        self.assertEqual(self.call('PUT', '/v1/clip', owner, body)[0], 204)
        self.assertEqual(self.call('GET', '/v1/clip?after=1', owner), (200, {'revision': 2, 'envelope': second_body['envelope']}))
        self.assertEqual(self.call('POST', '/v1/reset-peer', peer)[0], 403)
        self.assertEqual(self.call('POST', '/v1/reset-peer', owner)[0], 204)
        self.assertEqual(self.call('GET', '/v1/clip', peer)[0], 401)
        self.assertEqual(self.call('GET', '/v1/clip', owner), (204, None))

    def test_dictionary_versions_and_expiry(self):
        self.assertEqual(self.call('GET', '/health'), (200, {'status': 'ok', 'protocol': 1}))
        self.assertEqual(self.call('GET', '/v1/clip')[0], 401)
        owner, peer = self.pair()
        envelope = base64.b64encode(b'c' * 34).decode()
        self.assertEqual(self.call('PUT', '/v1/dictionary', owner, {'version': 5, 'envelope': envelope})[0], 204)
        self.assertEqual(self.call('PUT', '/v1/dictionary', owner, {'version': 4, 'envelope': envelope})[0], 204)
        self.assertEqual(self.call('GET', '/v1/dictionary', peer)[1]['version'], 5)
        self.assertEqual(self.call('GET', '/v1/dictionary?after=5', peer), (204, None))
        self.assertEqual(self.call('GET', '/v1/dictionary', owner), (204, None))
        body = {'id': '3' * 32, 'envelope': envelope}
        self.assertEqual(self.call('PUT', '/v1/clip', owner, body)[0], 204)
        with self.relay.store.database(write=True) as db:
            db.execute('UPDATE clipboard SET expiry = 0')
        self.assertEqual(self.call('GET', '/v1/clip', peer), (204, None))
        self.assertEqual(self.call('POST', '/v1/recover', self.setup_code)[0], 201)
        self.assertEqual(self.call('GET', '/v1/dictionary', owner)[0], 401)

    def test_reset_revokes_pending_clipboard_receive(self):
        owner, old_peer = self.pair()
        waiting = threading.Event()
        received = []
        condition = self.relay.store.changed
        wait = condition.wait

        def entered_wait(timeout=None):
            waiting.set()
            return wait(timeout)

        waiter = threading.Thread(target=lambda: received.append(self.call('GET', '/v1/clip?wait=3', old_peer)))
        with patch.object(condition, 'wait', side_effect=entered_wait):
            waiter.start()
            self.assertTrue(waiting.wait(timeout=2), 'The peer entered the clipboard wait before reset.')
            self.assertEqual(self.call('POST', '/v1/reset-peer', owner)[0], 204)
            waiter.join(timeout=2)
        self.assertEqual(received, [(401, {'error': 'invalid_credential'})])

    def test_clip_expiry_purges_idle_storage_and_keeps_only_retry_receipt(self):
        owner, peer = self.pair()
        ciphertext = base64.b64encode(b'ciphertext fixture' * 6).decode()
        body = {'id': 'a' * 32, 'envelope': ciphertext}
        now = int(time.time())
        self.assertEqual(self.call('PUT', '/v1/clip', owner, body)[0], 204)
        with self.relay.store.database(write=True) as db:
            expiry = db.execute('SELECT expiry FROM clipboard').fetchone()[0]
            retry_expiry = db.execute('SELECT expiry FROM received').fetchone()[0]
            self.assertGreaterEqual(expiry, now + 600)
            self.assertLessEqual(expiry, int(time.time()) + 600)
            self.assertEqual(retry_expiry - expiry, RECEIPT_TTL - CLIP_TTL)
            db.execute('UPDATE clipboard SET expiry = ?', (now - 1,))
        self.assertIn(ciphertext.encode('ascii'), self.relay.store.filename.read_bytes())

        # Give the existing idle maintenance loop a due cleanup without making an HTTP request.
        self.relay.next_cleanup = 0
        deadline = time.monotonic() + 3
        while True:
            with self.relay.store.database() as db:
                remains = db.execute('SELECT 1 FROM clipboard').fetchone()
            if remains is None or time.monotonic() >= deadline:
                break
            time.sleep(0.02)
        self.assertIsNone(remains)
        self.assertNotIn(ciphertext.encode('ascii'), self.relay.store.filename.read_bytes())
        self.assertEqual(self.call('PUT', '/v1/clip', owner, body)[0], 204)
        self.assertEqual(self.call('GET', '/v1/clip?after=0&wait=0', peer), (204, None))

    def test_restart_applies_shorter_expiry_to_an_already_queued_legacy_clip(self):
        with tempfile.TemporaryDirectory() as temporary:
            filename = Path(temporary) / 'legacy.sqlite'
            setup_hash = digest(self.setup_code)
            store = Store(filename, setup_hash)
            owner = store.bootstrap(self.setup_code)
            envelope = base64.b64encode(b'restart fixture' * 6).decode()
            event_id = 'b' * 32
            store.put_clip(owner['token'], event_id, envelope)

            sent = int(time.time()) - 60
            old_expiry = sent + 24 * 3600
            with store.database(write=True) as db:
                db.execute('UPDATE clipboard SET expiry = ?', (old_expiry,))
                db.execute('UPDATE received SET expiry = ?', (old_expiry,))
            resumed = Store(filename, setup_hash)
            again = Store(filename, setup_hash)
            with again.database() as db:
                self.assertEqual(db.execute('SELECT expiry, envelope FROM clipboard').fetchone()[:],
                                 (sent + 600, envelope))

            old_expiry = sent - CLIP_TTL + 24 * 3600
            with resumed.database(write=True) as db:
                db.execute('UPDATE clipboard SET expiry = ?', (old_expiry,))
                db.execute('UPDATE received SET expiry = ?', (old_expiry,))
            upgraded = Store(filename, setup_hash)
            with upgraded.database() as db:
                self.assertIsNone(db.execute('SELECT 1 FROM clipboard').fetchone())
                self.assertEqual(db.execute('SELECT event_id FROM received').fetchone()[0], event_id)

    def test_generated_error_responses_cannot_be_cached(self):
        self.assertEqual(self.call('DELETE', '/v1/clip', raw_response=True)[0], 501)

    def test_v2_clip_metadata_comes_from_authenticated_role_and_stays_separate(self):
        owner, peer = self.pair()
        first = base64.b64encode(b'v1 envelope' * 5).decode()
        second = base64.b64encode(b'v2 envelope' * 5).decode()
        identifier = '1' * 32
        sent_at = epoch_ms()
        self.assertEqual(self.call('PUT', '/v1/clip', owner, {'id': identifier, 'envelope': first})[0], 204)
        self.assertEqual(self.call('GET', '/v2/clip', peer), (204, None))
        body = {'id': identifier, 'sentAt': sent_at, 'envelope': second, 'sender': 'peer'}
        self.assertEqual(self.call('PUT', '/v2/clip', owner, body)[0], 204)
        self.assertEqual(self.call('GET', '/v1/clip', peer), (200, {'revision': 1, 'envelope': first}))
        expected = {'revision': 1, 'id': identifier, 'sentAt': sent_at, 'sender': 'owner', 'envelope': second}
        self.assertEqual(self.call('GET', '/v2/clip', peer), (200, expected))
        self.assertEqual(self.call('GET', '/v2/clip', owner), (200, {'revision': 1, 'envelope': None}))
        self.assertEqual(self.call('GET', '/v2/clip?after=1&wait=0', peer), (204, None))
        retry = dict(body, sentAt=sent_at + 1, envelope=first)
        self.assertEqual(self.call('PUT', '/v2/clip', owner, retry)[0], 204)
        self.assertEqual(self.call('GET', '/v2/clip', peer), (200, expected))

        reverse = {'id': '2' * 32, 'sentAt': sent_at, 'envelope': second}
        self.assertEqual(self.call('PUT', '/v2/clip', peer, reverse)[0], 204)
        self.assertEqual(self.call('GET', '/v2/clip?after=1', owner), (200, dict(reverse, revision=2, sender='peer')))

    def test_v2_clip_validates_timestamps_and_clamps_retention(self):
        owner, peer = self.pair()
        now = epoch_ms()
        envelope = base64.b64encode(b'clock fixture' * 5).decode()
        body = {'id': '3' * 32, 'envelope': envelope}
        with patch('server.relay.epoch_ms', return_value=now) as clock:
            for invalid in (now - CLIP_TTL_MS - 1, now - CLIP_TTL_MS, now + CLIP_CLOCK_AHEAD_MS + 1, str(now), True, None):
                with self.subTest(timestamp=invalid):
                    result = self.call('PUT', '/v2/clip', owner, dict(body, sentAt=invalid))
                    self.assertEqual(result, (400, {'error': 'invalid_timestamp'}))
            future = now + CLIP_CLOCK_AHEAD_MS
            self.assertEqual(self.call('PUT', '/v2/clip', owner, dict(body, sentAt=future))[0], 204)
            with self.relay.store.database() as db:
                self.assertEqual(db.execute('SELECT expiry FROM clipboard_v2').fetchone()[0], now + CLIP_TTL_MS)

            past = now - CLIP_TTL_MS + 15
            self.assertEqual(self.call('PUT', '/v2/clip', owner, dict(body, id='4' * 32, sentAt=past))[0], 204)
            clock.return_value = now + 14
            self.assertEqual(self.call('GET', '/v2/clip', peer)[1]['sentAt'], past)
            clock.return_value = now + 15
            self.assertEqual(self.call('GET', '/v2/clip', peer), (204, None))
            with self.relay.store.database(write=True) as db:
                self.relay.store.purge(db, now + 15)
                self.assertIsNone(db.execute('SELECT 1 FROM clipboard_v2').fetchone())
                self.assertEqual(db.execute('SELECT COUNT(*) FROM received_v2').fetchone()[0], 2)

    def test_v2_pairing_revocation_clears_ciphertext_and_receipts(self):
        owner, peer = self.pair()
        envelope = base64.b64encode(b'pair fixture' * 5).decode()
        body = {'id': 'e' * 32, 'sentAt': epoch_ms(), 'envelope': envelope}
        self.assertEqual(self.call('GET', '/v2/clip')[0], 401)
        self.assertEqual(self.call('PUT', '/v2/clip', peer, body)[0], 204)
        self.assertEqual(self.call('POST', '/v1/reset-peer', owner)[0], 204)
        self.assertEqual(self.call('GET', '/v2/clip', peer), (401, {'error': 'invalid_credential'}))
        self.assertEqual(self.call('GET', '/v2/clip', owner), (409, {'error': 'peer_required'}))
        self.assertEqual(self.call('PUT', '/v2/clip', owner, body), (409, {'error': 'peer_required'}))
        with self.relay.store.database() as db:
            self.assertIsNone(db.execute('SELECT 1 FROM clipboard_v2').fetchone())
            self.assertIsNone(db.execute('SELECT 1 FROM received_v2').fetchone())
        _, invite = self.call('POST', '/v1/invite', owner)
        _, new_peer = self.call('POST', '/v1/join', invite['invite'])
        self.assertEqual(self.call('GET', '/v2/clip', new_peer['token']), (204, None))

    def test_only_paired_devices_can_fetch_current_release(self):
        owner, peer = self.pair()
        update_url = '/v1/update'
        artifact_url = '/v1/update/files/20261102/windows-x64'
        self.assertEqual(self.call('GET', update_url, owner)[0], 404)
        self.assertEqual(self.call('GET', artifact_url, peer)[0], 404)

        directory = self.updates / '20261102'
        directory.mkdir(parents=True)
        artifact = bytes(range(256)) * 2049
        installer = directory / 'windows-x64.exe'
        installer.write_bytes(artifact)
        (directory / 'android-arm64-v8a.apk').write_bytes(b'not in the release manifest')
        inactive_version = self.updates / '20261101'
        inactive_version.mkdir()
        (inactive_version / 'windows-x64.exe').write_bytes(b'older release')
        manifest = {
            'versionCode': 20261102,
            'versionName': '0.2.0',
            'files': {'windows-x64': {'size': len(artifact), 'sha256': hashlib.sha256(artifact).hexdigest()}},
        }
        envelope = {'manifest': base64.b64encode(json.dumps(manifest).encode()).decode(),
                    'signature': base64.b64encode(b'signature-verified-by-client').decode()}
        raw_manifest = (json.dumps(envelope, indent=2) + '\n').encode()
        (self.updates / 'manifest.json').write_bytes(raw_manifest)

        self.assertEqual(self.call('GET', update_url)[0], 401)
        self.assertEqual(self.call('GET', artifact_url, credential())[0], 401)
        self.assertEqual(self.call('GET', update_url, owner, raw_response=True), (200, raw_manifest))
        self.assertEqual(self.call('GET', artifact_url, peer, raw_response=True), (200, artifact))
        self.assertEqual(self.call('GET', '/v1/update/files/20261102/android-arm64-v8a', peer)[0], 404)
        self.assertEqual(self.call('GET', '/v1/update/files/20261101/windows-x64', peer)[0], 404)
        self.assertEqual(self.call('GET', '/v1/update/files/20261102/%2e%2e%2fmanifest.json', peer)[0], 404)
        installer.write_bytes(b'incomplete')
        self.assertEqual(self.call('GET', artifact_url, peer)[0], 503)
        self.assertEqual(self.call('POST', '/v1/reset-peer', owner)[0], 204)
        self.assertEqual(self.call('GET', artifact_url, peer)[0], 401)


if __name__ == '__main__':
    unittest.main()
