import fs from 'node:fs/promises';
import path from 'node:path';
import { spawn } from 'node:child_process';
import readline from 'node:readline';
import { fileURLToPath } from 'node:url';

const VERSION = '0.4.16';
const LOOPBACK_HOSTS = new Set(['127.0.0.1', 'localhost', '[::1]', '::1']);
const ID_PATTERN = /^[A-Za-z0-9._-]{1,200}$/;
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const REFRESH_REQUEST_FILE = process.env.LOCALAPPDATA
  ? path.join(process.env.LOCALAPPDATA, 'CodexUsageBar', 'refresh.request')
  : null;
const ENGINE_LOCALE_DIR = path.join(path.dirname(fileURLToPath(import.meta.url)), 'locales');
const USER_LOCALE_DIR = process.env.LOCALAPPDATA
  ? path.join(process.env.LOCALAPPDATA, 'CodexUsageBar', 'locales')
  : null;
const CURRENT_LOCALE_FILE = process.env.LOCALAPPDATA
  ? path.join(process.env.LOCALAPPDATA, 'CodexUsageBar', 'locale.current')
  : null;
let lastWrittenDocumentLocale = null;

function normalizeLocaleCode(value) {
  return String(value || '').trim().replace(/_/g, '-').toLowerCase();
}

function isPlainObject(value) {
  return Boolean(value) && typeof value === 'object' && !Array.isArray(value);
}

function validateLocaleCatalog(value, filePath) {
  if (!isPlainObject(value)) throw new Error(`Locale catalog must be a JSON object: ${filePath}`);
  return value;
}

async function readLocaleDirectory(directory, catalogs, logger, override = false) {
  if (!directory) return;
  let entries;
  try { entries = await fs.readdir(directory, { withFileTypes: true }); }
  catch (error) {
    if (error?.code === 'ENOENT') return;
    logger?.error?.(`[usage-bar] locale directory read failed (${directory}): ${error.message}`);
    return;
  }
  for (const entry of entries) {
    if (!entry.isFile() || !entry.name.toLowerCase().endsWith('.json')) continue;
    const code = normalizeLocaleCode(entry.name.slice(0, -5));
    if (!code || !/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(code)) continue;
    if (!override && catalogs[code]) continue;
    const filePath = path.join(directory, entry.name);
    try {
      const parsed = JSON.parse(await fs.readFile(filePath, 'utf8'));
      catalogs[code] = validateLocaleCatalog(parsed, filePath);
    } catch (error) {
      logger?.error?.(`[usage-bar] locale catalog ignored (${filePath}): ${error.message}`);
    }
  }
}

async function loadLocaleCatalogs(logger) {
  const catalogs = {};
  await readLocaleDirectory(ENGINE_LOCALE_DIR, catalogs, logger, false);
  await readLocaleDirectory(USER_LOCALE_DIR, catalogs, logger, true);
  if (!catalogs.en || !catalogs.zh) {
    throw new Error('Built-in locale catalogs en.json and zh.json are required');
  }
  return catalogs;
}

async function noteDocumentLocale(session, logger) {
  if (!CURRENT_LOCALE_FILE || session?.closed) return;
  try {
    const raw = String(await session.evaluate(`String(document.documentElement?.lang || '')`) || '').trim();
    if (!raw || raw === lastWrittenDocumentLocale) return;
    await fs.mkdir(path.dirname(CURRENT_LOCALE_FILE), { recursive: true });
    await fs.writeFile(CURRENT_LOCALE_FILE, raw, 'utf8');
    lastWrittenDocumentLocale = raw;
    logger?.debug?.(`[usage-bar] document locale: ${raw}`);
  } catch (error) {
    logger?.debug?.(`[usage-bar] document locale read failed: ${error.message}`);
  }
}

