(() => {
  const extension = globalThis.browser ?? globalThis.chrome;
  const storageKey = 'mw4-camo-progress';
  let lastBackup;
  let saving = false;
  let restoring = false;

  async function request(message) {
    const result = await extension.runtime.sendMessage(message);
    if (!result || result.error) throw Error(result?.error || 'Local backup unavailable');
    return result;
  }

  async function capture() {
    if (saving || restoring) return;
    let raw;
    try { raw = localStorage.getItem(storageKey); } catch { return; }
    if (!raw || raw === lastBackup) return;
    saving = true;
    try {
      const data = JSON.parse(raw);
      if (data?.v !== 1 || data.mode !== 'mp') return;
      const { v, mode, counts, done, custom, focus, levels, track, player, updatedAt } = data;
      const result = await request({ type: 'mw4-save-backup',
        backup: { v, mode, counts, done, custom, focus, levels, track, player, updatedAt } });
      lastBackup = raw;
      status.textContent = result.status === 'saved' ? 'Local backup saved' :
        result.updatedAt ? 'Earlier backup preserved' : 'Waiting for Multiplayer progress';
    } catch {
      status.textContent = 'Local backup unavailable';
    } finally {
      saving = false;
    }
  }

  const status = document.createElement('span');
  status.setAttribute('role', 'status');
  status.style.marginInlineStart = '0.5em';

  const restore = document.createElement('button');
  restore.type = 'button';
  restore.className = 'btn';
  restore.textContent = 'Restore latest local backup';
  restore.addEventListener('click', async () => {
    if (saving || restoring) return;
    restoring = true;
    restore.disabled = true;
    try {
      const { backup } = await request({ type: 'mw4-read-backup' });
      if (!backup) throw Error('No local backup saved yet');
      const when = new Date(backup.updatedAt).toLocaleString();
      if (!window.confirm('Restore Multiplayer progress from ' + when + '? This replaces current Multiplayer progress.')) return;
      const input = restoreInput();
      if (!input) throw Error('Open Backup in Overlay & sync to restore');
      const transfer = new DataTransfer();
      transfer.items.add(new File([JSON.stringify(backup)], 'mw4-camos.latest.json', { type: 'application/json' }));
      input.files = transfer.files;
      status.textContent = 'Backup submitted to the tracker. Check the restored progress.';
      input.dispatchEvent(new Event('change', { bubbles: true }));
    } catch (error) {
      status.textContent = error.message;
    } finally {
      restoring = false;
      restore.disabled = false;
    }
  });

  function restoreInput() {
    if (location.pathname !== '/settings') return null;
    const inputs = [...document.querySelectorAll('input[type="file"]')].filter(input =>
      /Restore from file/i.test(input.labels?.[0]?.textContent ?? input.parentElement?.textContent ?? ''));
    return inputs.length === 1 ? inputs[0] : null;
  }

  function refresh() {
    const input = restoreInput();
    const label = input?.labels?.[0] ?? input?.parentElement;
    if (label && !restore.isConnected) label.after(restore, status);
    void capture();
  }

  refresh();
  setInterval(refresh, 2000);
})();

