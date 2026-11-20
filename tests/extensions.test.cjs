const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');

const root = path.resolve(__dirname, '..');
const base = require('../manifest.json');
test('popup checks extension releases and compares version components numerically', async () => {
  const elements = new Map();
  const document = {
    querySelectorAll: () => [],
    querySelector: selector => {
      if (!elements.has(selector)) elements.set(selector, {
        addEventListener(event, handler) { this[event] = handler; }
      });
      return elements.get(selector);
    }
  };
  const chrome = {
    runtime: { getManifest: () => ({ version: '1.9.9' }), sendMessage: async () => ({ connected: true }) },
    permissions: { request: async () => true }
  };
  vm.runInNewContext(fs.readFileSync(path.join(root, 'pairing.js'), 'utf8'), {
    document, chrome, AbortSignal,
    fetch: async () => ({ ok: true, json: async () => [
      { tag_name: 'v9.0.0', draft: false },
      { tag_name: 'extension-v2.0.0', draft: true },
      { tag_name: 'extension-v1.9.10', draft: false, prerelease: true }
    ] })
  });
  await elements.get('#check-update').click();
  assert.equal(elements.get('#extension-version').textContent, 'Extension v1.9.9');
  assert.equal(elements.get('#update-status').textContent, 'Extension v1.9.10 is available.');
  assert.equal(elements.get('#update-release').href,
    'https://github.com/JoshuaRibeiro/camo-connector/releases/tag/extension-v1.9.10');
  chrome.permissions.request = async () => false;
  await elements.get('#check-update').click();
  assert.equal(elements.get('#update-release').hidden, true);
});