function parseArgs(argv) {
  const options = {
    port: 9335,
    cdpHost: '127.0.0.1',
    mode: 'watch',
    browserId: null,
    renderer: null,
    codexCommand: null,
    trace: false,
    timeoutMs: 30000,
  };
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === '--port') options.port = Number(argv[++i]);
    else if (arg === '--cdp-host') options.cdpHost = argv[++i];
    else if (arg === '--browser-id') options.browserId = argv[++i];
    else if (arg === '--renderer') options.renderer = path.resolve(argv[++i]);
    else if (arg === '--codex-command') options.codexCommand = path.resolve(argv[++i]);
    else if (arg === '--trace') options.trace = true;
    else if (arg === '--watch') options.mode = 'watch';
    else if (arg === '--once') options.mode = 'once';
    else if (arg === '--verify') options.mode = 'verify';
    else if (arg === '--remove') options.mode = 'remove';
    else if (arg === '--self-test') options.mode = 'self-test';
    else if (arg === '--probe') options.mode = 'probe';
    else if (arg === '--timeout-ms') options.timeoutMs = Number(argv[++i]);
    else throw new Error(`Unknown argument: ${arg}`);
  }
  if (!Number.isInteger(options.port) || options.port < 1024 || options.port > 65535) {
    throw new Error(`Invalid CDP port: ${options.port}`);
  }
  if (!['127.0.0.1', '::1'].includes(options.cdpHost)) {
    throw new Error(`Invalid CDP host: ${options.cdpHost}`);
  }
  if (!Number.isInteger(options.timeoutMs) || options.timeoutMs < 250 || options.timeoutMs > 120000) {
    throw new Error(`Invalid timeout: ${options.timeoutMs}`);
  }
  if (!['self-test', 'probe'].includes(options.mode)) {
    if (!options.browserId || !ID_PATTERN.test(options.browserId)) throw new Error('--browser-id is required and invalid');
    if (!options.renderer) throw new Error('--renderer is required');
  }
  if (options.mode === 'watch' && !options.codexCommand) throw new Error('--codex-command is required in watch mode');
  return options;
}

function makeLogger(trace) {
  const stamp = () => new Date().toISOString();
  return {
    info(message) { console.log(`${stamp()} ${message}`); },
    debug(message) { if (trace) console.log(`${stamp()} DEBUG ${message}`); },
    error(message) { console.error(`${stamp()} ERROR ${message}`); },
  };
}

function validateDebuggerUrl(target, port, kind = 'page') {
  const value = String(target?.webSocketDebuggerUrl ?? '');
  const url = new URL(value);
  const expected = kind === 'browser'
    ? /^\/devtools\/browser\/[A-Za-z0-9._-]{1,200}$/
    : /^\/devtools\/page\/[A-Za-z0-9._-]{1,200}$/;
  if (url.protocol !== 'ws:' || !LOOPBACK_HOSTS.has(url.hostname) || Number(url.port) !== port ||
      url.username || url.password || url.search || url.hash || !expected.test(url.pathname)) {
    throw new Error(`Rejected unsafe CDP WebSocket URL: ${value}`);
  }
  return url.href;
}

function browserIdFromVersion(version, port) {
  const url = new URL(validateDebuggerUrl(version, port, 'browser'));
  const match = url.pathname.match(/^\/devtools\/browser\/([A-Za-z0-9._-]{1,200})$/);
  if (!match || !ID_PATTERN.test(match[1])) throw new Error('Invalid CDP browser identity');
  return match[1];
}

function cdpHttpBase(cdpHost, port) {
  if (cdpHost === '127.0.0.1') return `http://127.0.0.1:${port}`;
  if (cdpHost === '::1') return `http://[::1]:${port}`;
  throw new Error(`Rejected non-loopback CDP HTTP host: ${cdpHost}`);
}

async function fetchCdpJson(cdpHost, port, resource) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 2500);
  try {
    const response = await fetch(`${cdpHttpBase(cdpHost, port)}${resource}`, {
      redirect: 'error',
      signal: controller.signal,
    });
    if (!response.ok) throw new Error(`CDP HTTP ${response.status}`);
    return await response.json();
  } finally {
    clearTimeout(timeout);
  }
}

async function assertBrowserIdentity(cdpHost, port, expectedBrowserId) {
  const version = await fetchCdpJson(cdpHost, port, '/json/version');
  const actual = browserIdFromVersion(version, port);
  if (actual !== expectedBrowserId) {
    throw new Error(`CDP browser identity changed from ${expectedBrowserId} to ${actual}`);
  }
  return version;
}

async function listAppTargets(cdpHost, port) {
  const targets = await fetchCdpJson(cdpHost, port, '/json/list');
  if (!Array.isArray(targets)) throw new Error('CDP target list is not an array');
  return targets.filter((target) => {
    if (target?.type !== 'page' || typeof target.id !== 'string' || !ID_PATTERN.test(target.id)) return false;
    if (typeof target.url !== 'string' || !target.url.startsWith('app://')) return false;
    try {
      const url = new URL(validateDebuggerUrl(target, port, 'page'));
      return url.pathname === `/devtools/page/${target.id}`;
    } catch {
      return false;
    }
  });
}

function parseCdpMessage(data) {
  try {
    const message = JSON.parse(String(data));
    return message && typeof message === 'object' ? message : null;
  } catch {
    return null;
  }
}

class CdpSession {
  constructor(target, port) {
    this.target = target;
    this.ws = new WebSocket(validateDebuggerUrl(target, port, 'page'));
    this.nextId = 1;
    this.pending = new Map();
    this.listeners = new Map();
    this.closed = false;
  }

