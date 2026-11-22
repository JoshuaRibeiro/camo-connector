'use strict';
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const { createHash } = require('node:crypto');
const { spawn } = require('node:child_process');

const origin = 'http://127.0.0.1:47851';
const dataFile = process.env.TROPHY_STATE_FILE || path.join(__dirname, 'tracker-state.json');
const tesseract = process.env.TROPHY_TESSERACT || 'C:/Program Files/Tesseract-OCR/tesseract.exe';
let state = { current: 0, target: 10000, match: null, source: 'Starting total' };
if (fs.existsSync(dataFile)) state = JSON.parse(fs.readFileSync(dataFile, 'utf8'));

const dailyTargets = {
  '2026-11-18': 714,
  '2026-11-19': 1429,
  '2026-11-20': 2143,
  '2026-11-21': 2857,
  '2026-11-22': 3571,
  '2026-11-23': 4286,
  '2026-11-24': 5000,
  '2026-11-25': 5714,
  '2026-11-26': 6429,
  '2026-11-27': 7143,
  '2026-11-28': 7857,
  '2026-11-29': 8571,
  '2026-11-30': 9286,
  '2026-12-01': 10000
};
const londonCalendar = new Intl.DateTimeFormat('en-GB', {
  timeZone: 'Europe/London', year: 'numeric', month: '2-digit', day: '2-digit',
  hour: '2-digit', hourCycle: 'h23'
});
const [firstScheduledDay, lastScheduledDay] = ['2026-11-18', '2026-12-01'];

function londonDay(at = new Date()) {
  const { year, month, day, hour } = Object.fromEntries(
    londonCalendar.formatToParts(at).map(({ type, value }) => [type, value])
  );
  const goalDay = Number(day) - (Number(hour) < 4 ? 1 : 0);
  return new Date(Date.UTC(Number(year), Number(month) - 1, goalDay)).toISOString().slice(0, 10);
}

function scheduledTarget(day) {
  return dailyTargets[day > lastScheduledDay ? lastScheduledDay : day];
}

function publicState(day = londonDay()) {
  const { targetOverrideDay, ...visible } = state;
  const scheduled = scheduledTarget(day);
  if (scheduled === undefined) return visible;
  return {
    ...visible,
    target: targetOverrideDay === day || (day > lastScheduledDay && targetOverrideDay > lastScheduledDay)
      ? state.target
      : scheduled
  };
}

function save(change) {
  const updated = { ...state, ...change };
  fs.writeFileSync(dataFile + '.tmp', JSON.stringify(updated));
  fs.renameSync(dataFile + '.tmp', dataFile);
  state = updated;
}

const cropReads = new Map();
function cliRead(png, mode, digits) {
  return new Promise((resolve, reject) => {
    const args = ['stdin', 'stdout', '--psm', String(mode)];
    if (digits) args.push('-c', 'tessedit_char_whitelist=0123456789');
    const process = spawn(tesseract, args, { windowsHide: true, timeout: 3500 });
    let output = '';
    process.stdout.on('data', chunk => { output += chunk; });
    process.stderr.resume();
    process.stdin.on('error', () => {});
    process.on('error', reject);
    process.on('close', code => {
      if (code === 0) resolve(output.trim());
      else reject(new Error('Tesseract OCR failed to read the crop.'));
    });
    process.stdin.end(Buffer.from(png, 'base64'));
  });
}
const readCrop = process.env.TROPHY_OCR_WORKER
  ? require('./ocr-worker.cjs')(process.env.TROPHY_OCR_WORKER, path.dirname(tesseract), cliRead) : cliRead;
function readText(png, mode, digits = false) {
  const key = createHash('sha256').update(`${mode}:${digits}:`).update(png).digest('hex');
  if (cropReads.has(key)) return cropReads.get(key);
  const reading = readCrop(png, mode, digits);
  if (cropReads.size >= 64) cropReads.delete(cropReads.keys().next().value);
  cropReads.set(key, reading);
  void reading.catch(() => { if (cropReads.get(key) === reading) cropReads.delete(key); });
  return reading;
}