test('Sunburn ignores absolute reads and applies queued longshots only with +1', async () => {
  const tracker = fs.readFileSync(path.join(root, 'tracker.js'), 'utf8').replace(
    /  poll\(\);\s*setInterval\(poll, 500\);/,
    '  findTarget = () => globalThis.target(); multiplayerSelected = () => true; grindingCard = () => null;\n' +
    '  focusedWeapon = key => key === globalThis.focused; reportTrackerFocus = () => {};\n' +
    '  syncGold = globalThis.syncGold; globalThis.pollOnce = poll;\n' +
    '  openWeaponDetails = () => { throw new Error("Completion opened weapon details"); };\n' +
    '  grindingCompletion = (_, name) => globalThis.readCompletion(name);');
  const data = {};
  let trackerCount = 39;
  let clicks = 0;
  const button = { click() { clicks++; trackerCount++; } };
  const events = { session: 'session', count: 0, progress: { id: 1, value: 4, through: 0 }, trackedRows: [
    { id: 2, type: 'camo', weapon: 'Signal 50', camo: 'Sunburn', value: 4, goal: 40 },
    { id: 3, type: 'weapon-level', weapon: 'Signal 50', value: 0, goal: 4 }
  ] };
  const page = {
    document: { createElement: () => ({ style: {}, setAttribute() {} }), body: { append() {} } },
    chrome: { runtime: { sendMessage: async () => ({ events }) }, storage: { local: {
      get: async () => data, set: async value => Object.assign(data, value)
    } } },
    navigator: { locks: { request: (_, options, callback) => callback({}) } },
    focused: 'signal50',
    syncGold: async (_, unlocks) => { page.seenGold = unlocks; return Boolean(unlocks?.length); },
    target: () => trackerCount < 40 ? { count: trackerCount, button } : null,
    readCompletion: () => null,
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
  events.count = 3;
  events.weapon = 'Krait P68';
  events.goldUnlocks = ['Krait P68'];
  page.focused = 'kraitp68';
  await page.pollOnce();
  assert.deepEqual(page.seenGold, ['Krait P68']);
  assert.equal(data.sunburnCursor.cursor, 2);
  data.sunburnCursor.pending = true;
  page.seenGold = null;
  await page.pollOnce();
  assert.deepEqual(page.seenGold, ['Krait P68']);
  assert.equal(clicks, 1);

  events.weapon = 'Signal 50';
  page.focused = 'signal50';
  trackerCount = 39;

  let completionClicks = 0;
  const tick = { isConnected: true, click() { completionClicks++; trackerCount = 40; } };
  page.target = () => ({ count: trackerCount, button, tick });
  events.goldUnlocks = ['Sang 9mm'];
  page.seenGold = null;
  events.trackedRows.push({ id: 4, type: 'camo', weapon: 'Signal 50', camo: 'Sunburn', value: 40, goal: 40, confirmed: true });
  await page.pollOnce();
  await page.pollOnce();
  assert.equal(completionClicks, 1);
  assert.equal(trackerCount, 40);
  assert.equal(clicks, 1);
  assert.deepEqual([...data.sunburnCompletion.handled], [4]);
  assert.deepEqual(page.seenGold, []);
});

test('unavailable camo checkoffs leave later checkoffs runnable and retry when visible', async () => {
  const tracker = fs.readFileSync(path.join(root, 'tracker.js'), 'utf8').replace(
    /  poll\(\);\s*setInterval\(poll, 500\);/,
    '  multiplayerSelected = () => true; grindingCard = () => null; focusedWeapon = key => key === globalThis.focused;\n' +
    '  focusWeapon = () => false; grindingCompletion = globalThis.control; findTarget = globalThis.fallback;\n' +
    '  militaryIncomplete = weapon => weapon === "pila";\n' +
    '  Object.assign(globalThis, { syncTrackedRows, syncChallenges, syncGold });');
  const data = {};
  const available = new Set(['snakeskin', 'Red Tiger', 'sang9mm:Gold']);
  const clicked = [];
  const controls = new Map();
  const page = {
    document: { createElement: () => ({ style: {}, setAttribute() {} }), body: { append() {} } },
    chrome: { storage: { local: { get: async () => data, set: async value => Object.assign(data, value) } } },
    focused: 'sang9mm',
    setTimeout: callback => callback(),
    fallback: (weapon, name, goal) => {
      if (weapon !== '50gs' || name !== 'Frosted' || goal !== 20 || !available.has(name)) return null;
      if (!controls.has(name)) controls.set(name, { isConnected: true, click: () => clicked.push(name) });
      return { button: controls.get(name), count: clicked.includes(name) ? 20 : 19 };
    },
    control: (weapon, name) => {
      if (name === 'Frosted') return null;
      const key = name === 'Gold' ? weapon + ':Gold' : name;
      if (!available.has(key)) return null;
      if (!controls.has(key)) controls.set(key, { isConnected: true, click: () => clicked.push(key) });
      return { button: controls.get(key), done: clicked.includes(key) };
    }
  };
  vm.runInNewContext(tracker, page);
  const challenges = ['menu-autumn', 'menu-snakeskin'];
  await page.syncChallenges('session', challenges);
  available.add('autumn');
  await page.syncChallenges('session', challenges);
  await page.syncChallenges('session', challenges);
  await page.syncGold('session', ['.50 GS', 'Sang 9mm']);
  page.focused = '50gs';
  data.trackedProgress = { session: 'session', handled: [], pending: true };
  const rows = ['Frosted', 'Red Tiger'].map((camo, id) =>
    ({ id, camo, type: 'camo', weapon: '.50 GS', confirmed: true, goal: 20 }));
  await page.syncTrackedRows('session', rows);
  available.add('Frosted');
  await page.syncTrackedRows('session', rows);
  available.add('50gs:Gold');
  await page.syncGold('session', ['.50 GS', 'Sang 9mm']);
  assert.deepEqual(clicked, ['snakeskin', 'autumn', 'sang9mm:Gold', 'Red Tiger', 'Frosted', '50gs:Gold']);
  data.trackedProgress = { session: 'session', handled: [], pendingId: 0 };
  await page.syncTrackedRows('session', rows);
  assert.equal(clicked.filter(name => name === 'Frosted').length, 1);
  assert.deepEqual([...data.trackedProgress.handled], [0, 1]);
  page.focused = 'pila';
  available.add('Frontier');
  const frontier = { id: 2, camo: 'Frontier', type: 'camo', weapon: 'Pila', confirmed: true, goal: 10, value: 10 };
  await page.syncTrackedRows('session', [frontier]);
  await page.syncTrackedRows('session', [frontier]);
  assert.equal(clicked.filter(name => name === 'Frontier').length, 1);
  assert.deepEqual([...data.trackedProgress.handled], [0, 1, 2]);
});

test('tracked progress applies verified increments on the visible weapon card', async () => {
  const tracker = fs.readFileSync(path.join(root, 'tracker.js'), 'utf8').replace(
    /  poll\(\);\s*setInterval\(poll, 500\);/,
    '  multiplayerSelected = focusedWeapon = () => true; grindingCard = weaponDetails = () => null;\n' +
    '  findTarget = globalThis.readCounter; trackedControl = () => null; militaryIncomplete = () => globalThis.incomplete; globalThis.sync = syncTrackedRows;');
  const data = {};
  let count = 8;
  let clicks = 0;
  const button = { isConnected: true, click() { clicks++; count++; } };
  const page = {
    document: { createElement: () => ({ style: {}, setAttribute() {} }), body: { append() {} } },
    chrome: { storage: { local: { get: async () => data, set: async value => Object.assign(data, value) } } },
    readCounter: (weapon, camo, goal) =>
      (weapon === '50gs' && camo === 'Red Tiger' && goal === 30) ||
      (weapon === 'cronensquall' && camo === 'Oni' && goal === 20) ? { count, button } : null,
    setTimeout: callback => callback()
  };
  vm.runInNewContext(tracker, page);
  const row = { id: 13, type: 'camo', weapon: '.50 GS', camo: 'Red Tiger', goal: 30, value: 12 };
  for (let pass = 0; pass < 5; pass++) await page.sync('session', [row]);
  assert.equal(count, 12);
  assert.equal(clicks, 4);
  assert.deepEqual([...data.trackedProgress.handled], [13]);
  await page.sync('session', [{ ...row, id: 14, value: 9 }]);
  assert.equal(count, 12);
  assert.equal(clicks, 4);
  page.incomplete = true;
  count = 0;
  const specialRows = [
    { id: 15, type: 'camo', weapon: 'Cronen Squall', camo: 'Optical Reverb', goal: 20, value: 1 },
    { id: 16, type: 'camo', weapon: 'Cronen Squall', camo: 'Oni', goal: 20, value: 2 }
  ];
  await page.sync('session', specialRows);
  await page.sync('session', specialRows);
  assert.equal(count, 2);
  assert.equal(clicks, 6);
  assert.deepEqual([...data.trackedProgress.handled], [13, 14, 16]);
});

test('Military clicks only the highest tier and later visible stages acknowledge earlier unlocks', async () => {
  const tracker = fs.readFileSync(path.join(root, 'tracker.js'), 'utf8').replace(
    /  poll\(\);\s*setInterval\(poll, 500\);/,
    '  multiplayerSelected = () => true; focusedWeapon = () => true; grindingCard = () => globalThis.card;\n' +
    '  grindingMilitaryControl = (_, tier) => globalThis.military(tier); grindingCompletion = (weapon, name) => globalThis.special?.(weapon, name) ?? null;\n' +
    '  Object.assign(globalThis, { syncMilitary, syncTrackedRows, syncGold, pastCompletionStage });');
  const data = {};
  let count = 0;
  let stage = '';
  const clicked = [];
  const meter = { get textContent() { return stage === 'Platinum' ? 'Platinum Locked' : 'Gold 0/1'; } };
  const title = { parentElement: meter, getClientRects: () => [{}], get textContent() { return stage; } };
  const card = {};
  const fullCard = { textContent: 'Now Grinding .50 GS', contains: node => node === card };
  for (const node of [title, meter, fullCard]) {
    node.querySelectorAll = () => [];
    node.contains ??= () => false;
  }
  card.parentElement = fullCard;
  meter.parentElement = fullCard;
  const controls = new Map();
  const page = {
    document: { querySelectorAll: () => [title], createElement: () => ({ style: {}, setAttribute() {} }), body: { append() {} } },
    chrome: { storage: { local: { get: async () => data, set: async value => Object.assign(data, value) } } },
    card,
    getComputedStyle: () => ({ visibility: 'visible' }),
    setTimeout: callback => callback(),
    military: tier => {
      if (count === 8) return { count, button: null };
      if (!controls.has(tier)) controls.set(tier, {
        isConnected: true,
        getAttribute: () => 'Tick up to Military ' + tier,
        closest: () => null,
        click() { clicked.push(tier); count = tier; }
      });
      return { count, button: controls.get(tier) };
    }
  };
  vm.runInNewContext(tracker, page);
  const rows = [{ id: 1, type: 'camo', camo: 'Frosted', weapon: '.50 GS', confirmed: true }];
  await page.syncMilitary('session', [2, 7, 6], '.50 GS');
  stage = 'Gold';
  await page.syncTrackedRows('session', rows);
  assert.equal(data.trackedProgress, undefined);
  let specialDone = false;
  const specialButton = {
    isConnected: true, getAttribute: () => null,
    click() { specialDone = true; }
  };
  page.special = (weapon, name) => weapon === '50gs' && name === 'Red Tiger'
    ? { button: specialButton, done: specialDone } : null;
  await page.syncTrackedRows('session', [{ id: 2, type: 'camo', camo: 'Red Tiger', weapon: '.50 GS', confirmed: true }]);
  assert.equal(count, 7);
  assert.equal(specialDone, true);
  await page.syncMilitary('session', [2, 7, 6, 8], '.50 GS');
  await page.syncTrackedRows('session', rows);
  assert.deepEqual([...data.trackedProgress.handled], [2, 1]);
  data['goldProgress-han86'] = { session: 'session', handled: [], pending: true };
  await page.syncGold('session', ['.50 GS']);
  assert.equal(data['goldProgress-50gs'], undefined);
  stage = 'Platinum';
  await page.syncGold('session', ['.50 GS']);
  assert.deepEqual([...data['goldProgress-50gs'].handled], ['.50 GS']);

  count = 0;
  let footerClicks = 0;
  card.querySelectorAll = () => [{
    textContent: String.fromCharCode(0x2713) + ' Military 1',
    getClientRects: () => [{}],
    click() { footerClicks++; }
  }];
  page.military = () => ({ count, button: null });
  await page.syncMilitary('session', [1, 2], 'Pila');
  assert.equal(count, 0);
  assert.equal(footerClicks, 0);
  const military2Tile = {
    isConnected: true,
    closest: () => null,
    click() { count = 2; }
  };
  page.military = tier => ({ count, button: tier === 2 ? military2Tile : null });
  await page.syncMilitary('session', [1, 2], 'Pila');
  assert.equal(count, 2);
  assert.equal(footerClicks, 0);
  assert.equal(data['militaryCursor-pila'].cursor, 2);
  assert.equal(page.pastCompletionStage('50gs', 'Platinum'), false);
  stage = 'Harmonic Break';
  assert.equal(page.pastCompletionStage('50gs', 'Platinum'), true);
  assert.equal(page.pastCompletionStage('50gs', 'Harmonic Break'), false);
  assert.deepEqual(clicked, [7, 8]);
});

test('Military tile lookup supports artwork titles in the eight-tile row', () => {
  const tracker = fs.readFileSync(path.join(root, 'tracker.js'), 'utf8').replace(
    /  poll\(\);\s*setInterval\(poll, 500\);/,
    '  globalThis.grindingMilitaryControl = grindingMilitaryControl;');
  const tiles = Array.from({ length: 8 }, (_, index) => ({
    title: 'Artwork ' + index, textContent: '',
    getAttribute: () => null,
    getClientRects: () => [{}]
  }));
  const header = {
    textContent: 'Military · Earn weapon unlocks using the Pila 0/8',
    nextElementSibling: { querySelectorAll: () => tiles },
    getClientRects: () => [{}]
  };
  const parent = { querySelectorAll: selector => selector === 'div' ? [header] : tiles };
  const card = { parentElement: parent, querySelectorAll: () => [] };
  const page = {
    document: { createElement: () => ({ style: {}, setAttribute() {} }), body: { append() {} } },
    getComputedStyle: () => ({ visibility: 'visible' })
  };
  vm.runInNewContext(tracker, page);
  assert.equal(page.grindingMilitaryControl('pila', 2, card).button, tiles[1]);
  assert.equal(page.grindingMilitaryControl('han86', 2, card), null);
  tiles.pop();
  assert.equal(page.grindingMilitaryControl('pila', 2, card).button, null);
});

test('named Now Grinding actions work for any camo and only the matching weapon', () => {
  const tracker = fs.readFileSync(path.join(root, 'tracker.js'), 'utf8').replace(
    /  poll\(\);\s*setInterval\(poll, 500\);/,
    '  multiplayerSelected = () => true; focusedWeapon = () => true; globalThis.readTick = grindingCompletion;');
  const nodes = ['.50 GS', 'Signal 50'].map(textContent => {
    const heading = { textContent, children: [], getClientRects: () => [{}] };
    const buttons = ['Frosted', 'Sunburn', 'Gold'].map(name => ({
      name, getClientRects: () => [{}], getAttribute: key => key === 'aria-label' ? 'Mark ' + name + ' done' : null
    }));
    return { heading, buttons, contains: node => node === heading, querySelectorAll: () => buttons };
  });
  const page = {
    document: { createElement: () => ({ style: {}, setAttribute() {} }), body: { append() {} },
      querySelectorAll: selector => selector.startsWith('section[') ? nodes :
        selector.startsWith('h1,') ? nodes.map(node => node.heading) : [] },
    getComputedStyle: () => ({ visibility: 'visible' })
  };
  vm.runInNewContext(tracker, page);
  assert.equal(page.readTick('50gs', 'Frosted').button, nodes[0].buttons[0]);
  assert.equal(page.readTick('signal50', 'Sunburn').button, nodes[1].buttons[1]);
  assert.equal(page.readTick('signal50', 'Gold').button, nodes[1].buttons[2]);
  nodes[1].buttons[1].disabled = true;
  assert.equal(page.readTick('signal50', 'Sunburn'), null);
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
    let listener, connect;
    const stored = {};
    const origin = browser === 'Firefox' ? 'moz-extension://test/' : 'chrome-extension://test/';
    const pairing = { url: origin + 'pairing.html' };
    const tracker = { url: 'https://mw4.darkmattersolutions.uk/', frameId: 0 };
    const fakeKey = 'a'.repeat(64);
    const api = { runtime: { getURL: name => origin + name,
      onMessage: { addListener: handler => { listener = handler; } },
      onConnect: { addListener: handler => { connect = handler; } } },
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
    const openPort = sender => {
      let release;
      const port = { sender, name: 'mw4-sync-lock',
        postMessage: message => { port.acquired = message.acquired; },
        onDisconnect: { addListener: handler => { release = handler; } },
        disconnect: () => { port.closed = true; release?.(); } };
      connect(port);
      return port;
    };
    const first = openPort(tracker);
    assert.equal(first.acquired, true);
    assert.equal(openPort(tracker).acquired, false);
    assert.equal(openPort({ ...tracker, frameId: 1 }).closed, true);
    assert.equal(openPort({ ...tracker, url: 'https://example.com/' }).closed, true);
    first.disconnect();
    const next = openPort(tracker);
    assert.equal(next.acquired, true);
    next.disconnect();
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