  async open() {
    await new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        try { this.ws.close(); } catch {}
        reject(new Error('CDP WebSocket open timed out'));
      }, 5000);
      this.ws.addEventListener('open', () => { clearTimeout(timeout); resolve(); }, { once: true });
      this.ws.addEventListener('error', () => { clearTimeout(timeout); reject(new Error('CDP WebSocket open failed')); }, { once: true });
    });
    this.ws.addEventListener('message', (event) => this.onMessage(event));
    this.ws.addEventListener('error', () => this.close());
    this.ws.addEventListener('close', () => {
      this.closed = true;
      for (const waiter of this.pending.values()) {
        clearTimeout(waiter.timeout);
        waiter.reject(new Error('CDP socket closed'));
      }
      this.pending.clear();
    });
    await this.send('Runtime.enable');
    await this.send('Page.enable');
    return this;
  }

  onMessage(event) {
    const message = parseCdpMessage(event.data);
    if (!message) {
      this.close();
      return;
    }
    if (message.id) {
      const waiter = this.pending.get(message.id);
      if (!waiter) return;
      clearTimeout(waiter.timeout);
      this.pending.delete(message.id);
      if (message.error) waiter.reject(new Error(`${message.error.message} (${message.error.code})`));
      else waiter.resolve(message.result);
      return;
    }
    for (const listener of this.listeners.get(message.method) ?? []) {
      try { listener(message.params ?? {}); } catch {}
    }
  }

  on(method, listener) {
    const current = this.listeners.get(method) ?? [];
    current.push(listener);
    this.listeners.set(method, current);
  }

  send(method, params = {}, timeoutMs = 10000) {
    if (this.closed || this.ws.readyState !== WebSocket.OPEN) {
      return Promise.reject(new Error('CDP session is closed'));
    }
    return new Promise((resolve, reject) => {
      const id = this.nextId++;
      const timeout = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`CDP command timed out: ${method}`));
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timeout });
      try {
        this.ws.send(JSON.stringify({ id, method, params }));
      } catch (error) {
        clearTimeout(timeout);
        this.pending.delete(id);
        reject(error);
      }
    });
  }

  async evaluate(expression, timeoutMs = 10000) {
    const result = await this.send('Runtime.evaluate', {
      expression,
      awaitPromise: true,
      returnByValue: true,
    }, timeoutMs);
    if (result?.exceptionDetails) {
      const text = result.exceptionDetails.exception?.description || result.exceptionDetails.text || 'Renderer evaluation failed';
      throw new Error(text);
    }
    return result?.result?.value;
  }

  close() {
    if (this.closed) return;
    this.closed = true;
    try { this.ws.close(); } catch {}
  }
}

class BrowserIdentityAnchor {
  constructor(version, port) {
    this.ws = new WebSocket(validateDebuggerUrl(version, port, 'browser'));
    this.closed = false;
  }
  async open() {
    await new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        try { this.ws.close(); } catch {}
        reject(new Error('Browser identity WebSocket open timed out'));
      }, 5000);
      this.ws.addEventListener('open', () => { clearTimeout(timeout); resolve(); }, { once: true });
      this.ws.addEventListener('error', () => { clearTimeout(timeout); reject(new Error('Browser identity WebSocket open failed')); }, { once: true });
    });
    this.ws.addEventListener('close', () => { this.closed = true; });
    this.ws.addEventListener('error', () => { this.closed = true; });
    return this;
  }
  close() { try { this.ws.close(); } catch {} this.closed = true; }
}

async function probeCodexSession(session) {
  return Boolean(await session.evaluate(`(() => {
    if (location.protocol !== 'app:') return false;
    return Boolean(document.getElementById('application-menu-trigger-help-menu'));
  })()`));
}

async function waitForCodexProbe(session, timeoutMs = 2500) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try { if (await probeCodexSession(session)) return true; } catch {}
    await sleep(75);
  }
  return false;
}

function earlyPayloadFor(payload, revision) {
  return `(() => {
    const generationKey = '__CODEX_USAGE_BAR_EARLY_GENERATION__';
    const appliedKey = '__CODEX_USAGE_BAR_EARLY_APPLIED__';
    const generation = ${JSON.stringify(revision)};
    window[generationKey] = generation;
    let timer = null;
    let expiry = null;
    const stop = () => { if (timer) clearInterval(timer); if (expiry) clearTimeout(expiry); timer = null; expiry = null; };
    const install = () => {
      if (window[generationKey] !== generation) { stop(); return true; }
      if (location.protocol !== 'app:' || !document.documentElement || !document.getElementById('application-menu-trigger-help-menu')) return false;
      stop();
      ${payload}
      window[appliedKey] = generation;
      return true;
    };
    if (install()) return;
    document.addEventListener?.('DOMContentLoaded', install, { once: true });
    timer = setInterval(install, 250);
    expiry = setTimeout(stop, 10000);
  })()`;
}

