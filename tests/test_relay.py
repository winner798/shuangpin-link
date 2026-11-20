import base64
import hashlib
import http.client
import json
import tempfile
import threading
import time
import unittest
from pathlib import Path

from server.relay import Relay, Store, credential, digest


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
