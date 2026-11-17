const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { randomUUID, randomBytes, timingSafeEqual } = require('node:crypto');
const specialCatalog = require('./special-catalog.json');

const log = process.argv[2] || 'sunburn-events.jsonl';
const pairingFile = path.join(path.dirname(path.resolve(log)), 'bridge-pairing.key');
function loadPairingKey() {
  try {
    fs.writeFileSync(pairingFile, randomBytes(32).toString('hex'), { flag: 'wx', mode: 0o600 });
  } catch (error) {
    if (error.code !== 'EEXIST') throw error;
  }
  const key = fs.readFileSync(pairingFile, 'utf8').trim();
  if (!/^[0-9a-f]{64}$/.test(key)) throw Error('Bridge pairing key is invalid.');
  return Buffer.from('Bearer ' + key);
}
const pairingKey = loadPairingKey();
const handleBackup = require('./backup.cjs')(log);
const session = randomUUID();
let count = 0;
let akimboCount = 0;
const akimboTiming = {};
let weapon = null;
let matchState = null;
let progress = null;
let progressId = 0;
const trackedRows = new Map();
const camoKey = name => name.toLowerCase().replace(/[^a-z0-9]/g, '');
const military = [];
const catalogMilitary = new Map();
const challenges = [];
const goldUnlocks = new Set();
const initial = fs.existsSync(log) ? fs.statSync(log) : null;
let fileIdentity = initial?.ino;
let offset = initial?.size ?? 0;
let pending = '';

