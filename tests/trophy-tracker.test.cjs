const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const test = require('node:test');
const source = fs.readFileSync(require.resolve('../desktop/CamoConnector/TrophyTracker/server.js'), 'utf8');

test('queued OCR crops retain their modes and results if the native worker is unavailable', async () => {
  const createWorker = require('../desktop/CamoConnector/TrophyTracker/ocr-worker.cjs');
  const calls = [];
  const read = createWorker(require('node:path').join(__dirname, 'absent-ocr-worker'), __dirname,
    async (crop, mode, digits) => { calls.push([crop, mode, digits]); return crop; });
  assert.deepEqual(await Promise.all([read('label', 7, false), read('counter', 8, true)]), ['label', 'counter']);
  assert.deepEqual(calls, [['label', 7, false], ['counter', 8, true]]);
});

test('a live full-line and template match needs only the labelled crop', async () => {
  const context = vm.createContext({
    readText: async crop => { assert.equal(crop, 'line'); return 'TROPHIES COLLECTED: 2538)'; }
  });
  vm.runInContext(source.slice(source.indexOf('async function readLobbyLine'), source.indexOf('// A short sequence')), context);
  assert.equal(await context.readLobbyLine('line', null, null, 2538), 2538);
  assert.equal(await context.readLobbyLine('line', null, null, 2598), null);
});

test('a partial upper stroke cannot rewrite a pair of independently read threes', async () => {
  const context = vm.createContext({ readText: async () => '2538' });
  vm.runInContext(source.slice(source.indexOf('async function readTotal'), source.indexOf('async function readLobbyLine')), context);
  assert.equal(await context.readTotal({ digits: 'one', mask: 'two', glyphs: 4,
    closedUpperLoop: [false, false, true, false] }), 2538);
});

test('an OCR-only lobby correction needs distinct corroborated full-line crops', () => {
  const context = vm.createContext({ state: { current: 4938, match: null, source: 'Lobby' } });
  vm.runInContext('function save(change) { Object.assign(state, change); }\n' +
    source.slice(source.indexOf('function isLargeTotalChange'), source.indexOf('const files =')), context);
  let time = 0;
  const read = crop => context.observe('home', 2538, 'session', time += 300, false, 'lobby', crop);
  for (let i = 0; i < 4; i++) read('a');
  read('b');
  read('c');
  assert.equal(context.state.current, 4938);
  read('d');
  assert.equal(context.state.current, 2538);
});

test('the lobby matcher reads the captured 2544 numeral shapes', async () => {
  const { digitShape, classifyTotal } = await import('../desktop/CamoConnector/TrophyTracker/recognition.js');
  const { digits } = require('../desktop/CamoConnector/TrophyTracker/signatures.json');
  // Thresholded pixels from the 2544 report, held out from the digit templates.
  const rows = [
    '...####..#####.....##.....##..',
    '..#....#.#.........##.....##..',
    '..#...##.#........#.#....###..',
    '.....##..####....#..#...##.#..',
    '....##....####..##..#...#..#..',
    '...##........##.#...#..##..#..',
    '..##...#.#...#..######.######.',
    '..#....#.#...#......#......#..',
    '..######.#####......#......#..'
  ];
  const shapes = [[2, 8], [9, 14], [16, 21], [23, 28]].map(([left, right]) => ({
    shape: digitShape((x, y) => rows[y][x] === '#', left, right, 0, rows.length),
    narrow: false
  }));
  assert.equal(classifyTotal(shapes, digits), 2544);
  shapes[1].shape.fill(0);
  assert.equal(classifyTotal(shapes, digits), null);
});

test('a recognized trophy label allows a template read to skip numeric OCR', async () => {
  let label = 'TROPHIES COLLECTED';
  let numberReads = 0;
  const context = vm.createContext({
    readText: async () => label,
    readLobbyLine: async () => { numberReads++; return 2044; },
    isLargeTotalChange: () => false
  });
  vm.runInContext(source.slice(source.indexOf('async function readHomeTotal'), source.indexOf('const files =')), context);
  const body = { label: 'label', templateTotal: 2544 };
  const read = await context.readHomeTotal(body);
  assert.equal(read.value, 2544);
  assert.equal(read.corroborated, true);
  assert.equal(numberReads, 0);
  label = 'UNRELATED LABEL';
  assert.equal((await context.readHomeTotal(body)).value, 2044);
  assert.equal(numberReads, 1);
});

