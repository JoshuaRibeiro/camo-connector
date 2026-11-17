chrome.runtime.onMessage.addListener((message, sender, reply) => {
  if (message?.type === 'mw4-focus' && sender.url?.startsWith('https://mw4.darkmattersolutions.uk/')) {
    if (typeof message.weapon !== 'string' || !/^[a-z0-9 .-]{2,40}$/i.test(message.weapon)) return;
    fetch('http://127.0.0.1:47150/focus?' + new URLSearchParams({ weapon: message.weapon }), {
      method: 'POST', headers: { 'X-MW4-Connector': 'focus-v1' }, signal: AbortSignal.timeout(2000)
    }).then(response => reply(response.ok), () => reply(false));
    return true;
  }
  if (message !== 'sunburn-events' ||
      !sender.url?.startsWith('https://mw4.darkmattersolutions.uk/')) return;
  fetch('http://127.0.0.1:47150/events', { cache: 'no-store', signal: AbortSignal.timeout(2500) })
    .then(response => {
      if (!response.ok) throw Error('Local bridge unavailable');
      return response.json();
    })
    .then(events => reply({ events }), () => reply({ error: 'Waiting for local bridge' }));
  return true;
});

chrome.runtime.onMessage.addListener((message, sender, reply) => {
  if (!sender.url?.startsWith('https://mw4.darkmattersolutions.uk/') ||
      !['mw4-save-backup', 'mw4-read-backup'].includes(message?.type)) return;
  const save = message.type === 'mw4-save-backup';
  fetch('http://127.0.0.1:47150/backup', {
    method: save ? 'POST' : 'GET',
    headers: { 'X-MW4-Connector': 'backup-v1', 'Content-Type': 'application/json' },
    body: save ? JSON.stringify(message.backup) : undefined,
    cache: 'no-store',
    signal: AbortSignal.timeout(2500)
  }).then(response => {
    if (!response.ok) throw Error('Local backup unavailable');
    return response.json();
  }).then(reply, () => reply({ error: 'Local backup unavailable' }));
  return true;
});
