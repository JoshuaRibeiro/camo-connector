const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');

const root = path.resolve(__dirname, '..');
const base = require('../manifest.json');
for (const browser of ['Chrome', 'Firefox', 'Edge', 'Opera']) {
  test(browser + ' build and pairing message boundaries', async () => {
    const folder = path.join(root, 'dist', browser);
    const manifest = JSON.parse(fs.readFileSync(path.join(folder, 'manifest.json'), 'utf8'));
    assert.equal(manifest.manifest_version, 3);
    assert.equal(manifest.version, base.version);
    assert.deepEqual(manifest.permissions, ['storage']);
    assert.deepEqual(manifest.host_permissions, [
      browser === 'Firefox' ? 'http://127.0.0.1/*' : 'http://127.0.0.1:47150/*'
    ]);
    assert.equal(Boolean(manifest.background.scripts), browser === 'Firefox');
    assert.equal(Boolean(manifest.background.service_worker), browser !== 'Firefox');
    assert.ok(fs.statSync(path.join(root, 'dist', `CamoConnector-${browser}-v${base.version}.zip`)).size > 0);
    for (const file of fs.readdirSync(folder).filter(file => file.endsWith('.js'))) {
      new vm.Script(fs.readFileSync(path.join(folder, file), 'utf8'), { filename: file });
    }
    let listener;
    const stored = {};
    const origin = browser === 'Firefox' ? 'moz-extension://test/' : 'chrome-extension://test/';
    const pairing = { url: origin + 'pairing.html' };
    const tracker = { url: 'https://mw4.darkmattersolutions.uk/', frameId: 0 };
    const fakeKey = 'a'.repeat(64);
    const api = { runtime: { getURL: name => origin + name,
      onMessage: { addListener: handler => { listener = handler; } } },
    storage: { local: { get: async () => stored, set: async value => Object.assign(stored, value),
      remove: async names => { for (const name of names) delete stored[name]; } } } };
    let online = true;
    let approved = false;
    vm.runInNewContext(fs.readFileSync(path.join(folder, 'background.js'), 'utf8'), {
      [browser === 'Firefox' ? 'browser' : 'chrome']: api, URL, URLSearchParams, AbortSignal,
      fetch: async (url, options) => {
        if (url.endsWith('/pair')) {
          assert.equal(url, 'http://127.0.0.1:47150/pair');
          assert.equal(options.method, 'POST');
          assert.equal(options.headers['X-MW4-Connector'], 'pair-v1');
          assert.equal(options.headers['X-Camo-Client'], origin.slice(0, -1));
          assert.equal(options.headers.Authorization, undefined);
          return approved ? { ok: true, status: 200, json: async () => ({ token: fakeKey }) } :
            { ok: false, status: 403 };
        }
        assert.equal(url, 'http://127.0.0.1:47150/events');
        assert.equal(options.headers.Authorization, 'Bearer ' + fakeKey);
        if (approved) assert.equal(options.headers['X-Camo-Client'], origin.slice(0, -1));
        assert.equal(options.redirect, 'error');
        if (!online) throw Error('offline');
        return { ok: true, status: 200, json: async () => ({ session: 'fixture' }) };
      }
    });
    const send = (message, sender) => new Promise(resolve => {
      if (!listener(message, sender, resolve)) resolve(null);
    });
    assert.match((await send('sunburn-events', tracker)).error, /Pair/);
    assert.equal(await send({ type: 'pair', key: fakeKey }, tracker), null);
    assert.equal(await send('sunburn-events', { ...tracker, url: tracker.url + '.invalid', frameId: 1 }), null);
    assert.equal((await send({ type: 'pair', key: fakeKey }, pairing)).connected, true);
    assert.equal((await send('sunburn-events', tracker)).events.session, 'fixture');
    online = false;
    assert.match((await send('sunburn-events', tracker)).error, /Waiting/);
    online = true;
    assert.equal((await send('sunburn-events', tracker)).events.session, 'fixture');
    assert.equal((await send({ type: 'disconnect' }, pairing)).connected, false);
    assert.equal(await send({ type: 'auto-pair' }, tracker), null);
    assert.match((await send({ type: 'auto-pair' }, pairing)).error, /declined/);
    assert.equal(stored.bridgePairingKey, undefined);
    approved = true;
    assert.equal((await send({ type: 'auto-pair' }, pairing)).connected, true);
    assert.equal((await send('sunburn-events', tracker)).events.session, 'fixture');
    assert.equal((await send({ type: 'disconnect' }, pairing)).connected, false);
    assert.equal(stored.bridgeClientOrigin, undefined);
  });
}
