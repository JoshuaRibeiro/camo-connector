const camoTrackerHost = (() => {
  const prefix = 'camo-connector:';
  const webview = window.chrome.webview;
  let events;
  let poll;
  let waits = [];
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
    events = data.events;
    const now = performance.now();
    waits = waits.filter(wait => {
      if (wait.until > now) return true;
      wait.resolve();
      return false;
    });
    void poll?.();
  });
  return {
    extension: {
      storage: { local: storage },
      runtime: {
        async sendMessage(message) {
          if (message === 'sunburn-events') return { events, error: 'Waiting for local bridge' };
          if (message?.type === 'mw4-focus') webview.postMessage(message);
        }
      }
    },
    subscribe(callback) { poll = callback; },
    pause(ms) {
      return new Promise(resolve => waits.push({ resolve, until: performance.now() + ms }));
    }
  };
})();
