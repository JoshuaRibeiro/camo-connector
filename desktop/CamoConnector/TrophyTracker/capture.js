'use strict';
const get = id => document.getElementById(id);
const crop = document.createElement('canvas');
const context = crop.getContext('2d', { willReadFrequently: true });
let recognize;
let recognitionVersion;
let recognitionUpdate;
let nextVersionCheck = 0;

async function api(url, body) {
  const response = await fetch(url, body === undefined ? undefined : {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body)
  });
  const answer = await response.json();
  if (!response.ok) throw new Error(answer.error);
  return answer;
}

function updateRecognition() {
  nextVersionCheck = performance.now() + 1500;
  recognitionUpdate ??= (async () => {
    const { recognition } = await api('/api/version');
    if (recognition === recognitionVersion) return;
    const version = encodeURIComponent(recognition);
    const [{ createRecognizer }, signatures] = await Promise.all([
      import('/recognition.js?v=' + version),
      api('/signatures.json?v=' + version)
    ]);
    recognize = createRecognizer({ draw, context, crop, signatures });
    recognitionVersion = recognition;
  })().finally(() => { recognitionUpdate = undefined; });
  return recognitionUpdate;
}

let captureFrame;
function draw(x, y, width, height, outputWidth, outputHeight) {
  crop.width = outputWidth;
  crop.height = outputHeight;
  const sx = captureFrame.width / 2560;
  const sy = captureFrame.height / 1440;
  context.imageSmoothingQuality = 'high';
  context.drawImage(captureFrame, x * sx, y * sy, width * sx, height * sy, 0, 0, outputWidth, outputHeight);
}

function show(state) {
  get('current').textContent = state.current;
  get('goal').textContent = state.target;
  get('source').textContent = state.source;
  const target = get('target');
  const followingSavedTarget = target.value === target.defaultValue || target.valueAsNumber === state.target;
  if (followingSavedTarget) target.value = state.target;
  target.defaultValue = state.target;
}

let busy = false;
let lastFrameAt = 0;
let lastResponseAt = 0;
let session = crypto.randomUUID();
window.chrome?.webview?.addEventListener('message', async ({ data }) => {
  if (busy || typeof data !== 'string') return;
  busy = true;
  const now = performance.now();
  if (now - lastResponseAt > 2500) session = crypto.randomUUID();
  lastFrameAt = now;
  try {
    if (!recognize || now >= nextVersionCheck) await updateRecognition();
    const bytes = Uint8Array.from(atob(data), character => character.charCodeAt(0));
    captureFrame = await createImageBitmap(new Blob([bytes], { type: 'image/jpeg' }));
    let reading;
    try {
      reading = recognize();
    } finally {
      captureFrame.close();
      captureFrame = undefined;
    }
    const result = await api('/api/sample', {
      ...reading, session: session + ':' + recognitionVersion, frameTime: now
    });
    lastResponseAt = performance.now();
    show(result.state);
    get('status').textContent = result.reading;
  } catch (error) {
    get('status').textContent = error.message;
  } finally {
    busy = false;
    window.chrome.webview.postMessage('ready');
  }
});

get('settings').onsubmit = async event => {
  event.preventDefault();
  try {
    show(await api('/api/target', { target: get('target').valueAsNumber }));
    get('save-status').textContent = 'Target saved';
  } catch (error) {
    get('save-status').textContent = error.message;
  }
};

get('copy').onclick = async () => {
  try {
    await navigator.clipboard.writeText(get('url').value);
    get('copy').textContent = 'Copied';
  } catch {
    get('url').select();
  }
};

async function refresh() {
  if (!busy && performance.now() - lastFrameAt > 2000) {
    try {
      show(await api('/api/state'));
      if (!busy) get('status').textContent = 'Waiting for the selected capture source';
    } catch (error) {
      if (!busy) get('status').textContent = error.message;
    }
  }
  setTimeout(refresh, 1500);
}
void refresh();
window.chrome?.webview?.postMessage('ready');