async function registerEarlyPayload(session, payload, revision) {
  const result = await session.send('Page.addScriptToEvaluateOnNewDocument', {
    source: earlyPayloadFor(payload, revision),
  });
  return result?.identifier ?? null;
}

async function removeEarlyPayload(session, identifier) {
  if (!identifier || session.closed) return;
  await session.send('Page.removeScriptToEvaluateOnNewDocument', { identifier }).catch(() => {});
}

function collectLimitWindows(response) {
  const root = response?.rateLimits ?? response;
  const items = [];
  const visit = (node, pathName) => {
    if (!node || typeof node !== 'object') return;
    if (Number.isFinite(Number(node.usedPercent))) {
      const used = Math.max(0, Math.min(100, Number(node.usedPercent)));
      const remaining = 100 - used;
      items.push({
        key: pathName || 'rateLimit',
        usedPercent: Math.round(used * 1000) / 1000,
        remaining: Math.round(remaining * 1000) / 1000,
        resetsAt: node.resetsAt ?? null,
        windowDurationMins: Number.isFinite(Number(node.windowDurationMins)) ? Number(node.windowDurationMins) : null,
      });
      return;
    }
    if (Array.isArray(node)) {
      node.forEach((value, index) => visit(value, `${pathName}[${index}]`));
      return;
    }
    for (const [key, value] of Object.entries(node)) {
      if (key === 'rateLimitResetCredits') continue;
      visit(value, pathName ? `${pathName}.${key}` : key);
    }
  };
  visit(root, 'rateLimits');
  return items;
}

function compactToken(value) {
  const n = Number(value);
  if (!Number.isFinite(n) || n < 0) return '\u2014';
  const units = ['', 'K', 'M', 'B'];
  let number = n;
  let i = 0;
  while (number >= 1000 && i < units.length - 1) { number /= 1000; i += 1; }
  const rounded = i === 0 ? Math.round(number) : Math.round(number * 10) / 10;
  return `${rounded.toLocaleString('en-US', { maximumFractionDigits: i === 0 ? 0 : 1 })}${units[i]}`;
}

function tokenView(response) {
  const summary = response?.summary ?? {};
  const lifetime = summary?.lifetimeTokens;
  let today = null;
  const now = new Date();
  const todayKey = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
  for (const bucket of Array.isArray(response?.dailyUsageBuckets) ? response.dailyUsageBuckets : []) {
    if (String(bucket?.startDate) === todayKey) { today = bucket?.tokens; break; }
  }
  const exact = (value) => Number.isFinite(Number(value)) ? Math.round(Number(value)).toLocaleString('en-US') : null;
  return {
    today: compactToken(today),
    lifetime: compactToken(lifetime),
    todayExact: exact(today),
    lifetimeExact: exact(lifetime),
  };
}

class AppServerClient {
  constructor(codexCommand, logger) {
    this.codexCommand = codexCommand;
    this.logger = logger;
    this.proc = null;
    this.nextId = 1;
    this.pending = new Map();
    this.initialized = false;
    this.onRateUpdate = null;
  }