async function readTotal(pair, mode = 8, retryMode, rawTotal) {
  if (!pair?.digits || !pair.mask) return null;
  let [digits, cleanDigits] = await Promise.all([
    readText(pair.digits, mode, true), readText(pair.mask, mode, true)
  ]);
  const complete = text => /^(0|[1-9]\d{0,5})$/.test(text) && text.length === pair.glyphs;
  if (retryMode && (!complete(digits) || digits !== cleanDigits || /[068]/.test(digits + cleanDigits))) {
    const completeFirstReads = [digits, cleanDigits].filter(complete);
    const [line, cleanLine, raw] = await Promise.all([
      readText(pair.digits, retryMode, true), readText(pair.mask, retryMode, true),
      rawTotal ? readText(rawTotal, retryMode, true) : Promise.resolve('')
    ]);
    // Retry known glyph confusions only when another OCR pass supports the candidate.
    const compatible = (read, candidate) => [...read].every((digit, index) =>
      digit === candidate[index] || (digit === '8' && candidate[index] === '6') ||
      (digit === '0' && candidate[index] === '5'));
    if (complete(raw) && [digits, cleanDigits, line, cleanLine].includes(raw) &&
        [digits, cleanDigits, line, cleanLine].filter(complete).every(read => compatible(read, raw))) {
      digits = cleanDigits = raw;
    } else if (complete(line) && line === cleanLine && completeFirstReads.every(read => compatible(read, line))) {
      digits = line;
      cleanDigits = cleanLine;
    }
  }
  if (!complete(digits) || digits !== cleanDigits) return null;
  return Number(digits);
}

async function readLobbyLine(lineCrop, numberCrop, foregroundCrop, templateTotal) {
  if (!lineCrop || (!numberCrop && templateTotal == null)) return null;
  const line = await readText(lineCrop, 7);
  // The icon's antialiased edge can appear as punctuation after the total.
  const labelled = /\bTROPHIES\s+[C@]OLLECTED\s*:?\s+([1-9]\d{0,5}|0)\s*[:)\]]?\s*$/i.exec(line);
  if (!labelled) return null;
  if (Number(labelled[1]) === templateTotal) return templateTotal;
  // A contradictory shape read cannot be corroborated by another OCR crop
  // from the same frame.
  if (templateTotal != null) return null;
  if (labelled[1] === await readText(numberCrop, 7, true)) return Number(labelled[1]);
  if (foregroundCrop && labelled[1] === await readText(foregroundCrop, 7, true))
    return Number(labelled[1]);
  return null;
}

// A short sequence of matching OCR reads can still repeat the same bad crop.
function isLargeTotalChange(value) {
  return state.current > 0 && Math.abs(value - state.current) > Math.max(25, state.current * .05);
}

function isSuspectedFiveDrop(value) {
  if (value >= state.current || !isLargeTotalChange(value)) return false;
  const last = String(state.current);
  const read = String(value);
  if (last.length !== read.length) return false;
  // Reject a false drop if a missing stroke would leave the total near the last
  // accepted reading. Never publish the inferred number.
  return [...read].some((digit, index) => digit === '0' && last[index] === '5' &&
    Math.abs(Number(read.slice(0, index) + '5' + read.slice(index + 1)) - state.current) <= 25);
}

let candidate = null;
let skippedMatchValue = null;
let lobbyReads = [];

function confirmLobbyCorrection(value, session, frameTime, corroborated, labelledCrop) {
  // Background motion can alternate correct digits with plausible OCR errors.
  // Keep a bounded window and require the correction to lead competing reads.
  lobbyReads = lobbyReads.filter(read =>
    read.session === session && frameTime > read.time && frameTime - read.time <= 8000
  ).slice(-8);
  lobbyReads.push({ value, session, time: frameTime, corroborated, labelledCrop });
  const matching = lobbyReads.filter(read => read.value === value);
  const votes = reads => {
    const strong = reads.filter(read => read.corroborated).length;
    const distinctCrops = new Set(reads.map(read => read.labelledCrop).filter(Boolean)).size;
    return Math.max(strong, distinctCrops >= 4 ? distinctCrops : 0);
  };
  const support = votes(matching);
  if (matching.length < 3 || support < 2) return false;
  const competing = new Map();
  for (const read of lobbyReads) {
    if (read.value !== value) {
      if (!competing.has(read.value)) competing.set(read.value, []);
      competing.get(read.value).push(read);
    }
  }
  return [...competing.values()].every(reads => votes(reads) < support);
}