test('lobby OCR stabilizes large corrections and accepts small updates quickly', () => {
  const context = vm.createContext({
    state: { current: 4240, match: null, source: 'Lobby' }
  });
  vm.runInContext(
    'function save(change) { Object.assign(state, change); }\n' +
    source.slice(source.indexOf('function isLargeTotalChange'), source.indexOf('const files =')),
    context
  );
  const read = (value, time, confirmed = false, layout = 'lobby') =>
    context.observe('home', value, 'capture', time, confirmed, layout);
  read(2228, 100);
  read(2228, 300);
  read(2228, 500);
  assert.equal(context.state.current, 4240);
  read(2228, 700, true);
  read(2228, 900, true);
  assert.equal(context.state.current, 2228);
  read(4240, 1100, true);
  read(4240, 1300, true);
  read(4240, 1500);
  // Corroborating the same jump twice allows legitimate adjustments.
  assert.equal(context.state.current, 4240);
  read(4250, 1700);
  read(4250, 1900);
  assert.equal(context.state.current, 4250);
  read(2228, 2100, true, 'events');
  read(2228, 2300, true, 'events');
  read(2228, 2500, true, 'events');
  assert.equal(context.state.current, 4250);
});

test('an apparent jump to 4938 cannot bypass a lobby OCR reading of 2538', async () => {
  const context = vm.createContext({
    state: { current: 2536 },
    readText: async () => 'TROPHIES COLLECTED',
    readLobbyLine: async () => 2538
  });
  vm.runInContext(source.slice(source.indexOf('function isLargeTotalChange'), source.indexOf('const files =')), context);
  const read = await context.readHomeTotal({ label: 'label', templateTotal: 4938 });
  assert.equal(read.value, 2538);
  assert.equal(read.corroborated, true);
  context.state.current = 4938;
  const correction = await context.readHomeTotal({ label: 'label', templateTotal: 2538 });
  assert.equal(correction.value, 2538);
  assert.equal(correction.corroborated, true);
});

test('interleaved lobby OCR errors do not erase a supported correction', () => {
  const context = vm.createContext({ state: { current: 4938, match: null, source: 'Lobby' } });
  vm.runInContext(
    'function save(change) { Object.assign(state, change); }\n' +
    source.slice(source.indexOf('function isLargeTotalChange'), source.indexOf('const files =')),
    context
  );
  const reads = [[2538, true], [4938, true], [2038, false], [2538, true],
    [2938, true], [4038, false], [2538, false]];
  reads.forEach(([number, corroborated], index) => {
    context.observe('home', number, 'capture', 100 + index * 450, corroborated, 'lobby');
    assert.equal(context.state.current, index === reads.length - 1 ? 2538 : 4938);
  });
});

test('the full lobby line corroborates the shape read despite a noisy number crop', async () => {
  const context = vm.createContext({
    readText: async crop => crop === 'line' ? 'TROPHIES COLLECTED 2538)' : '29368'
  });
  vm.runInContext(source.slice(source.indexOf('async function readLobbyLine'), source.indexOf('// A short sequence')), context);
  assert.equal(await context.readLobbyLine('line', 'digits', null, 2538), 2538);
  assert.equal(await context.readLobbyLine('line', 'digits', null, 2938), null);
});

test('lobby OCR recovers a six when original pixels corroborate a masked read', async () => {
  const context = vm.createContext({
    readText: async (crop, mode) => crop === 'original' || (crop === 'mask' && mode === 7) ? '2226' : '2228'
  });
  vm.runInContext(
    source.slice(source.indexOf('async function readTotal'), source.indexOf('// A short sequence')),
    context
  );
  const pair = { digits: 'digits', mask: 'mask', glyphs: 4 };
  assert.equal(await context.readTotal(pair, 8, 7, 'original'), 2226);
  assert.equal(await context.readTotal(pair, 8, 7), 2228);
});

test('lobby fallback requires the visible label and separately read counter to agree', async () => {
  let labelled = 'TROPHIES COLLECTED 2222';
  const context = vm.createContext({
    readText: async (crop) => crop === 'line' ? labelled : '2222'
  });
  vm.runInContext(
    source.slice(source.indexOf('async function readLobbyLine'), source.indexOf('// A short sequence')),
    context
  );
  assert.equal(await context.readLobbyLine('line', 'number'), 2222);
  labelled = 'TROPHIES COLLECTED 2232';
  assert.equal(await context.readLobbyLine('line', 'number'), null);
  labelled = 'DAILY CHALLENGES 2222';
  assert.equal(await context.readLobbyLine('line', 'number'), null);
});