  async start() {
    if (this.proc && this.proc.exitCode === null) return;
    this.initialized = false;
    this.pending.clear();
    const env = { ...process.env };
    const command = this.codexCommand;
    const ext = path.extname(command).toLowerCase();
    let fileName = command;
    let args = ['app-server', '--listen', 'stdio://'];

    // Windows Store's bundled resources\codex.exe is ACL-protected and cannot
    // be spawned directly from a normal Node process.  Reuse the launchable CLI
    // resolution strategy from the original PowerShell plugin instead.
    let windowsVerbatimArguments = false;
    if (ext === '.cmd' || ext === '.bat') {
      fileName = process.env.ComSpec || 'cmd.exe';
      // Keep the /c payload from starting with a quote. Node's normal Windows
      // argv escaping can turn the classic cmd.exe /c ""script.cmd" ..."
      // form into literal backslash-escaped quotes. Prefixing with CHCP/CALL
      // gives cmd.exe an unambiguous command line and keeps diagnostics UTF-8.
      const commandLine = `chcp 65001>nul & call "${command}" app-server --listen stdio://`;
      args = ['/d', '/s', '/c', commandLine];
      windowsVerbatimArguments = true;
    } else if (ext === '.ps1') {
      fileName = 'powershell.exe';
      args = ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', command, 'app-server', '--listen', 'stdio://'];
    }

    this.logger.info(`[usage-bar] starting app-server via: ${command}`);
    this.logger.debug(`[usage-bar] app-server launcher: ${fileName} ${args.join(' ')}`);
    this.proc = spawn(fileName, args, {
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true,
      shell: false,
      windowsVerbatimArguments,
      env,
    });
    this.proc.stdin.setDefaultEncoding('utf8');
    this.proc.stdout.setEncoding('utf8');
    this.proc.stderr.setEncoding('utf8');
    this.proc.on('exit', (code, signal) => {
      this.initialized = false;
      const error = new Error(`Codex app-server exited (${code ?? 'null'}${signal ? `, ${signal}` : ''})`);
      for (const pending of this.pending.values()) pending.reject(error);
      this.pending.clear();
      this.logger.error(`[usage-bar] ${error.message}`);
    });
    this.proc.on('error', (error) => this.logger.error(`[usage-bar] app-server spawn failed: ${error.message}`));

    const stdout = readline.createInterface({ input: this.proc.stdout, crlfDelay: Infinity });
    stdout.on('line', (line) => this.handleLine(line));
    const stderr = readline.createInterface({ input: this.proc.stderr, crlfDelay: Infinity });
    stderr.on('line', (line) => this.logger.debug(`[app-server] ${line}`));

    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('Codex app-server did not stay alive during startup')), 250);
      this.proc.once('exit', () => { clearTimeout(timer); reject(new Error('Codex app-server exited during startup')); });
      setTimeout(() => { clearTimeout(timer); resolve(); }, 80);
    });

    await this.request('initialize', {
      clientInfo: { name: 'codex-usage-bar', title: 'Codex Usage Bar', version: VERSION },
      capabilities: { experimentalApi: true },
    }, 10000);
    this.notify('initialized');
    this.initialized = true;
    this.logger.info('[usage-bar] app-server initialized');
  }

  handleLine(line) {
    if (!line || !line.trim()) return;
    let message;
    try { message = JSON.parse(line); }
    catch {
      this.logger.debug(`[app-server stdout non-json] ${line}`);
      return;
    }
    if (message.id !== undefined && message.id !== null) {
      const pending = this.pending.get(String(message.id));
      if (!pending) return;
      clearTimeout(pending.timeout);
      this.pending.delete(String(message.id));
      if (message.error) pending.reject(new Error(JSON.stringify(message.error)));
      else pending.resolve(message.result);
      return;
    }
    if (message.method === 'account/rateLimits/updated') {
      try { this.onRateUpdate?.(message.params ?? {}); } catch {}
    }
  }

  write(message) {
    if (!this.proc || this.proc.exitCode !== null || !this.proc.stdin.writable) throw new Error('Codex app-server stdin is not writable');
    const line = `${JSON.stringify(message)}\n`;
    this.proc.stdin.write(line, 'utf8');
    this.logger.debug(`[app-server ->] ${line.trim()}`);
  }

  notify(method, params = undefined) {
    const message = { method };
    if (params !== undefined) message.params = params;
    this.write(message);
  }

  request(method, params = undefined, timeoutMs = 10000) {
    return new Promise((resolve, reject) => {
      const id = this.nextId++;
      const timeout = setTimeout(() => {
        this.pending.delete(String(id));
        reject(new Error(`App-server request timed out: ${method}`));
      }, timeoutMs);
      this.pending.set(String(id), { resolve, reject, timeout });
      try {
        const message = { method, id };
        if (params !== undefined) message.params = params;
        this.write(message);
      }
      catch (error) {
        clearTimeout(timeout);
        this.pending.delete(String(id));
        reject(error);
      }
    });
  }

  stop() {
    const proc = this.proc;
    this.proc = null;
    this.initialized = false;
    if (!proc) return;
    try { proc.stdin.end(); } catch {}
    const killer = setTimeout(() => { try { proc.kill(); } catch {} }, 1200);
    proc.once('exit', () => clearTimeout(killer));
  }
}

async function loadRenderer(rendererPath) {
  const source = await fs.readFile(rendererPath, 'utf8');
  if (!source.includes('application-menu-trigger-help-menu') || !source.includes('__codexUsageBar')) {
    throw new Error('Renderer payload is missing required Codex Usage Bar markers');
  }
  // Compile-only validation without executing DOM code.
  new Function(source);
  return source;
}

async function publishState(session, state, logger = null) {
  const json = JSON.stringify(state);
  const result = await session.evaluate(`Boolean(window.__codexUsageBar && window.__codexUsageBar.render(${json}))`);
  await noteDocumentLocale(session, logger);
  return result;
}