function collectEvents() {
  let fd;
  try {
    fd = fs.openSync(log, 'r');
  } catch (error) {
    if (error.code === 'ENOENT') return;
    throw error;
  }
  try {
    const stat = fs.fstatSync(fd);
    if (stat.ino !== fileIdentity || stat.size < offset) {
      fileIdentity = stat.ino;
      offset = 0;
      pending = '';
    }
    if (stat.size === offset) return;
    const buffer = Buffer.alloc(Math.min(stat.size - offset, 64 * 1024));
    const bytes = fs.readSync(fd, buffer, 0, buffer.length, offset);
    offset += bytes;
    const lines = (pending + buffer.subarray(0, bytes).toString('utf8')).split('\n');
    pending = lines.pop();
    for (const line of lines) {
      let event;
      try { event = JSON.parse(line.trim().replace(/^\uFEFF/, '')); } catch { continue; }
      if (event?.source === 'live' && ['match-voting', 'match-confirmed', 'match-started', 'match-ended'].includes(event.kind)) {
        matchState = { kind: event.kind, match: event.match, matchMode: event.matchMode || null };
        if (event.kind === 'match-voting') weapon = null;
      }
      if (event?.kind === 'akimbo-kill' && event.source === 'live' &&
          event.mode === 'mp' && event.weapon === 'Sang 9mm') {
        akimboCount++;
        const bridgeReadAt = new Date().toISOString();
        akimboTiming[akimboCount] = {
          capturedAt: event.at, detectedAt: event.detectedAt, writtenAt: event.writtenAt,
          bridgeReadAt
        };
        console.log('Queued Space Time Akimbo kill', akimboCount, akimboTiming[akimboCount]);
      }
      if (event?.kind === 'progress-snapshot' && event.source === 'live' && event.mode === 'mp' &&
          event.key === 'signal-50:sp-3' && event.goal === 40 &&
          Number.isInteger(event.count) && event.count >= 0 && event.count <= 40) {
        progress = { id: ++progressId, value: event.count, through: count };
        console.log('Sunburn progress snapshot:', progress.value + '/40');
      }
      if (event?.kind === 'progress-snapshot' && event.source === 'live' && event.mode === 'mp' &&
          event.key !== 'signal-50:sp-3' && ['camo', 'weapon-level'].includes(event.type) &&
          typeof event.weapon === 'string' && /^[a-z0-9 .-]{2,32}$/i.test(event.weapon) &&
          (event.type === 'camo' || typeof event.objective === 'string' && event.objective.length > 0 && event.objective.length <= 160) &&
          Number.isInteger(event.count) && Number.isInteger(event.goal) &&
          event.count >= 0 && event.count <= event.goal && event.goal > 0 && event.goal <= 9999) {
        const artwork = event.identity === 'artwork' && event.type === 'camo' &&
          Object.hasOwn(specialCatalog, event.weapon) &&
          specialCatalog[event.weapon]?.some(entry => entry.name === event.camo &&
            entry.artworkRGB12);
        if (event.type === 'camo' && (typeof event.camo !== 'string' || !camoKey(event.camo) ||
            event.camo.length > 80 || (event.identity === 'artwork' && !artwork))) continue;
        const spaceTime = event.type === 'camo' && event.weapon === 'Sang 9mm' &&
          event.goal === 50 && camoKey(event.camo) === 'spacetime';
        const key = spaceTime ? 'sang9mm:space-time' :
          (event.type === 'camo' ? [event.weapon, camoKey(event.camo)] :
            [event.weapon, event.objective, event.goal]).join('|').toLowerCase();
        const previous = trackedRows.get(key);
        if (!previous || (!previous.confirmed && (event.count !== previous.value || event.goal !== previous.goal ||
            (event.camo && event.camo !== previous.camo) || (spaceTime && previous.akimboThrough !== akimboCount)))) {
          trackedRows.set(key, { id: ++progressId, weapon: event.weapon,
            ...(event.type === 'weapon-level' && { objective: event.objective }),
            camo: typeof event.camo === 'string' ? event.camo : null,
            type: event.type, value: event.count, goal: event.goal, ...(artwork && { identity: 'artwork' }),
            ...(spaceTime && { akimboThrough: akimboCount }) });
          console.log(event.weapon, event.camo || event.objective, event.count + '/' + event.goal);
        }
      }
      if (event?.kind === 'challenge-completed' && event.source === 'live' && event.mode === 'mp') {
        const banner = ['ads-25', 'streak-3x5'].includes(event.challenge);
        const menu = ['menu-autumn', 'menu-snakeskin', 'menu-space-time'].includes(event.challenge);
        const id = event.weapon === 'Sang 9mm' && (banner || menu) ? event.challenge :
          !event.weapon && banner ? 'selected-' + event.challenge : null;
        if (id && !challenges.includes(id)) {
          challenges.push(id);
          console.log('Queued challenge', id);
        }
      }
      if (event?.kind === 'catalog-special-unlocked' && event.source === 'live' && event.mode === 'mp') {
        const camo = Object.hasOwn(specialCatalog, event.weapon) &&
          specialCatalog[event.weapon].find(entry => entry.name === event.challenge);
        if (camo && Number.isInteger(camo.goal)) {
          const spaceTime = event.weapon === 'Sang 9mm' && camo.name === 'Space Time';
          const key = spaceTime ? 'sang9mm:space-time' :
            [event.weapon, camoKey(camo.name)].join('|').toLowerCase();
          if (!trackedRows.get(key)?.confirmed) {
            trackedRows.set(key, { id: ++progressId, weapon: event.weapon, camo: camo.name,
              goal: camo.goal, value: camo.goal, type: 'camo', confirmed: true,
              ...(spaceTime && { akimboThrough: akimboCount }) });
            console.log('Verified Special unlock:', event.weapon, camo.name);
          }
        }
      }
      if (event?.kind === 'military-unlock' && event.source === 'live' && event.mode === 'mp' &&
          event.weapon !== 'Sang 9mm' &&
          typeof event.weapon === 'string' && /^[a-z0-9 .-]{2,32}$/i.test(event.weapon) &&
          Number.isInteger(event.tier) && event.tier >= 1 && event.tier <= 8) {
        const tiers = catalogMilitary.get(event.weapon) || [];
        if (!tiers.includes(event.tier)) tiers.push(event.tier);
        catalogMilitary.set(event.weapon, tiers);
      }
      if (event?.kind === 'completionist-unlock' && event.source === 'live' && event.mode === 'mp' &&
          event.challenge === 'Gold' && typeof event.weapon === 'string' &&
          /^[a-z0-9 .-]{2,32}$/i.test(event.weapon) && !goldUnlocks.has(event.weapon)) {
        goldUnlocks.add(event.weapon);
        console.log('Queued Gold unlock:', event.weapon);
      }
      if (event?.kind === 'weapon' && event.source === 'live' && event.mode === 'mp' &&
          typeof event.weapon === 'string' && /^[a-z0-9 .-]{2,32}$/i.test(event.weapon)) {
        weapon = event.weapon;
        console.log('Equipped weapon:', weapon);
      }
      if (event?.kind === 'military-unlock' && event.source === 'live' &&
          event.mode === 'mp' && event.weapon === 'Sang 9mm' &&
          Number.isInteger(event.tier) && event.tier >= 1 && event.tier <= 8 &&
          !military.includes(event.tier)) {
        military.push(event.tier);
        console.log('Queued Sang 9mm Military', event.tier);
      }
      if (event?.source === 'live' && event.mode === 'mp' &&
          event.key === 'signal-50:sp-3' && event.delta === 1) {
        count++;
        console.log('Queued Sunburn longshot', count);
      }
    }
  } finally {
    fs.closeSync(fd);
  }
}