function observe(kind, value, session, frameTime, corroborated = false, layout = null, labelledCrop = null, wagerShape = false) {
  if (kind === 'match' || (kind === 'home' && layout !== 'lobby')) lobbyReads = [];
  if (value === null) {
    const sameSession = candidate?.session === session;
    const gap = sameSession ? frameTime - candidate.time : Infinity;
    const missedHomeRead = kind === 'home' && candidate?.kind === 'home' && gap < 5000;
    const missedMatchRead = (kind === 'match' || kind === 'none') && candidate?.kind === 'match' && gap < 750;
    if (!missedHomeRead && !missedMatchRead) candidate = null;
    return;
  }
  const repeat = candidate && candidate.kind === kind && candidate.value === value &&
    candidate.session === session && candidate.layout === layout && frameTime > candidate.time && frameTime - candidate.time < (kind === 'home' ? 5000 : 2500);
  const frames = repeat ? candidate.frames + 1 : 1;
  const firstTime = repeat ? candidate.firstTime : frameTime;
  const strongReads = (repeat ? candidate.strongReads : 0) + (corroborated ? 1 : 0);
  candidate = { kind, value, session, time: frameTime, firstTime, frames, strongReads, layout };
  const lobbyConfirmed = kind === 'home' && layout === 'lobby' &&
    confirmLobbyCorrection(value, session, frameTime, corroborated, labelledCrop);
  // A normal lobby increase already has two OCR reads from separate crops.
  // Keep multi-frame confirmation for corrections, large jumps and match reads.
  const quickIncrease = kind === 'home' && layout === 'lobby' && corroborated &&
    state.current > 0 && value > state.current && !isLargeTotalChange(value);
  if (frames < 2 && !quickIncrease && !lobbyConfirmed) return;
  if (kind === 'home') {
    // Large corrections need two independently cropped reads across three frames.
    // Menu totals can correct a decrease after three frames with two corroborated reads.
    const confirmedMenu = (layout === 'events' || layout === 'wager') && !isLargeTotalChange(value) && frames >= 3 && strongReads >= 2;
    const verifiedWager = layout === 'wager' && wagerShape && frames >= 3 && strongReads >= 2;
    if (layout === 'wager' && isLargeTotalChange(value) && !verifiedWager) return;
    if (value < state.current && layout !== 'lobby' && !confirmedMenu && !verifiedWager) return;
    if (isSuspectedFiveDrop(value) && !verifiedWager) return;
    if (isLargeTotalChange(value) &&
        (layout === 'lobby' ? !lobbyConfirmed : frames < 3 || strongReads < 2)) return;
    if (value < state.current && !isLargeTotalChange(value) && frames < 3) return;
    skippedMatchValue = null;
    if (state.current !== value || state.match !== null || state.source !== 'Lobby') {
      if (state.current !== value) lobbyReads = [];
      save({ current: value, match: null, source: 'Lobby' });
    }
  } else if (state.match === null) {
    skippedMatchValue = null;
    save({ match: value, source: 'Match' });
  } else if (value === 0 && state.match > 0 && frames >= 5 && frameTime - firstTime >= 850) {
    // A sustained, visually verified zero marks a fresh match when the lobby was missed.
    skippedMatchValue = null;
    save({ match: 0, source: 'Match' });
  } else if (value === state.match + 1 ||
      (skippedMatchValue?.session === session && value === skippedMatchValue.value + 1 &&
       frameTime - skippedMatchValue.time < 2500)) {
    skippedMatchValue = null;
    save({ current: state.current + 1, match: value, source: 'Match' });
  } else if (value === state.match) {
    skippedMatchValue = null;
  } else if (value > state.match + 1 && frames >= 3) {
    // Award nothing for the jump. Keep the last accepted baseline too, so a
    // confirmed +1 can still continue from either value if the jump was an OCR error.
    skippedMatchValue = { value, session, time: frameTime };
  }
}

