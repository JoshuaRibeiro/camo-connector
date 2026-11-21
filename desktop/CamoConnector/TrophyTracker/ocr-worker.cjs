'use strict';
const { spawn } = require('node:child_process');
const { createInterface } = require('node:readline');

// A single warm engine processes this capture stream. Line-delimited replies
// are consumed in request order, including when several crops arrive together.
module.exports = function createOcrWorker(executable, runtime, fallback) {
  let child, pending, retryAt = 0;
  let queue = Promise.resolve();

  function stop(worker, error) {
    if (child !== worker) return;
    child = undefined;
    retryAt = Date.now() + 15000;
    if (pending) {
      clearTimeout(pending.timer);
      pending.reject(error);
      pending = undefined;
    }
    worker.kill();
  }

  function start() {
    const worker = spawn(executable, ['--ocr-worker', runtime], {
      windowsHide: true, stdio: ['pipe', 'pipe', 'ignore'],
      env: { ...process.env, OMP_THREAD_LIMIT: '1' }
    });
    child = worker;
    worker.on('error', error => stop(worker, error));
    worker.stdin.on('error', error => stop(worker, error));
    worker.once('exit', () => stop(worker, new Error('OCR worker exited')));
    createInterface({ input: worker.stdout }).on('line', line => {
      if (child !== worker || !pending) return;
      const request = pending;
      clearTimeout(request.timer);
      pending = undefined;
      try {
        const text = JSON.parse(line);
        if (typeof text !== 'string') throw new Error('Invalid OCR response');
        request.resolve(text);
      } catch (error) { request.reject(error); }
    });
    return worker;
  }

  return function read(image, mode, digits) {
    const run = async () => {
      if (Date.now() >= retryAt) {
        try {
          const worker = child ?? start();
          return await new Promise((resolve, reject) => {
            const timer = setTimeout(() => stop(worker, new Error('OCR worker timed out')), 3500);
            pending = { resolve, reject, timer };
            worker.stdin.write(JSON.stringify({ image, mode, digits }) + '\n');
          });
        } catch { /* Use the packaged CLI for this crop if the worker fails. */ }
      }
      return fallback(image, mode, digits);
    };
    const result = queue.then(run);
    queue = result.catch(() => {});
    return result;
  };
};
