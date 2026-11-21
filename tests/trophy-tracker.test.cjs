const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const test = require('node:test');
const source = fs.readFileSync(require.resolve('../desktop/CamoConnector/TrophyTracker/server.js'), 'utf8');

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
  context.state.current = 2051;
  digits = '2051';
  assert.equal((await context.readHomeTotal(body)).corroborated, false);
  assert.equal((await context.readHomeTotal({ ...body, templateTotal: 2551 })).corroborated, true);
  assert.equal((await context.readHomeTotal({ ...body, templateTotal: 2051 })).corroborated, false);
});
