const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');

const root = path.resolve(__dirname, '..');
const base = require('../manifest.json');
test('Sunburn ignores absolute reads and applies queued longshots only with +1', async () => {
  const tracker = fs.readFileSync(path.join(root, 'tracker.js'), 'utf8').replace(
    /  poll\(\);\s*setInterval\(poll, 500\);/,
    '  findTarget = globalThis.target; multiplayerSelected = () => true;\n' +
    '  focusedWeapon = key => key === "signal50"; reportTrackerFocus = () => {}; globalThis.pollOnce = poll;');
  const data = {};
  let trackerCount = 39;
  let clicks = 0;
  const button = { click() { clicks++; trackerCount++; } };
  const events = { session: 'session', count: 0, progress: { id: 1, value: 4, through: 0 }, trackedRows: [
    { id: 2, type: 'camo', weapon: 'Signal 50', camo: 'Sunburn', value: 4, goal: 40 },
    { id: 3, type: 'weapon-level', weapon: 'Signal 50', value: 0, goal: 4 },
    { id: 4, type: 'camo', weapon: 'Signal 50', camo: 'Sunburn', value: 40, goal: 40, confirmed: true }
  ] };
  const page = {
    document: { createElement: () => ({ style: {}, setAttribute() {} }), body: { append() {} } },
    chrome: { runtime: { sendMessage: async () => ({ events }) }, storage: { local: {
      get: async () => data, set: async value => Object.assign(data, value)
    } } },
    navigator: { locks: { request: (_, options, callback) => callback({}) } },
    target: () => trackerCount < 40 ? { count: trackerCount, button } : null,
    setTimeout
  };
  vm.runInNewContext(tracker, page);
  await page.pollOnce();
  assert.equal(trackerCount, 39);
  assert.equal(clicks, 0);
  events.count = 1;
  await page.pollOnce();
  assert.equal(trackerCount, 40);
  assert.equal(clicks, 1);
  events.count = 2;
  await page.pollOnce();
  assert.equal(clicks, 1);
  assert.equal(data.sunburnCursor.cursor, 2);
});

test('the details panel recognizes a completed Special camo by its prefixed title', () => {
  const tracker = fs.readFileSync(path.join(root, 'tracker.js'), 'utf8')
    .replace(/  poll\(\);\s*setInterval\(poll, 500\);/, '  globalThis.detailCounter = detailCounter;');
  const page = {
    document: { createElement: () => ({ style: {}, setAttribute() {} }), body: { append() {} } },
    getComputedStyle: () => ({ visibility: 'visible' })
  };
  vm.runInNewContext(tracker, page);
  const node = (tagName, textContent, properties = {}) => ({
    tagName, textContent, children: [], getClientRects: () => [{}],
    querySelectorAll: () => [], getAttribute: () => null, ...properties
  });
  const title = node('DIV', 'Special \u00b7 Off Grid');
  const minus = node('BUTTON', '\u2212');
  const plus = node('BUTTON', '+');
  const count = node('BUTTON', '15');
  const checkbox = node('INPUT', '', { type: 'checkbox', checked: true });
  const panel = node('DIV', '', { querySelectorAll: () => [minus, plus, count] });
  const row = node('DIV', '', { parentElement: panel, querySelectorAll: selector => ({
    button: [minus, plus, count],
    'h2,h3,h4,h5,span,strong,b,p,div,label': [title],
    'span,div,button,output': [title, minus, plus, count],
    input: [checkbox], 'input[type="checkbox"],[role="checkbox"]': [checkbox]
  })[selector] ?? [] });
  minus.parentElement = row;
  const match = page.detailCounter({ panel }, { camo: 'Off Grid' });
  assert.equal(match?.count, 15);
  assert.equal(match?.completed, true);
  assert.equal(match?.identity, 2);
  assert.equal(match?.checkbox, checkbox);
  assert.equal(page.detailCounter({ panel }, { camo: 'Urban Flamingo' }), null);
});

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