async function removeFromSession(session) {
  return session.evaluate(`(() => {
    try { window.__codexUsageBar?.destroy?.(); } catch {}
    try { delete window.__codexUsageBar; } catch {}
    document.getElementById('codex-usage-native')?.remove();
    document.getElementById('codex-usage-native-popover')?.remove();
    document.getElementById('codex-usage-native-style')?.remove();
    return true;
  })()`);
}

async function verifySession(session) {
  return session.evaluate(`(() => ({
    codex: location.protocol === 'app:' && Boolean(document.getElementById('application-menu-trigger-help-menu')),
    injected: Boolean(window.__codexUsageBar),
    rootPresent: Boolean(document.getElementById('codex-usage-native')),
    version: window.__codexUsageBar?.version ?? null,
  }))()`);
}


async function runProbe(options) {
  const version = await fetchCdpJson(options.cdpHost, options.port, '/json/version');
  const browserId = browserIdFromVersion(version, options.port);
  const targets = await listAppTargets(options.cdpHost, options.port);
  const payload = {
    ok: true,
    cdpHost: options.cdpHost,
    port: options.port,
    browserId,
    browser: String(version?.Browser ?? ''),
    protocolVersion: String(version?.['Protocol-Version'] ?? ''),
    targetCount: targets.length,
    targets: targets.map((target) => ({ id: target.id, title: target.title ?? '', url: target.url })),
  };
  console.log(JSON.stringify(payload));
}

async function connectVerifiedCodexTargets(cdpHost, port, timeoutMs, expectedBrowserId) {
  const deadline = Date.now() + timeoutMs;
  let lastError = null;
  while (Date.now() < deadline) {
    await assertBrowserIdentity(cdpHost, port, expectedBrowserId);
    const targets = await listAppTargets(cdpHost, port);
    const connected = [];
    for (const target of targets) {
      let session = null;
      try {
        session = await new CdpSession(target, port).open();
        if (await waitForCodexProbe(session, 1800)) connected.push({ target, session });
        else session.close();
      } catch (error) {
        session?.close();
        lastError = error;
      }
    }
    if (connected.length) return connected;
    lastError = lastError ?? new Error('No renderer matched the Codex menu marker');
    await sleep(350);
  }
  throw new Error(`No verified Codex renderer: ${lastError?.message ?? 'timed out'}`);
}

async function runOneShot(options, rendererSource) {
  const connected = await connectVerifiedCodexTargets(options.cdpHost, options.port, options.timeoutMs, options.browserId);
  const results = [];
  try {
    for (const { target, session } of connected) {
      try {
        if (options.mode === 'remove') await removeFromSession(session);
        else if (options.mode === 'once') await session.evaluate(rendererSource);
        const result = options.mode === 'remove'
          ? { removed: !(await verifySession(session)).injected }
          : await verifySession(session);
        results.push({ targetId: target.id, result });
      } catch (error) {
        results.push({ targetId: target.id, error: error.message });
      } finally { session.close(); }
    }
  } finally { for (const { session } of connected) session.close(); }
  console.log(JSON.stringify({ mode: options.mode, cdpHost: options.cdpHost, port: options.port, targets: results }, null, 2));
  const failed = results.length === 0 || results.some((item) => item.error ||
    (options.mode === 'remove' ? !item.result?.removed : !item.result?.injected));
  if (failed) process.exitCode = 2;
}

