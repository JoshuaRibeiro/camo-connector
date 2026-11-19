const camoTrackerHost = (() => {
  const prefix = 'camo-connector:';
  const webview = window.chrome.webview;
  let events;
  let poll;
  let lastPollAt = -Infinity;
  let waits = [];
  let nextBackupRequest = 0;
  const backupRequests = new Map();
  const storage = {
    async get(keys) {
      return Object.fromEntries([].concat(keys).map(key => [
        key, JSON.parse(localStorage.getItem(prefix + key))
      ]));
    },
    async set(values) {
      for (const [key, value] of Object.entries(values)) {
        localStorage.setItem(prefix + key, JSON.stringify(value));
      }
    }
  };
  webview.addEventListener('message', ({ data }) => {
    if (data.backupRequestId != null) {
      const finish = backupRequests.get(data.backupRequestId);
      finish?.(data.result);
      return;
    }
    if (data.events) events = data.events;
    const now = performance.now();
    waits = waits.filter(wait => {
      if (wait.until > now) return true;
      wait.resolve();
      return false;
    });
    if (data.events || now - lastPollAt >= 1000) {
      lastPollAt = now;
      void poll?.();
    }
  });
  return {
    extension: {
      storage: { local: storage },
      runtime: {
        async sendMessage(message) {
          if (message === 'sunburn-events') return { events, error: 'Waiting for local bridge' };
          if (message?.type === 'mw4-focus') webview.postMessage(message);
          if (message?.type === 'mw4-read-backup' || message?.type === 'mw4-save-backup') {
            const id = ++nextBackupRequest;
            return new Promise(resolve => {
              const timeout = setTimeout(() => finish({ error: 'Local backup unavailable' }), 3000);
              function finish(result) {
                clearTimeout(timeout);
                backupRequests.delete(id);
                resolve(result);
              }
              backupRequests.set(id, finish);
              try { webview.postMessage({ ...message, backupRequestId: id }); }
              catch { finish({ error: 'Local backup unavailable' }); }
            });
          }
        }
      }
    },
    subscribe(callback) { poll = callback; },
    pause(ms) {
      return new Promise(resolve => waits.push({ resolve, until: performance.now() + ms }));
    }
  };
})();
