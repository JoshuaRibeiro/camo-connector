const assert = require('node:assert/strict');
const { test } = require('node:test');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const vm = require('node:vm');
const { createRequire } = require('node:module');

test('loopback bridge requires pairing, rejects website origins, and retains sync and backup', async () => {
  const folder = await fs.mkdtemp(path.join(os.tmpdir(), 'camo-bridge-test-'));
  const log = path.join(folder, 'events.jsonl');
  const script = path.join(__dirname, '..', 'bridge.cjs');
  const load = createRequire(script);
  let handler, collect;
  try {
    vm.runInNewContext(await fs.readFile(script, 'utf8'), {
      require: name => name === 'node:http' ? { createServer(callback) {
        handler = callback;
        return { on() {}, listen(port, host) {
          assert.equal(port, 47150);
          assert.equal(host, '127.0.0.1');
        } };
      } } : load(name),
      process: { argv: ['', script, log] },
      setInterval(callback) { collect = callback; return { unref() {} }; },
      console, URL, Buffer
    });
    const token = await fs.readFile(path.join(folder, 'bridge-pairing.key'), 'utf8');
    const auth = { authorization: 'Bearer ' + token };
    const request = (route, { method = 'GET', headers = {}, body } = {}) =>
      new Promise(resolve => {
        const data = { status: 0, headers: {}, text: '' };
        handler({
          url: route, method, headers: { host: '127.0.0.1:47150', ...headers },
          async *[Symbol.asyncIterator]() { if (body) yield Buffer.from(body); }
        }, {
          setHeader(name, value) { data.headers[name.toLowerCase()] = value; },
          writeHead(status) { data.status = status; return this; },
          end(text) { data.text = text; resolve(data); }
        });
      });
    for (const route of ['/events', '/focus', '/backup']) {
      assert.equal((await request(route)).status, 401);
      assert.equal((await request(route, { headers: { ...auth, origin: 'https://mw4.darkmattersolutions.uk' } })).status, 403);
    }
    assert.equal((await request('/events', { headers: { ...auth, host: 'localhost:47150' } })).status, 403);
    await fs.writeFile(log, JSON.stringify({ source: 'live', mode: 'mp', key: 'signal-50:sp-3', delta: 1 }) + '\n');
    collect();
    for (const origin of ['chrome-extension://' + 'a'.repeat(32), 'moz-extension://59a0f88f-f245-4d6a-bde4-e2cba25a4558']) {
      const headers = { ...auth, origin };
      const preflight = await request('/events', { method: 'OPTIONS', headers: { origin } });
      assert.equal(preflight.status, 204);
      assert.equal(preflight.headers['access-control-allow-origin'], origin);
      const events = JSON.parse((await request('/events', { headers })).text);
      assert.ok(events.session);
      assert.equal(events.count, 1);
      assert.equal((await request('/focus?weapon=Signal%2050', {
        method: 'POST', headers: { ...headers, 'x-mw4-connector': 'focus-v1' }
      })).status, 204);
      assert.equal(JSON.parse((await request('/focus', { headers: auth })).text).weapon, 'Signal 50');
    }
    const headers = { ...auth, 'x-mw4-connector': 'backup-v1', 'content-type': 'application/json' };
    const backup = { v: 1, mode: 'mp', counts: { test: 3 }, done: {}, updatedAt: 1 };
    assert.equal(JSON.parse((await request('/backup', { method: 'POST', headers, body: JSON.stringify(backup) })).text).status, 'saved');
    assert.deepEqual(JSON.parse((await request('/backup', { headers })).text).backup, backup);
    const reduced = { ...backup, counts: { test: 1 }, updatedAt: 2 };
    assert.equal(JSON.parse((await request('/backup', { method: 'POST', headers, body: JSON.stringify(reduced) })).text).status, 'preserved');
    assert.deepEqual(JSON.parse((await request('/backup', { headers })).text).backup, backup);
  } finally {
    await fs.rm(folder, { recursive: true, force: true });
  }
});