async function runWatch(options, rendererSource, logger) {
  const version = await assertBrowserIdentity(options.cdpHost, options.port, options.browserId);
  const identityAnchor = await new BrowserIdentityAnchor(version, options.port).open();
  const sessions = new Map();
  const earlyScripts = new Map();
  const fallbackTargets = new Set();
  const failures = new Map();
  const revision = `${VERSION}:${Buffer.from(rendererSource).length}`;
  let stopping = false;
  let state = {
    windows: [],
    tokens: { today: '\u2014', lifetime: '\u2014', todayExact: null, lifetimeExact: null },
    i18n: { catalogs: await loadLocaleCatalogs(logger) },
  };
  let lastRateAt = 0;
  let lastUsageAt = 0;
  let rateRefreshQueued = false;
  let appServer = null;
  let appServerRetryAt = 0;
  let lastLocaleProbeAt = 0;

  const stop = () => { stopping = true; };
  process.on('SIGINT', stop);
  process.on('SIGTERM', stop);

  const publishAll = async () => {
    for (const [id, session] of sessions) {
      if (session.closed) continue;
      try { await publishState(session, state, logger); }
      catch (error) { logger.error(`[usage-bar] publish failed for ${id}: ${error.message}`); session.close(); }
    }
  };

  const refreshRate = async () => {
    if (!appServer?.initialized) return;
    const response = await appServer.request('account/rateLimits/read');
    state.windows = collectLimitWindows(response);
    lastRateAt = Date.now();
    logger.debug(`[usage-bar] rate windows: ${JSON.stringify(state.windows)}`);
    await publishAll();
  };
  const refreshUsage = async () => {
    if (!appServer?.initialized) return;
    const response = await appServer.request('account/usage/read');
    state.tokens = tokenView(response);
    lastUsageAt = Date.now();
    logger.debug(`[usage-bar] token usage: ${JSON.stringify(state.tokens)}`);
    await publishAll();
  };

  const consumeManualRefresh = async () => {
    if (!REFRESH_REQUEST_FILE) return false;
    try {
      await fs.unlink(REFRESH_REQUEST_FILE);
      return true;
    } catch (error) {
      if (error?.code === 'ENOENT') return false;
      logger.error(`[usage-bar] refresh request read failed: ${error.message}`);
      return false;
    }
  };

  const ensureAppServer = async () => {
    if (appServer?.proc && appServer.proc.exitCode === null && appServer.initialized) return true;
    if (Date.now() < appServerRetryAt) return false;
    try {
      appServer?.stop();
      appServer = new AppServerClient(options.codexCommand, logger);
      appServer.onRateUpdate = () => { rateRefreshQueued = true; };
      await appServer.start();
      await Promise.all([refreshRate(), refreshUsage()]);
      appServerRetryAt = 0;
      return true;
    } catch (error) {
      logger.error(`[usage-bar] app-server unavailable: ${error.message}`);
      appServer?.stop();
      appServerRetryAt = Date.now() + 5000;
      return false;
    }
  };

  const attachFallback = (id, session) => {
    session.on('Page.loadEventFired', () => {
      if (!fallbackTargets.has(id)) return;
      setTimeout(async () => {
        try {
          if (await waitForCodexProbe(session, 1800)) {
            await session.evaluate(rendererSource);
            await publishState(session, state, logger);
          }
        } catch (error) { logger.error(`[usage-bar] fallback reinject failed for ${id}: ${error.message}`); }
      }, 250);
    });
  };

  try {
    await ensureAppServer();
    while (!stopping) {
      if (identityAnchor.closed) {
        logger.error('[usage-bar] original CDP browser identity closed; watcher will stop');
        process.exitCode = 3;
        break;
      }

      let targets = [];
      try { targets = await listAppTargets(options.cdpHost, options.port); }
      catch (error) { logger.error(`[usage-bar] target list failed: ${error.message}`); await sleep(1500); continue; }

      const activeIds = new Set(targets.map((target) => target.id));
      for (const [id, session] of sessions) {
        if (!activeIds.has(id) || session.closed) {
          await removeEarlyPayload(session, earlyScripts.get(id));
          session.close();
          sessions.delete(id);
          earlyScripts.delete(id);
          fallbackTargets.delete(id);
        }
      }

      for (const target of targets) {
        if (sessions.has(target.id)) continue;
        const fail = failures.get(target.id);
        if (fail && fail.until > Date.now()) continue;
        let session = null;
        let earlyId = null;
        try {
          session = await new CdpSession(target, options.port).open();
          let fallback = false;
          try {
            earlyId = await registerEarlyPayload(session, rendererSource, revision);
            if (!earlyId) throw new Error('No early-script identifier');
            await session.evaluate(earlyPayloadFor(rendererSource, revision));
          } catch (error) {
            await removeEarlyPayload(session, earlyId);
            earlyId = null;
            fallback = true;
            logger.debug(`[usage-bar] early injection unavailable for ${target.id}: ${error.message}`);
          }
          if (!(await waitForCodexProbe(session, 2200))) {
            await removeEarlyPayload(session, earlyId);
            session.close();
            failures.set(target.id, { until: Date.now() + 5000, count: 1 });
            continue;
          }
          if (fallback) {
            fallbackTargets.add(target.id);
            attachFallback(target.id, session);
            await session.evaluate(rendererSource);
          } else {
            const earlyApplied = await session.evaluate(`window.__CODEX_USAGE_BAR_EARLY_APPLIED__ === ${JSON.stringify(revision)}`).catch(() => false);
            if (!earlyApplied) await session.evaluate(rendererSource);
          }
          sessions.set(target.id, session);
          if (earlyId) earlyScripts.set(target.id, earlyId);
          failures.delete(target.id);
          await publishState(session, state, logger);
          logger.info(`[usage-bar] injected target ${target.id}`);
        } catch (error) {
          await removeEarlyPayload(session, earlyId);
          session?.close();
          const previous = failures.get(target.id) ?? { count: 0 };
          const count = previous.count + 1;
          const delay = Math.min(30000, 2500 * (2 ** Math.min(count - 1, 3)));
          failures.set(target.id, { count, until: Date.now() + delay });
          logger.error(`[usage-bar] inject failed for ${target.id}: ${error.message}; retry ${delay}ms`);
        }
      }

      if (Date.now() - lastLocaleProbeAt >= 10000) {
        lastLocaleProbeAt = Date.now();
        const firstLiveSession = Array.from(sessions.values()).find((session) => !session.closed);
        if (firstLiveSession) await noteDocumentLocale(firstLiveSession, logger);
      }

      await ensureAppServer();
      if (appServer?.initialized) {
        try {
          const manualRefresh = await consumeManualRefresh();
          if (manualRefresh) {
            rateRefreshQueued = false;
            state.i18n = { catalogs: await loadLocaleCatalogs(logger) };
            await Promise.all([refreshRate(), refreshUsage()]);
            logger.info('[usage-bar] manual refresh completed (data + locales)');
          } else {
            if (rateRefreshQueued) { rateRefreshQueued = false; await refreshRate(); }
            else if (Date.now() - lastRateAt >= 120000) await refreshRate();
            if (Date.now() - lastUsageAt >= 600000) await refreshUsage();
          }
        } catch (error) {
          logger.error(`[usage-bar] app-server request failed: ${error.message}`);
          appServer.stop();
          appServerRetryAt = Date.now() + 5000;
        }
      }

      await sleep(1200);
    }
  } finally {
    identityAnchor.close();
    appServer?.stop();
    for (const [id, session] of sessions) {
      await removeEarlyPayload(session, earlyScripts.get(id));
      session.close();
    }
  }
}

