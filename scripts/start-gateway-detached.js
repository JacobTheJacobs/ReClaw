#!/usr/bin/env node
const { spawn } = require('child_process');
const fs = require('fs');
const path = require('path');
const os = require('os');

function sanitizeEnv(raw) {
  const out = {};
  for (const [k, v] of Object.entries(raw || {})) {
    if (!k || k.includes('\0') || k.includes('=')) continue;
    if (v == null) continue;
    out[k] = String(v);
  }
  return out;
}

function augmentPathForWindows(env) {
  if (process.platform !== 'win32') return env;
  const extras = [
    path.join(process.env.APPDATA || '', 'npm'),
    path.join(process.env.ProgramFiles || '', 'nodejs'),
    path.join(process.env['ProgramFiles(x86)'] || '', 'nodejs'),
  ].filter(Boolean);
  const current = String(env.PATH || process.env.PATH || '');
  const merged = [...extras, ...current.split(';').filter(Boolean)];
  const deduped = [];
  const seen = new Set();
  for (const item of merged) {
    const key = item.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    deduped.push(item);
  }
  return { ...env, PATH: deduped.join(';') };
}

function resolveOpenclawBinary() {
  const candidates = [];
  if (process.platform === 'win32') {
    candidates.push(
      process.env.RECLAW_OPENCLAW_PATH || process.env.OPENCLAW_EXE || null,
      path.join(process.env.APPDATA || '', 'npm', 'openclaw.cmd'),
      path.join(process.env.ProgramFiles || '', 'nodejs', 'openclaw.cmd'),
      path.join(process.env['ProgramFiles(x86)'] || '', 'nodejs', 'openclaw.cmd'),
      'openclaw.cmd',
    );
  } else {
    candidates.push(
      process.env.RECLAW_OPENCLAW_PATH || process.env.OPENCLAW_EXE || null,
      '/usr/local/bin/openclaw',
      '/opt/homebrew/bin/openclaw',
      '/usr/bin/openclaw',
      'openclaw',
    );
  }

  for (const cand of candidates.filter(Boolean)) {
    try {
      if (fs.existsSync(cand) && fs.statSync(cand).isFile()) {
        return cand;
      }
    } catch (_) {
      // ignore
    }
  }
  return process.platform === 'win32' ? 'openclaw.cmd' : 'openclaw';
}

function startGatewayDetached() {
  const env = augmentPathForWindows(sanitizeEnv(process.env));
  const port = process.env.OPENCLAW_GATEWAY_PORT || '18789';
  const args = ['gateway', 'run', '--port', port];

  const opts = {
    env,
    detached: true,
    windowsHide: true,
    stdio: 'ignore',
  };

  let cmd = resolveOpenclawBinary();
  let cmdArgs = args;

  if (process.platform === 'win32' && /\.cmd$/i.test(cmd)) {
    cmdArgs = ['/c', cmd, ...args];
    cmd = 'cmd.exe';
  }

  const child = spawn(cmd, cmdArgs, opts);
  child.unref();
  return true;
}

function main() {
  try {
    const ok = startGatewayDetached();
    if (!ok) {
      process.stderr.write('Failed to spawn gateway run detached.\n');
      process.exit(1);
      return;
    }
    process.stdout.write('Gateway run launched in background.\n');
  } catch (error) {
    process.stderr.write(`Failed: ${error.message}\n`);
    process.exit(1);
  }
}

if (require.main === module) {
  main();
}