async function readHomeTotal(body) {
  const templateTotal = Number.isInteger(body.templateTotal) && body.templateTotal >= 0 &&
    body.templateTotal <= 99999 ? body.templateTotal : null;
  if (templateTotal !== null && body.layout === 'wager') {
    const labelled = /TROPHIES\s+COLLECTE[DO]\b/i.test(await readText(body.label, 7));
    return { value: labelled ? templateTotal : null, corroborated: labelled };
  }
  if (templateTotal !== null && !body.templateNeedsLine && !body.layout && body.label &&
      !isLargeTotalChange(templateTotal) &&
      /\bTROPHIES\s+COLLE[CG]TE[DS]\b/i.test(await readText(body.label, 7))) {
    return { value: templateTotal, corroborated: true };
  }
  // The lobby line and the isolated number can confirm the total with just two OCR jobs.
  let lobby = await readLobbyLine(body.lobbyLine, body.rawTotal, body.foregroundTotal, templateTotal);
  if (lobby === null && body.alternateLine && !body.layout) {
    lobby = await readLobbyLine(body.alternateLine, body.rawTotal, body.foregroundTotal, templateTotal);
  }
  if (lobby !== null && (lobby === templateTotal || !isLargeTotalChange(lobby))) {
    return { value: lobby, corroborated: true };
  }
  if (!body.label) return { value: lobby, corroborated: lobby !== null };

  const pair = body.primary ?? body;
  const retryMode = !body.layout && pair.glyphs >= 4 ? 7 : undefined;
  const [label, primary] = await Promise.all([
    readText(body.label, 7), readTotal(pair, 8, retryMode, body.rawTotal)
  ]);
  const labelPattern = body.layout === 'events' ? /\bEVENTS\b/i
    : body.layout === 'wager' ? /TROPHIES\s+COLLECTE[DO]\b/i
    : /TROPHIES\s+COLLE[CG]TE[DS]\b/i;
  if (!labelPattern.test(label)) return { value: lobby, corroborated: lobby !== null };
  // A large lobby change needs agreement between the shape match and numeric OCR
  // before it can outrank the ordinary lobby reading.
  if (!body.layout && !body.templateNeedsLine && templateTotal !== null && templateTotal === primary) {
    return { value: primary, corroborated: true };
  }
  if (!body.layout && (lobby !== null || primary === null || isLargeTotalChange(primary))) {
    const [alternate, foreground] = await Promise.all([
      readTotal(body.alternate, 7, retryMode),
      body.foregroundTotal ? readText(body.foregroundTotal, 7, true) : Promise.resolve('')
    ]);
    const clean = /^(0|[1-9]\d{0,5})$/.test(foreground) ? Number(foreground) : null;
    if (clean !== null && (clean === primary || clean === alternate) && !isLargeTotalChange(clean))
      return { value: clean, corroborated: true };
    if (primary !== null && primary === alternate && !isLargeTotalChange(primary))
      return { value: primary, corroborated: true };
    const value = lobby ?? primary ?? alternate;
    if (templateTotal !== null && value !== null && value > state.current &&
        isLargeTotalChange(value) && templateTotal !== value && value !== clean) {
      return { value, corroborated: false };
    }
    return { value, corroborated: value !== null &&
      (value === clean || (value === alternate && (value === primary || value === lobby)) ||
        (value === primary && value === lobby)) };
  }
  if (primary !== null && (primary === templateTotal || (primary >= state.current && !isLargeTotalChange(primary)))) {
    return { value: primary, corroborated: primary === templateTotal };
  }

  const [alternate, raw] = await Promise.all([
    readTotal(body.alternate, body.layout === 'wager' ? 8 : 7, retryMode),
    body.layout === 'events' && body.rawTotal ? readText(body.rawTotal, 7, true) : Promise.resolve('')
  ]);
  const value = primary ?? alternate;
  const originalAgrees = /^(0|[1-9]\d{0,5})$/.test(raw) && Number(raw) === value;
  return { value, corroborated: originalAgrees || (primary !== null && primary === alternate) };
}

const files = {
  '/': ['control.html', 'text/html; charset=utf-8'],
  '/capture.js': ['capture.js', 'text/javascript; charset=utf-8'],
  '/recognition.js': ['recognition.js', 'text/javascript; charset=utf-8'],
  '/overlay': ['overlay.html', 'text/html; charset=utf-8'],
  '/signatures.json': ['signatures.json', 'application/json']
};

function assetVersion(...names) {
  const version = createHash('sha256');
  for (const name of names) version.update(fs.readFileSync(path.join(__dirname, name)));
  return version.digest('hex');
}

async function readBody(request) {
  let body = '';
  for await (const chunk of request) {
    body += chunk;
    // A sample can contain several base64 PNG crops, including the full lobby label.
    if (body.length > 4 * 1024 * 1024) throw new Error('Capture crop is too large.');
  }
  return JSON.parse(body);
}

