// Host shim that runs one assembled test262 test inside Node, as a black box.
//
// Input (JSON on stdin):
//   { file, module, parseOnly, async, scripts: [{name, source}], source }
// Output (one JSON line on stdout): { ok: true } or
//   { ok: false, phase: "parse" | "resolution" | "runtime" | "async", name, message }
//
// The test runs in a fresh vm context so every test gets its own realm, and `$262` is built
// from the pieces test262 asks hosts to provide (createRealm, evalScript, detachArrayBuffer,
// gc, agent). IsHTMLDDA cannot be provided from JavaScript, so the runner skips those tests.
'use strict';

const fs = require('fs');
const path = require('path');
const vm = require('vm');
const wt = require('worker_threads');

const SAB_CONTROL_SLOTS = 4;

function describe(e) {
  let name = 'Error';
  let message = '';
  try {
    if (e !== null && (typeof e === 'object' || typeof e === 'function')) {
      name = (e.constructor && e.constructor.name) || e.name || 'Object';
      message = String(e.message);
    } else {
      name = typeof e;
      message = String(e);
    }
  } catch (inner) {
    message = '<unprintable>';
  }
  return { name, message };
}

const detachTransfer = ArrayBuffer.prototype.transfer;

function makeAgentApi() {
  const workers = [];
  const reports = [];
  const control = new Int32Array(new SharedArrayBuffer(4 * SAB_CONTROL_SLOTS));
  // control[0]: number of agents that have taken the current broadcast.
  const reportPorts = [];
  const workerSource = `
    const wt = require('worker_threads');
    const vm = require('vm');
    const { source, control, reportPort } = wt.workerData;
    let callback = null;
    const start = Date.now();
    const agent = {
      receiveBroadcast(cb) { callback = cb; },
      report(v) { reportPort.postMessage(String(v)); },
      sleep(ms) { Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms); },
      leaving() {},
      monotonicNow() { return performance.now(); },
    };
    globalThis.$262 = { agent };
    wt.parentPort.on('message', (m) => {
      // Acknowledge receipt first: the callback may block (Atomics.wait) and the broadcaster
      // is waiting for every agent to have taken the message.
      Atomics.add(control, 0, 1);
      Atomics.notify(control, 0);
      if (callback) {
        const cb = callback;
        cb(m.sab, m.id);
      }
    });
    vm.runInThisContext(source);
  `;
  return {
    start(src) {
      const { port1: reportPort, port2: reportPortChild } = new wt.MessageChannel();
      reportPorts.push(reportPort);
      const w = new wt.Worker(workerSource, {
        eval: true,
        workerData: { source: String(src), control, reportPort: reportPortChild },
        transferList: [reportPortChild],
      });
      w.unref();
      workers.push(w);
      // Give the agent a moment to run its script up to receiveBroadcast.
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 100);
    },
    broadcast(sab, id) {
      Atomics.store(control, 0, 0);
      for (const w of workers) w.postMessage({ sab, id });
      const deadline = Date.now() + 10000;
      for (;;) {
        const taken = Atomics.load(control, 0);
        if (taken >= workers.length) break;
        if (Date.now() > deadline) throw new Error('agent broadcast timed out');
        Atomics.wait(control, 0, taken, 50);
      }
    },
    getReport() {
      for (const port of reportPorts) {
        const m = wt.receiveMessageOnPort(port);
        if (m !== undefined) return m.message;
      }
      return null;
    },
    sleep(ms) {
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
    },
    monotonicNow() {
      return performance.now();
    },
  };
}

