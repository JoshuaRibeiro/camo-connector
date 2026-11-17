const api = globalThis.browser ?? globalThis.chrome;
const form = document.querySelector('form');
const input = document.querySelector('#key');
const status = document.querySelector('#status');
const controls = [...document.querySelectorAll('button')];

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