let ocrInProgress = false;
let correctionRevision = 0;
const server = http.createServer(async (request, response) => {
  function json(value, status = 200) {
    response.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
    response.end(JSON.stringify(value));
  }
  try {
    if (request.headers.host !== '127.0.0.1:47851') return json({ error: 'Invalid host.' }, 403);
    if (request.method === 'GET') {
      const page = request.url.split('?')[0];
      if (page === '/api/instance') return json(process.env.TROPHY_INSTANCE);
      if (page === '/api/state') return json(publicState());
      if (page === '/api/version') return json({
        recognition: assetVersion('recognition.js', 'signatures.json'),
        overlay: assetVersion('overlay.html')
      });
      const file = files[page];
      if (!file) return json({ error: 'Page missing.' }, 404);
      let content = fs.readFileSync(path.join(__dirname, file[0]));
      if (page === '/overlay') {
        const version = createHash('sha256').update(content).digest('hex');
        content = content.toString('utf8').replace('__OVERLAY_VERSION__', version);
      }
      response.writeHead(200, { 'Content-Type': file[1], 'Cache-Control': 'no-store' });
      return response.end(content);
    }
    if (request.method !== 'POST' || request.headers.origin !== origin) {
      return json({ error: 'Open the tracker from its local control page.' }, 403);
    }
    const body = await readBody(request);
    if (request.url === '/api/target') {
      if (!Number.isSafeInteger(body.target) || body.target < 1) return json({ error: 'Enter a positive whole number.' }, 400);
      const day = londonDay();
      save({ target: body.target, targetOverrideDay: day >= firstScheduledDay ? day : null });
      return json(publicState(day));
    }
    if (request.url === '/api/current') {
      if (!Number.isSafeInteger(body.current) || body.current < 0 || body.current > 99999) {
        return json({ error: 'Enter a trophy total from 0 to 99999.' }, 400);
      }
      candidate = null;
      lobbyReads = [];
      skippedMatchValue = null;
      save({ current: body.current, match: null, source: 'Manual correction' });
      correctionRevision++;
      return json(publicState());
    }
    if (request.url !== '/api/sample') return json({ error: 'Page missing.' }, 404);
    if (ocrInProgress) return json({ state: publicState(), reading: 'OCR busy' });
    ocrInProgress = true;
    const revision = correctionRevision;
    try {
      let value = null;
      let corroborated = false;
      let labelledCrop = null;
      if (body.kind === 'home') {
        ({ value, corroborated } = await readHomeTotal(body));
        if (!body.layout && value !== null && isLargeTotalChange(value) &&
            value !== body.templateTotal) {
          if (corroborated && !body.label && body.lobbyLine && body.rawTotal) {
            labelledCrop = createHash('sha256').update(body.lobbyLine).digest('hex');
          }
          corroborated = false;
        }
        if (value === null && !body.label) body.kind = 'none';
      }
      if (body.kind === 'match') {
        if (body.zero === true) value = 0;
        else if (body.digits) {
          let digits = await readText(body.digits, 7, true);
          if (!/^[1-9]\d{0,2}$/.test(digits) && body.mask) digits = await readText(body.mask, 7, true);
          if (/^[1-9]\d{0,2}$/.test(digits)) value = Number(digits);
        }
      }
      if (revision !== correctionRevision) return json({ state: publicState(), reading: 'Total corrected' });
      observe(body.kind, value, body.session, body.frameTime, corroborated,
        body.kind === 'home' ? body.layout ?? 'lobby' : null, labelledCrop, body.layout === 'wager' && corroborated && value === body.templateTotal);
      const source = body.layout === 'wager' ? 'Wager' : body.layout === 'events' ? 'Events' : body.kind === 'home' ? 'Lobby' : 'Match';
      const pending = body.kind === 'home' && value !== null && value !== state.current;
      return json({ state: publicState(), reading: value === null ? 'Looking for the trophy counter' :
        pending ? `Checking ${source.toLowerCase()} total: ${value}` : `${source} read: ${value}` });
    } finally {
      ocrInProgress = false;
    }
  } catch (error) {
    console.error(error.message);
    return json({ error: error.code === 'ENOENT' ? 'Tesseract was not found. Check its installation path.' : error.message }, 500);
  }
});

server.on('error', error => {
  console.error(error.code === 'EADDRINUSE' ? `The tracker is already running, or port 47851 is occupied. Try ${origin}` : error.message);
  process.exitCode = 1;
});
server.listen(47851, '127.0.0.1', () => {
  console.log(`Trophy Tracker is ready at ${origin}`);
});
