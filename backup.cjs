const fs = require('node:fs');
const path = require('node:path');
const { randomUUID } = require('node:crypto');

function valid(backup) {
  const map = value => value && typeof value === 'object' && !Array.isArray(value);
  return backup?.v === 1 && backup.mode === 'mp' && Number.isFinite(backup.updatedAt) &&
    map(backup.counts) && Object.values(backup.counts).every(n => Number.isSafeInteger(n) && n >= 0) &&
    map(backup.done) && Object.values(backup.done).every(flag => typeof flag === 'boolean');
}

function backupHandler(log) {
  const file = path.join(path.dirname(path.resolve(log)), 'mw4-camos.latest.json');
  let writes = Promise.resolve();
  const read = async () => {
    try {
      const backup = JSON.parse(await fs.promises.readFile(file, 'utf8'));
      if (!valid(backup)) throw Error('Backup file is invalid');
      return backup;
    } catch (error) {
      if (error.code === 'ENOENT') return null;
      throw error;
    }
  };

  async function save(next) {
    const previous = await read();
    const populated = Object.values(next.counts).some(n => n > 0) ||
      Object.values(next.done).some(Boolean);
    const regressed = previous && (next.updatedAt < previous.updatedAt ||
      Object.entries(previous.counts).some(([key, count]) => count > (next.counts[key] ?? 0)) ||
      Object.entries(previous.done).some(([key, done]) => done && next.done[key] !== true));
    if (!populated || regressed) return { status: 'preserved', updatedAt: previous?.updatedAt };
    if (JSON.stringify(previous) === JSON.stringify(next)) return { status: 'saved', updatedAt: next.updatedAt };
    const temporary = file + '.' + randomUUID() + '.tmp';
    try {
      await fs.promises.writeFile(temporary, JSON.stringify(next, null, 2), { flag: 'wx' });
      await fs.promises.rename(temporary, file);
    } finally {
      await fs.promises.rm(temporary, { force: true });
    }
    return { status: 'saved', updatedAt: next.updatedAt };
  }

  return async (request, response) => {
    const reply = (status, body) => {
      response.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
      response.end(JSON.stringify(body));
    };
    if (request.headers['x-mw4-connector'] !== 'backup-v1') return reply(403, {});
    try {
      if (request.method === 'GET') return reply(200, { backup: await read() });
      if (request.method !== 'POST') return reply(405, {});
      let size = 0;
      const chunks = [];
      for await (const chunk of request) {
        size += chunk.length;
        if (size <= 1024 * 1024) chunks.push(chunk);
      }
      if (size > 1024 * 1024) return reply(413, {});
      let backup;
      try { backup = JSON.parse(Buffer.concat(chunks).toString('utf8')); } catch { return reply(400, {}); }
      if (!valid(backup)) return reply(400, {});
      const operation = writes.then(() => save(backup));
      writes = operation.catch(() => {});
      reply(200, await operation);
    } catch {
      reply(500, { error: 'Could not access the local backup file' });
    }
  };
}

module.exports = backupHandler;