let trackerFocus = null;
let trackerFocusAt = 0;

const server = http.createServer((request, response) => {
  if (request.headers.host !== '127.0.0.1:47150' || !request.url.startsWith('/') ||
      request.url.startsWith('//')) return response.writeHead(403).end();
  const origin = request.headers.origin;
  if (origin) {
    if (!/^(?:chrome-extension:\/\/[a-p]{32}|moz-extension:\/\/[0-9a-f-]{36})$/i.test(origin)) {
      return response.writeHead(403).end();
    }
    response.setHeader('Access-Control-Allow-Origin', origin);
    response.setHeader('Vary', 'Origin');
    if (request.method === 'OPTIONS') {
      response.setHeader('Access-Control-Allow-Methods', 'GET, POST');
      response.setHeader('Access-Control-Allow-Headers', 'Authorization, Content-Type, X-MW4-Connector');
      return response.writeHead(204).end();
    }
  }
  response.setHeader('Cache-Control', 'no-store');
  const supplied = Buffer.from(request.headers.authorization || '');
  if (supplied.length !== pairingKey.length || !timingSafeEqual(supplied, pairingKey)) {
    return response.writeHead(401).end();
  }
  let url;
  try { url = new URL(request.url, 'http://127.0.0.1:47150'); }
  catch { return response.writeHead(400).end(); }
  if (request.headers.host === '127.0.0.1:47150' && url.pathname === '/focus') {
    if (request.method === 'POST' && request.headers['x-mw4-connector'] === 'focus-v1') {
      const name = url.searchParams.get('weapon');
      if (!name || !/^[a-z0-9 .-]{2,40}$/i.test(name)) return response.writeHead(400).end();
      trackerFocus = name;
      trackerFocusAt = Date.now();
      return response.writeHead(204).end();
    }
    if (request.method !== 'GET') return response.writeHead(405).end();
    response.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
    return response.end(JSON.stringify({ weapon: Date.now() - trackerFocusAt < 15000 ? trackerFocus : null }));
  }
  if (request.headers.host === '127.0.0.1:47150' && request.url === '/backup') {
    void handleBackup(request, response);
    return;
  }
  if (request.headers.host !== '127.0.0.1:47150' ||
      request.method !== 'GET' || request.url !== '/events') {
    response.writeHead(404).end();
    return;
  }
  try {
    response.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
    response.end(JSON.stringify({ session, count, akimboCount, akimboTiming, weapon, matchState, military, challenges, progress,
      trackedRows: [...trackedRows.values()], catalogMilitary: [...catalogMilitary],
      goldUnlocks: [...goldUnlocks] }));
  } catch (error) {
    console.error(error.message);
    response.writeHead(500).end();
  }
});
server.on('error', error => {
  console.error(error.message);
  process.exitCode = 1;
});
server.listen(47150, '127.0.0.1', () => {
  console.log('Camo bridge ready. Watching', require('node:path').resolve(log));
  console.log('Only new log entries from this run are queued. Ctrl+C stops.');
});
const readTimer = setInterval(() => {
  try { collectEvents(); } catch (error) { console.error('Log read failed:', error.message); }
}, 100);
readTimer.unref();
