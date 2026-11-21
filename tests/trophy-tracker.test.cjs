const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const test = require('node:test');
const source = fs.readFileSync(require.resolve('../desktop/CamoConnector/TrophyTracker/server.js'), 'utf8');

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
