import test from 'node:test';
import assert from 'node:assert/strict';
import worker, { ReleaseNotifications } from './worker.mjs';

test('publish requires the release token and a plain version', async () => {
  let forwarded = 0;
  const env = {
    RELEASE_TOKEN: 'private-test-token',
    RELEASES: {
      idFromName: name => name,
      get: () => ({ fetch: () => { forwarded++; return new Response(null, { status: 204 }); } }),
    },
  };
  const post = (token, version) => worker.fetch(new Request('https://example.test/release', {
    method: 'POST', headers: { Authorization: 'Bearer ' + token, 'X-Camo-Version': version },
  }), env);

  assert.equal((await post('wrong', '2.0.16')).status, 401);
  assert.equal((await post(env.RELEASE_TOKEN, '2.0.16-preview')).status, 400);
  assert.equal((await post(env.RELEASE_TOKEN, '2.0.16')).status, 204);
  assert.equal(forwarded, 1);
  assert.equal((await worker.fetch(new Request('https://example.test/updates'), env)).status, 426);
});

test('broadcasts only versions newer than the stored release', async () => {
  const versions = [];
  const storage = new Map();
  const notifications = new ReleaseNotifications({
    storage: { get: key => storage.get(key), put: (key, value) => storage.set(key, value) },
    blockConcurrencyWhile: callback => callback(),
    getWebSockets: () => [{ send: version => versions.push(version) }],
  });
  for (const version of ['2.0.9', '2.0.10', '2.0.8', '2.0.10']) {
    await notifications.fetch(new Request('https://example.test/release', {
      method: 'POST', headers: { 'X-Camo-Version': version },
    }));
  }
  assert.deepEqual(versions, ['2.0.9', '2.0.10']);
  assert.equal(storage.get('latest'), '2.0.10');
});