function selfTest() {
  const probeArgs = parseArgs(['--probe', '--cdp-host', '::1', '--port', '9335']);
  if (probeArgs.mode !== 'probe' || probeArgs.cdpHost !== '::1') throw new Error('probe argument parsing failed');
  const goodPage = { webSocketDebuggerUrl: 'ws://127.0.0.1:9335/devtools/page/test' };
  const goodBrowser = { webSocketDebuggerUrl: 'ws://127.0.0.1:9335/devtools/browser/test-browser' };
  const goodIpv6Page = { webSocketDebuggerUrl: 'ws://[::1]:9335/devtools/page/test-v6' };
  if (!validateDebuggerUrl(goodPage, 9335, 'page').includes('/devtools/page/test')) throw new Error('page URL validation failed');
  if (browserIdFromVersion(goodBrowser, 9335) !== 'test-browser') throw new Error('browser ID test failed');
  if (!validateDebuggerUrl(goodIpv6Page, 9335, 'page').includes('/devtools/page/test-v6')) throw new Error('IPv6 page URL validation failed');
  if (cdpHttpBase('::1', 9335) !== 'http://[::1]:9335') throw new Error('IPv6 HTTP base failed');
  if (cdpHttpBase('127.0.0.1', 9335) !== 'http://127.0.0.1:9335') throw new Error('IPv4 HTTP base failed');
  for (const bad of [
    'ws://example.com:9335/devtools/page/test',
    'ws://127.0.0.1:9336/devtools/page/test',
    'wss://127.0.0.1:9335/devtools/page/test',
    'ws://user@127.0.0.1:9335/devtools/page/test',
    'ws://127.0.0.1:9335/wrong/test',
  ]) {
    let rejected = false;
    try { validateDebuggerUrl({ webSocketDebuggerUrl: bad }, 9335, 'page'); } catch { rejected = true; }
    if (!rejected) throw new Error(`Unsafe URL accepted: ${bad}`);
  }
  const sample = { rateLimits: { primary: { usedPercent: 25, resetsAt: 1787144400 }, secondary: { usedPercent: 82, resetsAt: 1787749200 } } };
  const sampleWindows = collectLimitWindows(sample);
  if (sampleWindows.length !== 2 || sampleWindows[0].resetsAt !== 1787144400) throw new Error('rate-limit collection failed');
  if (normalizeLocaleCode('PT_BR') !== 'pt-br') throw new Error('locale normalization failed');
  console.log('Codex Usage Bar injector self-tests passed.');
}

const scriptPath = fileURLToPath(import.meta.url);
if (path.resolve(process.argv[1] || '') === path.resolve(scriptPath)) {
  const options = parseArgs(process.argv.slice(2));
  const logger = makeLogger(options.trace || process.env.CODEX_USAGE_BAR_TRACE === '1');
  if (options.mode === 'self-test') {
    selfTest();
  } else if (options.mode === 'probe') {
    await runProbe(options);
  } else {
    const rendererSource = await loadRenderer(options.renderer);
    if (options.mode === 'watch') await runWatch(options, rendererSource, logger);
    else await runOneShot(options, rendererSource);
  }
}