test('request reader accepts a multi-crop lobby sample and bounds oversized input', async () => {
  const context = vm.createContext({});
  vm.runInContext(source.slice(source.indexOf('async function readBody'), source.indexOf('let ocrInProgress')), context);
  const lobbyLine = 'A'.repeat(1_050_000);
  async function* request(body) {
    const json = JSON.stringify(body);
    yield json.slice(0, 600_000);
    yield json.slice(600_000);
  }
  assert.equal((await context.readBody(request({ lobbyLine }))).lobbyLine.length, lobbyLine.length);
  await assert.rejects(context.readBody(request({ lobbyLine: 'A'.repeat(4 * 1024 * 1024) })), /too large/);
});

test('corroborated lobby totals skip the slower masked OCR path', async () => {
  const context = vm.createContext({
    readLobbyLine: async () => 2345,
    isLargeTotalChange: () => false,
    readText: async () => assert.fail('masked label read should be skipped'),
    readTotal: async () => assert.fail('masked total read should be skipped')
  });
  vm.runInContext(
    source.slice(source.indexOf('async function readHomeTotal'), source.indexOf('const files =')),
    context
  );
  const read = await context.readHomeTotal({ label: 'label', lobbyLine: 'full line', rawTotal: 'number' });
  assert.equal(read.value, 2345);
  assert.equal(read.corroborated, true);
});

test('a corroborated small lobby gain publishes on the first frame', () => {
  const context = vm.createContext({ state: { current: 2313, match: null, source: 'Lobby' } });
  vm.runInContext(
    'function save(change) { Object.assign(state, change); }\n' +
    source.slice(source.indexOf('function isLargeTotalChange'), source.indexOf('const files =')),
    context
  );
  context.observe('home', 2345, 'capture', 100, true, 'lobby');
  assert.equal(context.state.current, 2345);
  context.observe('home', 4240, 'capture', 300, true, 'lobby');
  assert.equal(context.state.current, 2345);
  context.observe('home', 2335, 'capture', 500, true, 'lobby');
  assert.equal(context.state.current, 2345);
});

test('a 2046 outlier can be corrected by independently corroborated 2546 crops', async () => {
  let alternate = 2546;
  const context = vm.createContext({
    isLargeTotalChange: (value) => Math.abs(value - 2536) > 126,
    readLobbyLine: async () => 2046,
    readText: async () => 'TROPHIES COLLECTED',
    readTotal: async (crop) => crop === 'wide' ? alternate : 2546
  });
  vm.runInContext(source.slice(source.indexOf('async function readHomeTotal'), source.indexOf('const files =')), context);
  const body = { label: 'label', primary: { glyphs: 4 }, alternate: 'wide' };
  let read = await context.readHomeTotal(body);
  assert.equal(read.value, 2546);
  assert.equal(read.corroborated, true);
  alternate = null;
  read = await context.readHomeTotal(body);
  assert.equal(read.value, 2046);
  assert.equal(read.corroborated, false);
});

test('foreground digits can resolve conflicting 2906 and 2006 lobby reads', async () => {
  const context = vm.createContext({
    isLargeTotalChange: (value) => Math.abs(value - 2536) > 126,
    readLobbyLine: async () => 2906,
    readText: async (crop) => crop === 'foreground' ? '2556' : 'TROPHIES COLLECTED',
    readTotal: async (crop) => crop === 'wide' ? 2556 : 2006
  });
  vm.runInContext(source.slice(source.indexOf('async function readHomeTotal'), source.indexOf('const files =')), context);
  const result = await context.readHomeTotal({
    label: 'label', primary: { glyphs: 4 }, alternate: 'wide', foregroundTotal: 'foreground'
  });
  assert.equal(result.value, 2556);
  assert.equal(result.corroborated, true);
});

test('repeated 2044 reads cannot overwrite a nearby trusted 25xx total', () => {
  const context = vm.createContext({ state: { current: 2536, match: null, source: 'Lobby' } });
  vm.runInContext(
    'function save(change) { Object.assign(state, change); }\n' +
    source.slice(source.indexOf('function isLargeTotalChange'), source.indexOf('const files =')),
    context
  );
  for (const time of [100, 300, 500, 700]) context.observe('home', 2044, 'capture', time, true, 'lobby');
  assert.equal(context.state.current, 2536);
  context.observe('home', 2544, 'capture', 900, true, 'lobby');
  assert.equal(context.state.current, 2544);

  context.state.current = 2044;
  for (const time of [1100, 1300, 1500]) context.observe('home', 2544, 'capture', time, true, 'lobby');
  assert.equal(context.state.current, 2544);
});

