const api = globalThis.browser ?? globalThis.chrome;
const trackerOrigin = 'https://mw4.darkmattersolutions.uk';
const bridge = 'http://127.0.0.1:47150';
const pairingKey = 'bridgePairingKey';
const clientHeader = 'bridgeClientOrigin';
let trackerSyncPort = null;

function isTrackerFrame(sender) {
  try { return sender.frameId === 0 && new URL(sender.url).origin === trackerOrigin; }
  catch { return false; }
}

function onTrackerConnect(port) {
  if (port.name !== 'mw4-sync-lock' || !isTrackerFrame(port.sender ?? {})) {
    port.disconnect();
    return;
  }
  if (trackerSyncPort) {
    port.postMessage({ acquired: false });
    return;
  }
  trackerSyncPort = port;
  port.onDisconnect.addListener(() => {
    if (trackerSyncPort === port) trackerSyncPort = null;
  });
  port.postMessage({ acquired: true });
}

async function request(route, key, options = {}, client = null) {
  if (!/^[a-f0-9]{64}$/.test(key || '')) throw Error('Pair Camo Connector using the extension icon.');
  let response;
  try {
    response = await fetch(bridge + route, {
      ...options,
      headers: { ...options.headers, Authorization: 'Bearer ' + key, ...(client && { 'X-Camo-Client': client }) },
      cache: 'no-store', redirect: 'error', signal: AbortSignal.timeout(2500)
    });
  } catch { throw Error('Waiting for the desktop connector.'); }
  if (response.status === 401) throw Error('Pair again with the desktop connector.');
  if (!response.ok) throw Error('The local connector rejected the request.');
  return response.status === 204 ? true : response.json();
}

function onMessage(message, sender, reply) {
  let url;
  try { url = new URL(sender.url); } catch { return false; }
  const pairing = url.href === api.runtime.getURL('pairing.html');
  if (pairing && ['auto-pair', 'pair', 'disconnect', 'status'].includes(message?.type)) {
    runPairing(message).then(reply, error => reply({ error: error.message }));
    return true;
  }
  if (!isTrackerFrame(sender)) return false;
  const type = typeof message === 'string' ? message : message?.type;
  if (!['sunburn-events', 'mw4-focus', 'mw4-save-backup', 'mw4-read-backup'].includes(type)) return false;
  runTrackerRequest(type, message).then(reply, error => reply({ error: error.message }));
  return true;
}

async function runPairing(message) {
  if (message.type === 'disconnect') {
    await api.storage.local.remove([pairingKey, clientHeader]);
    return { connected: false };
  }
  if (message.type === 'auto-pair') {
    const client = api.runtime.getURL('').replace(/\/$/, '');
    let response;
    try {
      response = await fetch(bridge + '/pair', {
        method: 'POST', headers: { 'X-MW4-Connector': 'pair-v1', 'X-Camo-Client': client },
        cache: 'no-store', redirect: 'error', signal: AbortSignal.timeout(25000)
      });
    } catch { throw Error('Start the new desktop app and try again.'); }
    if (response.status === 401 || response.status === 404) throw Error('This desktop build uses a pairing key.');
    if (!response.ok) throw Error('Desktop approval was declined or is already pending.');
    const { token } = await response.json();
    await request('/events', token, {}, client);
    await api.storage.local.set({ [pairingKey]: token, [clientHeader]: client });
    return { connected: true };
  }
  const values = await api.storage.local.get([pairingKey, clientHeader]);
  const saved = values[pairingKey];
  const client = message.type === 'pair' ? null : values[clientHeader];
  if (message.type === 'status' && !saved) return { connected: false };
  const key = message.type === 'pair' ? message.key : saved;
  await request('/events', key, {}, client);
  if (message.type === 'pair') await api.storage.local.set({ [pairingKey]: key, [clientHeader]: null });
  return { connected: true };
}

async function runTrackerRequest(type, message) {
  const values = await api.storage.local.get([pairingKey, clientHeader]);
  const key = values[pairingKey], client = values[clientHeader];
  if (type === 'sunburn-events') return { events: await request('/events', key, {}, client) };
  if (type === 'mw4-focus') {
    if (typeof message.weapon !== 'string' || !/^[a-z0-9 .-]{2,40}$/i.test(message.weapon)) return false;
    return request('/focus?' + new URLSearchParams({ weapon: message.weapon }), key, {
      method: 'POST', headers: { 'X-MW4-Connector': 'focus-v1' }
    }, client);
  }
  const save = type === 'mw4-save-backup';
  return request('/backup', key, {
    method: save ? 'POST' : 'GET',
    headers: { 'X-MW4-Connector': 'backup-v1', 'Content-Type': 'application/json' },
    body: save ? JSON.stringify(message.backup) : undefined
  }, client);
}

api.runtime.onMessage.addListener(onMessage);
api.runtime.onConnect.addListener(onTrackerConnect);
