const versionPattern = /^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$/;

function newer(candidate, current) {
  if (!current) return true;
  const left = candidate.split('.').map(Number);
  const right = current.split('.').map(Number);
  const index = left.findIndex((component, i) => component !== right[i]);
  return index >= 0 && left[index] > right[index];
}

async function authorized(request, token) {
  const authorization = request.headers.get('Authorization');
  if (!token || !authorization?.startsWith('Bearer ') || authorization.length > 512) return false;
  const encoder = new TextEncoder();
  const expected = await crypto.subtle.digest('SHA-256', encoder.encode(token));
  const provided = await crypto.subtle.digest('SHA-256', encoder.encode(authorization.slice(7)));
  const a = new Uint8Array(expected);
  const b = new Uint8Array(provided);
  let mismatch = 0;
  for (let i = 0; i < a.length; i++) mismatch |= a[i] ^ b[i];
  return mismatch === 0;
}

export default {
  async fetch(request, env) {
    const { pathname } = new URL(request.url);
    if (pathname === '/release' && request.method === 'POST') {
      if (!await authorized(request, env.RELEASE_TOKEN)) return new Response(null, { status: 401 });
      // Publish the version in a header so the service never has to read a request body.
      if (!versionPattern.test(request.headers.get('X-Camo-Version') ?? ''))
        return new Response(null, { status: 400 });
    } else if (pathname === '/updates' && request.method === 'GET') {
      if (request.headers.get('Upgrade')?.toLowerCase() !== 'websocket')
        return new Response(null, { status: 426 });
    } else return new Response(null, { status: 404 });
    return env.RELEASES.get(env.RELEASES.idFromName('stable')).fetch(request);
  },
};

export class ReleaseNotifications {
  constructor(state) {
    this.state = state;
  }

  async fetch(request) {
    if (request.method === 'POST') {
      const version = request.headers.get('X-Camo-Version');
      await this.state.blockConcurrencyWhile(async () => {
        const previous = await this.state.storage.get('latest');
        if (!newer(version, previous)) return;
        await this.state.storage.put('latest', version);
        for (const socket of this.state.getWebSockets()) {
          try { socket.send(version); }
          catch { socket.close(1011, 'Reconnect for updates'); }
        }
      });
      return new Response(null, { status: 204 });
    }

    const [client, server] = Object.values(new WebSocketPair());
    this.state.acceptWebSocket(server);
    const latest = await this.state.storage.get('latest');
    if (latest) server.send(latest);
    return new Response(null, { status: 101, webSocket: client });
  }

  webSocketMessage(socket) {
    socket.close(1008, 'Read-only connection');
  }

  webSocketClose(socket, code) {
    socket.close(code === 1005 || code === 1006 ? 1000 : code);
  }
}
