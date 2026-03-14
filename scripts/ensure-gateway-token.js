#!/usr/bin/env node
const fs = require('fs-extra');
const path = require('path');
const os = require('os');
const crypto = require('crypto');
const http = require('http');
const { spawn, spawnSync } = require('child_process');

function parseArgs(argv) {
  const out = {};
  for (let i = 0; i < argv.length; i += 1) {
    const token = argv[i];
    if (token === '--home' && argv[i + 1]) {
      out.home = argv[i + 1];
      i += 1;
    } else if (token === '--open') {
      out.open = true;
    }
  }
  return out;
}

function sanitizeSpawnEnv(rawEnv) {
  const safeEnv = {};
  for (const [key, value] of Object.entries(rawEnv || {})) {
    if (!key || key.includes('\0') || key.includes('=')) {
      continue;
    }
    if (value == null) {
      continue;
    }
    safeEnv[key] = String(value);
  }
  return safeEnv;
}

function openDashboardUrl(url) {
  if (process.platform === 'darwin') {
    spawnSync('open', [url], { stdio: 'ignore' });
    return;
  }

  if (process.platform === 'win32') {
    spawnSync('cmd', ['/c', 'start', '', url], { stdio: 'ignore', windowsHide: true });
    return;
  }

  spawnSync('xdg-open', [url], { stdio: 'ignore' });
}

function runOpenclaw(args) {
  const candidates = process.platform === 'win32' ? ['openclaw.cmd', 'openclaw'] : ['openclaw'];

  for (const cmd of candidates) {
    try {
      const result = spawnSync(cmd, args, {
        env: sanitizeSpawnEnv(process.env),
        encoding: 'utf-8',
        timeout: 120000,
        windowsHide: true,
        stdio: ['ignore', 'pipe', 'pipe'],
      });
      if (!result.error || result.error.code !== 'ENOENT') {
        return result;
      }
    } catch (_) {
      // Try the next candidate command.
    }
  }

  return { status: 1, stdout: '', stderr: 'openclaw command not found in PATH.' };
}

function isPidRunning(pid) {
  if (!Number.isInteger(pid) || pid <= 0) {
    return false;
  }
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return Boolean(error && error.code === 'EPERM');
  }
}

function cleanupStaleGatewayLocks(homeDir) {
  const tmpRoot = path.join(process.env.TEMP || process.env.TMP || os.tmpdir(), 'openclaw');
  if (!fs.existsSync(tmpRoot)) {
    return { scanned: 0, removed: 0 };
  }

  let scanned = 0;
  let removed = 0;
  const targetConfigPath = homeDir ? path.join(homeDir, 'openclaw.json').toLowerCase() : '';

  for (const name of fs.readdirSync(tmpRoot)) {
    if (!/^gateway.*\.lock$/i.test(name)) {
      continue;
    }
    scanned += 1;
    const filePath = path.join(tmpRoot, name);

    try {
      const raw = fs.readFileSync(filePath, 'utf8');
      const parsed = JSON.parse(raw);
      const lockConfig = String(parsed && parsed.configPath ? parsed.configPath : '').toLowerCase();
      if (targetConfigPath && lockConfig && lockConfig !== targetConfigPath) {
        continue;
      }

      const pid = Number(parsed && parsed.pid);
      if (isPidRunning(pid)) {
        continue;
      }

      fs.removeSync(filePath);
      removed += 1;
    } catch (_) {
      // Corrupted lock files are safe to remove.
      try {
        fs.removeSync(filePath);
        removed += 1;
      } catch (_) {
        // ignore
      }
    }
  }

  return { scanned, removed };
}

