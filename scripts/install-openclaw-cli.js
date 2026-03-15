#!/usr/bin/env node
const { spawnSync } = require('child_process');
const path = require('path');
const fs = require('fs');
const os = require('os');

function resolveNpmExecutable() {
  const candidates = [];
  const pathParts = String(process.env.PATH || '').split(path.delimiter).filter(Boolean);

  // Common Windows locations
  candidates.push(
    path.join(process.env.APPDATA || '', 'npm', 'npm.cmd'),
    path.join(process.env.ProgramFiles || '', 'nodejs', 'npm.cmd'),
    path.join(process.env['ProgramFiles(x86)'] || '', 'nodejs', 'npm.cmd'),
  );

  // PATH entries
  for (const p of pathParts) {
    candidates.push(path.join(p, os.platform() === 'win32' ? 'npm.cmd' : 'npm'));
  }

  for (const cand of candidates) {
    try {
      if (cand && fs.existsSync(cand) && fs.statSync(cand).isFile()) {
        return cand;
      }
    } catch (_) {
      // ignore
    }
  }

  return os.platform() === 'win32' ? 'npm.cmd' : 'npm';
}

function runNpmInstall() {
  const npmExe = resolveNpmExecutable();
  const args = ['install', '-g', 'openclaw@latest', '--no-fund', '--no-audit', '--loglevel=error'];
  const result = spawnSync(npmExe, args, {
    stdio: 'inherit',
    windowsHide: true,
    env: { ...process.env, SHARP_IGNORE_GLOBAL_LIBVIPS: '1' },
  });

  if (result.error || Number(result.status) !== 0) {
    // Fallback: try PowerShell to resolve npm on PATH and avoid EINVAL
    const psExe = process.env.ComSpec && process.env.ComSpec.toLowerCase().includes('powershell')
      ? process.env.ComSpec
      : 'powershell.exe';
    const psCmd = `npm install -g openclaw@latest --no-fund --no-audit --loglevel=error`;
    const psResult = spawnSync(psExe, ['-NoProfile', '-NonInteractive', '-Command', psCmd], {
      stdio: 'inherit',
      windowsHide: true,
      env: { ...process.env, SHARP_IGNORE_GLOBAL_LIBVIPS: '1' },
    });

    if (psResult.error) {
      if (psResult.error.code === 'ENOENT') {
        throw new Error('npm was not found. Install Node.js from https://nodejs.org and retry.');
      }
      throw new Error(`PowerShell npm install failed: ${psResult.error.message || psResult.error}`);
    }

    if (Number(psResult.status) !== 0) {
      throw new Error(`npm install exited with code ${psResult.status}. Try running from an elevated PowerShell.`);
    }
  }
}

console.log('Installing OpenClaw CLI via npm install -g openclaw@latest …');
try {
  runNpmInstall();
  console.log('✅ OpenClaw CLI installed.');
} catch (err) {
  console.error(`❌ Failed to install OpenClaw CLI: ${err.message || err}`);
  process.exit(1);
}