test('dim Events card can confirm a large correction without a second mask pair', async () => {
  let digits = '2551';
  const context = vm.createContext({
    state: { current: 2051, match: null, source: 'Lobby' },
    readLobbyLine: async () => null,
    readText: async (crop) => crop === 'label' ? 'EVENTS' : digits,
    readTotal: async (pair) => pair ? 2551 : null
  });
  vm.runInContext(
    'function save(change) { Object.assign(state, change); }\n' +
    source.slice(source.indexOf('function isLargeTotalChange'), source.indexOf('const files =')),
    context
  );
  const body = { layout: 'events', label: 'label', rawTotal: 'original pixels', primary: { glyphs: 4 } };
  for (const time of [100, 300, 500]) {
    const read = await context.readHomeTotal(body);
    assert.equal(read.corroborated, true);
    context.observe('home', read.value, 'capture', time, read.corroborated, body.layout);
  }
  assert.equal(context.state.current, 2551);
  context.state.current = 2695;
  for (const time of [700, 900]) context.observe('home', 2685, 'capture', time, true, 'events');
  assert.equal(context.state.current, 2695);
  context.observe('home', 2685, 'capture', 1100, true, 'events');
  assert.equal(context.state.current, 2685);
  context.state.current = 2665;
  context.readText = async () => 'TROPHIES COLLECTED';
  context.readTotal = async pair => pair ? 2655 : null;
  for (const time of [1300, 1500, 1700]) {
    const read = await context.readHomeTotal({ layout: 'wager', label: 'banner', primary: { glyphs: 4 }, alternate: { glyphs: 4 } });
    context.observe('home', read.value, 'capture', time, read.corroborated, 'wager');
  }
  assert.equal(context.state.current, 2655);
  context.readText = async crop => crop === 'label' ? 'EVENTS' : digits;
  context.readTotal = async pair => pair ? 2551 : null;
  context.state.current = 2051;
  digits = '2051';
  assert.equal((await context.readHomeTotal(body)).corroborated, false);
  assert.equal((await context.readHomeTotal({ ...body, templateTotal: 2551 })).corroborated, true);
  assert.equal((await context.readHomeTotal({ ...body, templateTotal: 2051 })).corroborated, false);
});

test('the Wager panel corrects a stale total after three confirmed frames', () => {
  for (const initial of [3531, 9521]) {
    const context = vm.createContext({ state: { current: initial, match: null, source: 'Lobby' } });
    vm.runInContext('function save(change) { Object.assign(state, change); }\n' +
      source.slice(source.indexOf('function isLargeTotalChange'), source.indexOf('const files =')), context);
    for (let frame = 1; frame <= 3; frame++) {
      context.observe('home', 3521, 'session', frame * 300, true, 'wager', null, true);
      assert.equal(context.state.current, frame === 3 ? 3521 : initial);
    }
    for (let frame = 4; frame <= 9; frame++)
      context.observe('home', 9521, 'session', frame * 300, true, 'wager');
    assert.equal(context.state.current, 3521);
  }
});

test('Wager matches the captured 3521 at two foreground thresholds', async () => {
  const { wagerShapes, classifyTotal } = await import('../desktop/CamoConnector/TrophyTracker/recognition.js');
  const { digits } = require('../desktop/CamoConnector/TrophyTracker/signatures.json');
  // Two-threshold foreground mask from the reported Wager screen.
  const foreground = require('node:zlib').inflateSync(Buffer.from('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', 'base64'));
  const pixels = Buffer.alloc(foreground.length * 4);
  foreground.forEach((v, i) => { pixels[i * 4] = pixels[i * 4 + 1] = v; });
  for (const floor of [170, 205])
    assert.equal(classifyTotal(wagerShapes(pixels, 545, 185, floor), digits), 3521);
});

test('dim Events tile corrects 4155 to 4145 only after corroboration', async () => {
  const state = { current: 4155, source: 'Lobby', match: null };
  const context = vm.createContext({ state, readText: async crop => crop, save: change => Object.assign(state, change) });
  vm.runInContext(source.slice(source.indexOf('function isLargeTotalChange'), source.indexOf('const files =')), context);
  const tile = { layout: 'events', label: 'EVENTS', eventsLine: '4145', rawTotal: '4145' };
  for (const conflict of [{ label: 'LIVE' }, { eventsLine: '4155' }])
    assert.equal((await context.readHomeTotal({ ...tile, ...conflict })).value, null);
  for (let frame = 1; frame <= 3; frame++) {
    const read = await context.readHomeTotal(tile);
    context.observe('home', read.value, 'session', frame * 400, read.corroborated, 'events');
    assert.equal(state.current, frame === 3 ? 4145 : 4155);
  }
});