function launchWindowsGatewayFallback(homeDir) {
  if (process.platform !== 'win32') {
    return false;
  }

  const env = { ...process.env };
  if (process.env.APPDATA) {
    const npmBin = path.join(process.env.APPDATA, 'npm');
    env.PATH = `${npmBin};${env.PATH || ''}`;
  }
  const safeEnv = sanitizeSpawnEnv(env);

  const gatewayCmd = path.join(homeDir, 'gateway.cmd');
  if (fs.existsSync(gatewayCmd)) {
    try {
      const content = fs.readFileSync(gatewayCmd, 'utf8');
      const lines = content
        .split(/\r?\n/)
        .map((line) => String(line || '').trim())
        .filter(Boolean);

      const launchLine = lines
        .slice()
        .reverse()
        .find((line) => !/^@?echo\b/i.test(line) && !/^rem\b/i.test(line) && !/^set\s+/i.test(line));

      if (launchLine) {
        const match = launchLine.match(/^("[^"]+"|\S+)\s+"([^"]+)"(?:\s+(.*))?$/);
        if (match) {
          const exe = String(match[1]).replace(/^"|"$/g, '');
          const scriptPath = String(match[2]);
          const tail = String(match[3] || '').trim();
          const tailArgs = tail ? (tail.match(/"[^"]*"|\S+/g) || []).map((arg) => arg.replace(/^"|"$/g, '')) : [];

          const launcher = spawn(exe, [scriptPath, ...tailArgs], {
            env: safeEnv,
            shell: false,
            windowsHide: true,
            detached: true,
            stdio: 'ignore',
          });
          launcher.unref();
          return true;
        }
      }
    } catch (_) {
      // Fall through to command-string launcher.
    }
  }

  // Preferred path: detached gateway run command. This matches the dashboard guidance
  // and avoids brittle login-item/service state when the service is installed but idle.
  try {
    const tmpRoot = path.join(process.env.TEMP || process.env.TMP || os.tmpdir(), 'openclaw');
    fs.ensureDirSync(tmpRoot);
    const runLogPath = path.join(tmpRoot, 'gateway-detached.log');
    const launchCmd = `openclaw gateway run --port 18789 > "${runLogPath}" 2>&1`;
    const comspec = process.env.ComSpec || 'cmd.exe';
    const launcher = spawn(comspec, ['/d', '/c', launchCmd], {
      env: safeEnv,
      shell: false,
      windowsHide: true,
      detached: true,
      stdio: 'ignore',
    });
    launcher.unref();
    return true;
  } catch (_) {
    // Fall through to gateway.cmd launcher.
  }

  const gatewayCmdPath = path.join(homeDir, 'gateway.cmd');
  if (!fs.existsSync(gatewayCmdPath)) {
    return false;
  }

  try {
    const launcher = spawn(gatewayCmdPath, [], {
      env: safeEnv,
      shell: true,
      windowsHide: true,
      detached: true,
      stdio: 'ignore',
    });
    launcher.unref();
    return true;
  } catch (_) {
    return false;
  }
}

function probeGateway(timeoutMs = 1200) {
  return new Promise((resolve) => {
    const req = http.get(
      {
        host: '127.0.0.1',
        port: 18789,
        path: '/healthz',
        timeout: timeoutMs,
      },
      (res) => {
        const ok = (res.statusCode || 0) >= 200 && (res.statusCode || 0) < 500;
        res.resume();
        resolve(ok);
      },
    );

    req.on('timeout', () => {
      req.destroy();
      resolve(false);
    });

    req.on('error', () => resolve(false));
  });
}

async function waitForGatewayReady(timeoutMs = 30000, intervalMs = 1000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    // eslint-disable-next-line no-await-in-loop
    const healthy = await probeGateway();
    if (healthy) return true;
    // eslint-disable-next-line no-await-in-loop
    await new Promise((resolve) => setTimeout(resolve, intervalMs));
  }
  return false;
}

async function ensureGatewayReadyForDashboard(homeDir) {
  if (await probeGateway()) {
    return true;
  }

  console.log('   ⚙️ Gateway offline. Starting OpenClaw gateway...');
  const cleanedBeforeStart = cleanupStaleGatewayLocks(homeDir);
  if (cleanedBeforeStart.removed > 0) {
    console.log(`   🧹 Removed ${cleanedBeforeStart.removed} stale gateway lock file(s).`);
  }
  let healthy = false;

  if (process.platform === 'win32') {
    const gatewayCmd = path.join(homeDir, 'gateway.cmd');
    if (!fs.existsSync(gatewayCmd)) {
      const install = runOpenclaw(['gateway', 'install']);
      if (install.stdout && String(install.stdout).trim()) {
        console.log(String(install.stdout).trim());
      }
      if (install.stderr && String(install.stderr).trim()) {
        console.error(String(install.stderr).trim());
      }
    }

    const fallbackLaunched = launchWindowsGatewayFallback(homeDir);
    if (fallbackLaunched) {
      console.log('   ⚙️ Gateway launch requested. Waiting for gateway...');
      healthy = await waitForGatewayReady(30000, 1000);
    }

    if (!healthy) {
      const cleanedBeforeRetry = cleanupStaleGatewayLocks(homeDir);
      if (cleanedBeforeRetry.removed > 0) {
        console.log(`   🧹 Removed ${cleanedBeforeRetry.removed} stale gateway lock file(s) before retry.`);
      }

      const start = runOpenclaw(['gateway', 'start']);
      if (start.stdout && String(start.stdout).trim()) {
        console.log(String(start.stdout).trim());
      }
      if (start.stderr && String(start.stderr).trim()) {
        console.error(String(start.stderr).trim());
      }
      healthy = await waitForGatewayReady(15000, 1000);
    }
  } else {
    const start = runOpenclaw(['gateway', 'start']);
    if (start.stdout && String(start.stdout).trim()) {
      console.log(String(start.stdout).trim());
    }
    if (start.stderr && String(start.stderr).trim()) {
      console.error(String(start.stderr).trim());
    }
    if (typeof start.status === 'number' && start.status !== 0) {
      console.error(`   ⚠️ openclaw gateway start exited with code ${start.status}.`);
    }
    healthy = await waitForGatewayReady(35000, 1000);
  }

  if (!healthy) {
    console.error('   ❌ Gateway is still offline after startup attempt.');
    console.error('   Tip: run `openclaw gateway start` and then retry Open Dashboard.');
  }
  return healthy;
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const homeDir = args.home || process.env.OPENCLAW_HOME || path.join(os.homedir(), '.openclaw');
  const configPath = path.join(homeDir, 'openclaw.json');

  if (!(await fs.pathExists(configPath))) {
    return;
  }

  let config;
  try {
    config = await fs.readJson(configPath);
  } catch (_) {
    return;
  }

  config.gateway = config.gateway || {};
  config.gateway.auth = config.gateway.auth || {};

  if (!config.gateway.auth.token || !String(config.gateway.auth.token).trim()) {
    config.gateway.auth.token = crypto.randomBytes(24).toString('hex');
    await fs.writeJson(configPath, config, { spaces: 2 });
    console.log('   🔐 Generated missing OpenClaw gateway token.');
  }

  const token = config.gateway.auth.token;
  const dashboardUrl = `http://127.0.0.1:18789/#token=${token}`;
  console.log(`   🌐 Dashboard URL (tokenized): ${dashboardUrl}`);

  if (process.platform === 'win32') {
    spawnSync('clip', { input: dashboardUrl, encoding: 'utf-8', stdio: ['pipe', 'ignore', 'ignore'] });
  }

  if (process.platform === 'darwin') {
    // Best-effort clipboard copy for faster UX.
    spawnSync('pbcopy', { input: dashboardUrl, encoding: 'utf-8', stdio: ['pipe', 'ignore', 'ignore'] });
  }

  if (args.open || process.env.RECLAW_OPEN_DASHBOARD === '1') {
    const ready = await ensureGatewayReadyForDashboard(homeDir);
    if (!ready) {
      process.exitCode = 1;
      return;
    }
    openDashboardUrl(dashboardUrl);
  }
}

main().catch(() => process.exit(0));