function main() {
  const req = JSON.parse(fs.readFileSync(0, 'utf8'));
  const baseDir = path.dirname(req.file);
  let finished = false;
  let asyncPending = false;

  function finish(result) {
    if (finished) return;
    finished = true;
    process.stdout.write(JSON.stringify(result) + '\n', () => process.exit(0));
  }

  const moduleCache = new Map();
  const readiness = new Map();

  function newContext() {
    const ctx = vm.createContext({});
    vm.runInContext('0', ctx);
    const g = vm.runInContext('globalThis', ctx);
    Object.defineProperty(g, 'print', {
      value: function print(msg) {
        const s = String(msg);
        if (!asyncPending) return;
        if (s.startsWith('Test262:AsyncTestFailure:')) {
          finish({ ok: false, phase: 'async', name: 'Test262Error', message: s.slice('Test262:AsyncTestFailure:'.length) });
        } else if (s === 'Test262:AsyncTestComplete') {
          finish({ ok: true });
        } else {
          finish({ ok: false, phase: 'async', name: 'Test262Error', message: 'unexpected print: ' + s });
        }
      },
      writable: true, configurable: true, enumerable: false,
    });
    const api = {
      global: g,
      createRealm() { return newContext(); },
      evalScript(src) {
        const script = new vm.Script(String(src), { importModuleDynamically });
        return script.runInContext(ctx);
      },
      // Detaching an already detached buffer is a no-op for hosts; transfer would throw.
      detachArrayBuffer(buf) { if (!buf.detached) detachTransfer.call(buf); },
      gc() { if (typeof globalThis.gc === 'function') globalThis.gc(); else throw new Error('gc unavailable'); },
      agent: makeAgentApi(),
    };
    Object.defineProperty(g, '$262', { value: api, writable: true, configurable: true, enumerable: false });
    contexts.set(ctx, api);
    return api;
  }

  const contexts = new Map();
  const rootApi = newContext();
  const rootCtx = [...contexts.keys()][0];

  function loadModule(ctx, file, text) {
    const key = file;
    if (moduleCache.has(key)) return moduleCache.get(key);
    let mod;
    if (file.endsWith('.json')) {
      const value = vm.runInContext('JSON.parse', ctx)(text);
      mod = new vm.SyntheticModule(['default'], function () { this.setExport('default', value); }, { context: ctx, identifier: file });
    } else {
      mod = new vm.SourceTextModule(text, { context: ctx, identifier: file, importModuleDynamically });
    }
    moduleCache.set(key, mod);
    return mod;
  }

  function linker(ctx) {
    return function link(specifier, referencing) {
      const from = path.dirname(referencing.identifier);
      const file = path.resolve(from, specifier);
      const text = fs.readFileSync(file, 'utf8');
      return loadModule(ctx, file, text);
    };
  }

  async function importModuleDynamically(specifier, referrer) {
    const ctx = referrer && referrer.context ? referrer.context : rootCtx;
    let from = baseDir;
    if (referrer && typeof referrer.identifier === 'string') from = path.dirname(referrer.identifier);
    else if (referrer && typeof referrer.filename === 'string') from = path.dirname(referrer.filename);
    const file = path.resolve(from, specifier);
    const text = fs.readFileSync(file, 'utf8');
    const mod = loadModule(ctx, file, text);
    // Concurrent imports of one module must share a single link-and-evaluate pass.
    if (!readiness.has(mod)) {
      readiness.set(mod, (async () => {
        if (mod.status === 'unlinked') await mod.link(linker(ctx));
        if (mod.status === 'linked') await mod.evaluate();
        else if (mod.status === 'errored') throw mod.error;
      })());
    }
    await readiness.get(mod);
    return mod;
  }

  function fail(phase, e) {
    const d = describe(e);
    finish({ ok: false, phase, name: d.name, message: d.message });
  }

  // Harness scripts run first, in the test realm's global scope.
  if (!req.parseOnly) {
    for (const s of req.scripts) {
      try {
        new vm.Script(s.source, { filename: s.name, importModuleDynamically }).runInContext(rootCtx);
      } catch (e) {
        return fail('harness', e);
      }
    }
  }
  asyncPending = !!req.async && !req.parseOnly;

  if (req.module) {
    let mod;
    try {
      mod = new vm.SourceTextModule(req.source, { context: rootCtx, identifier: req.file, importModuleDynamically });
      moduleCache.set(req.file, mod);
    } catch (e) {
      return fail('parse', e);
    }
    if (req.parseOnly) return finish({ ok: true });
    // A module whose top-level await never settles leaves the event loop empty.
    process.on('beforeExit', () => finish({ ok: false, phase: 'runtime', name: 'Test262Error', message: 'module evaluation never completed' }));
    mod.link(linker(rootCtx)).then(
      () => mod.evaluate().then(
        () => { if (!asyncPending) finish({ ok: true }); },
        (e) => fail('runtime', e)),
      (e) => fail('resolution', e));
    return undefined;
  }

  let script;
  try {
    script = new vm.Script(req.source, { filename: req.file, importModuleDynamically });
  } catch (e) {
    return fail('parse', e);
  }
  if (req.parseOnly) return finish({ ok: true });
  try {
    script.runInContext(rootCtx);
  } catch (e) {
    return fail('runtime', e);
  }
  if (!asyncPending) {
    // Let already-queued microtasks and immediately scheduled work finish before exiting.
    setImmediate(() => finish({ ok: true }));
  } else {
    // Async test: a print() call finishes it; if the event loop drains first, it never reported.
    process.on('beforeExit', () => finish({ ok: false, phase: 'async', name: 'Test262Error', message: 'async test finished without calling $DONE' }));
  }
  return undefined;
}

process.on('uncaughtException', (e) => {
  const d = describe(e);
  process.stdout.write(JSON.stringify({ ok: false, phase: 'runtime', name: d.name, message: d.message }) + '\n', () => process.exit(0));
});
// Rejected promises nobody handles are legal in test262 tests; they are not failures.
process.on('unhandledRejection', () => {});

main();
