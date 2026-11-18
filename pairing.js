const api = globalThis.browser ?? globalThis.chrome;
const form = document.querySelector('form');
const input = document.querySelector('#key');
const status = document.querySelector('#status');
const controls = [...document.querySelectorAll('button:not(#check-update)')];

async function send(message) {
  controls.forEach(button => { button.disabled = true; });
  try {
    const result = await api.runtime.sendMessage(message);
    status.textContent = result?.error || (result?.connected ? 'Connected' : 'Pairing required');
    if (result?.connected || message.type === 'disconnect') input.value = '';
  } catch { status.textContent = 'The extension could not check the connection.'; }
  finally { controls.forEach(button => { button.disabled = false; }); }
}

form.addEventListener('submit', event => {
  event.preventDefault();
  void send({ type: 'pair', key: input.value.trim().toLowerCase() });
});
document.querySelector('#disconnect').addEventListener('click', () => void send({ type: 'disconnect' }));
document.querySelector('#auto-pair').addEventListener('click', () => {
  status.textContent = 'Waiting for desktop approval…';
  void send({ type: 'auto-pair' });
});
void send({ type: 'status' });

const currentVersion = api.runtime.getManifest().version;
const updatesButton = document.querySelector('#check-update');
const updateStatus = document.querySelector('#update-status');
const updateRelease = document.querySelector('#update-release');
const repository = 'JoshuaRibeiro/camo-connector';
document.querySelector('#extension-version').textContent = 'Extension v' + currentVersion;

function compareVersions(left, right) {
  const a = left.split('.').map(Number);
  const b = right.split('.').map(Number);
  return a[0] - b[0] || a[1] - b[1] || a[2] - b[2];
}

updatesButton.addEventListener('click', async () => {
  updatesButton.disabled = true;
  updateRelease.hidden = true;
  updateStatus.textContent = 'Checking GitHub for extension updates.';
  try {
    if (!await api.permissions.request({ origins: ['https://api.github.com/*'] })) {
      updateStatus.textContent = 'Allow GitHub access to check for updates.';
      return;
    }
    let versions = [];
    for (let page = 1; versions.length === 0; page++) {
      const response = await fetch('https://api.github.com/repos/' + repository +
        '/releases?per_page=100&page=' + page, { credentials: 'omit', signal: AbortSignal.timeout(10000) });
      if (!response.ok) throw new Error('GitHub request failed');
      const releases = await response.json();
      if (!Array.isArray(releases)) throw new Error('Invalid GitHub response');
      versions = releases.filter(release => release.draft === false)
        .map(release => /^extension-v(\d+\.\d+\.\d+)$/.exec(release.tag_name)?.[1]).filter(Boolean);
      if (releases.length < 100) break;
    }
    const latest = versions.sort((a, b) => compareVersions(b, a))[0];
    if (!latest) {
      updateStatus.textContent = 'No extension releases found.';
    } else if (compareVersions(latest, currentVersion) <= 0) {
      updateStatus.textContent = 'Extension is up to date.';
    } else {
      updateStatus.textContent = 'Extension v' + latest + ' is available.';
      updateRelease.href = 'https://github.com/' + repository + '/releases/tag/extension-v' + latest;
      updateRelease.hidden = false;
    }
  } catch {
    updateStatus.textContent = 'Could not check GitHub. Try again later.';
  } finally {
    updatesButton.disabled = false;
  }
});
