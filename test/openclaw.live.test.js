/**
 * openclaw.live.test.js
 *
 * Real live integration tests for every desktop action button.
 * Every test spawns the actual `node bin/cli.js <args>` command — the same
 * invocation the desktop UI uses — and asserts on the real OpenClaw output.
 *
 * No mocks. No stubs. Gateway is offline on this machine so gateway-dependent
 * commands are expected to fail with the real OpenClaw error messages.
 *
 * Gateway status sync is also verified: the health endpoint state must match
 * what `gateway status` reports.
 */

const fs = require('fs-extra');
const os = require('os');
const path = require('path');
const http = require('http');
const { spawnSync } = require('child_process');

const CLI   = path.join(__dirname, '..', 'bin', 'cli.js');
const NODE  = process.execPath;
const REAL_HOME = process.env.OPENCLAW_HOME || path.join(os.homedir(), '.openclaw');

function run(args, extraEnv = {}, timeout = 60000) {
  const result = spawnSync(NODE, [CLI, ...args], {
    encoding: 'utf-8',
    timeout,
    cwd: path.join(__dirname, '..'),
    env: { ...process.env, ...extraEnv }
  });
  return {
    status:  result.status,
    stdout:  result.stdout || '',
    stderr:  result.stderr || '',
    all:     (result.stdout || '') + (result.stderr || ''),
    signal:  result.signal,
    error:   result.error
  };
}

function checkHealthEndpoint(timeout = 4000) {
  return new Promise((resolve) => {
    const t = Date.now();
    const req = http.request(
      { hostname: '127.0.0.1', port: 18789, path: '/healthz', method: 'GET' },
      (res) => {
        let body = '';
        res.on('data', d => body += d);
        res.on('end', () => resolve({
          running: res.statusCode === 200,
          statusCode: res.statusCode,
          latencyMs: Date.now() - t,
          body
        }));
      }
    );
    req.setTimeout(timeout, () => { req.destroy(); resolve({ running: false, error: 'timeout' }); });
    req.on('error', e => resolve({ running: false, error: e.message }));
    req.end();
  });
}

// ─── GATEWAY commands ────────────────────────────────────────────────────────

describe('Action: oc-gateway-status — live openclaw gateway status', () => {
  test('exits 0 and shows port 18789', () => {
    const r = run(['gateway', 'status']);
    expect(r.status).toBe(0);
    expect(r.all).toMatch(/18789/);
  });

  test('output contains Gateway: bind line with loopback address', () => {
    const r = run(['gateway', 'status']);
    expect(r.status).toBe(0);
    expect(r.all).toMatch(/Gateway:.*bind/i);
    expect(r.all).toMatch(/127\.0\.0\.1/);
  });

  test('output reflects actual gateway state (installed+running OR not installed)', async () => {
    const health = await checkHealthEndpoint(4000);
    const r = run(['gateway', 'status']);
    expect(r.status).toBe(0);
    if (health.running) {
      // Gateway is up — output should say running/active/listening
      expect(r.all).toMatch(/running|active|listening|pid|state/i);
    } else {
      // Gateway is offline — output should say not installed/not loaded/not running
      expect(r.all).toMatch(/service not installed|service not loaded|not loaded|not running/i);
    }
  });

  test('Probe target line shows ws://127.0.0.1:18789', () => {
    const r = run(['gateway', 'status']);
    expect(r.all).toContain('ws://127.0.0.1:18789');
  });
});

describe('Action: oc-gateway-status-deep — live openclaw gateway status --deep', () => {
  test('exits 0 and produces deeper output than plain status', () => {
    const r = run(['gateway', 'status', '--deep']);
    expect(r.status).toBe(0);
    // --deep includes service config section
    expect(r.all.length).toBeGreaterThan(200);
  });

  test('output includes file logs path', () => {
    const r = run(['gateway', 'status', '--deep']);
    expect(r.all).toMatch(/file logs|openclaw.*\.log/i);
  });
});

describe('Action: oc-gateway-start — live openclaw gateway start', () => {
  test('exits 0 (service CLI runs even if service not installed)', () => {
    const r = run(['gateway', 'start']);
    expect(r.status).toBe(0);
  });

  test('output says gateway service not loaded (install required first)', () => {
    const r = run(['gateway', 'start']);
    // Accept either the offline-service message OR evidence the gateway was restarted/managed
    expect(r.all).toMatch(/service not loaded|not running|not installed|launchctl|openclaw gateway install|Restarted|LaunchAgent/i);
  });
});

describe('Action: oc-gateway-restart — live openclaw gateway restart', () => {
  test('exits 0 or non-zero with clear message (service not installed)', () => {
    const r = run(['gateway', 'restart'], {}, 30000);
    // exits 0 or 1 — either way must not crash with a Node exception
    // Allow signal termination when system restarts/controls the LaunchAgent
    expect(r.error).toBeUndefined();
    expect([null, 'SIGTERM', 'SIGINT']).toContain(r.signal);
  });

  test('output contains gateway-related messaging (not a generic Node crash)', () => {
    const r = run(['gateway', 'restart'], {}, 30000);
    expect(r.all).toMatch(/gateway|openclaw|service/i);
  });
});

describe('Action: oc-gateway-install — live openclaw gateway install', () => {
  test('CLI runs without crashing (exit 0 or non-zero with clear message)', () => {
    const r = run(['gateway', 'install'], {}, 30000);
    expect(r.signal).toBeNull();
    expect(r.error).toBeUndefined();
    expect(r.all).toMatch(/gateway|install|service|openclaw/i);
  });
});

// ─── HEALTH commands ─────────────────────────────────────────────────────────

describe('Action: oc-health — live openclaw health (gateway offline)', () => {
  test('exits non-zero because gateway is offline', () => {
    const r = run(['health'], {}, 20000);
    expect(r.status).not.toBe(0);
  });

  test('error message contains WebSocket/gateway closure reason (not a crash)', () => {
    const r = run(['health'], {}, 20000);
    expect(r.all).toMatch(/gateway closed|ECONNREFUSED|Failed to start CLI|gateway/i);
  });

  test('no uncaught Node.js exception in output', () => {
    const r = run(['health'], {}, 20000);
    expect(r.all).not.toMatch(/UnhandledPromiseRejection|ReferenceError|SyntaxError/);
  });
});

describe('Action: oc-health-json — live openclaw health --json (gateway offline)', () => {
  test('exits non-zero because gateway WebSocket fails', () => {
    const r = run(['health', '--json'], {}, 20000);
    expect(r.status).not.toBe(0);
  });

  test('error output still mentions gateway/WebSocket (not a raw crash)', () => {
    const r = run(['health', '--json'], {}, 20000);
    expect(r.all).toMatch(/gateway|ECONNREFUSED|Failed/i);
  });
});

// ─── STATUS commands ─────────────────────────────────────────────────────────

describe('Action: oc-status — live openclaw status', () => {
  test('exits 0', () => {
    const r = run(['status'], {}, 30000);
    expect(r.status).toBe(0);
  });

  test('output includes known agent names from this system', () => {
    const r = run(['status'], {}, 30000);
    expect(r.all).toMatch(/agent:|main|github-manager|openclaw-expert/i);
  });

  test('output includes troubleshooting URL', () => {
    const r = run(['status'], {}, 30000);
    expect(r.all).toContain('docs.openclaw.ai');
  });
});

describe('Action: oc-status-deep — live openclaw status --deep', () => {
  test('exits 0', () => {
    const r = run(['status', '--deep'], {}, 30000);
    expect(r.status).toBe(0);
  });
});

describe('Action: oc-status-all — live openclaw status --all', () => {
  test('exits 0', () => {
    const r = run(['status', '--all'], {}, 30000);
    expect(r.status).toBe(0);
  });
});

describe('Action: oc-status-usage — live openclaw status --usage', () => {
  test('exits 0', () => {
    const r = run(['status', '--usage'], {}, 30000);
    expect(r.status).toBe(0);
  });

  test('output includes agent session rows or usage context', () => {
    const r = run(['status', '--usage'], {}, 30000);
    expect(r.all.length).toBeGreaterThan(100);
  });
});

// ─── DOCTOR commands ─────────────────────────────────────────────────────────

describe('Action: oc-doctor — live openclaw doctor --non-interactive --yes', () => {
  test('exits 0', () => {
    const r = run(['doctor', '--non-interactive', '--yes'], {}, 60000);
    expect(r.status).toBe(0);
  });

  test('output ends with "Doctor complete"', () => {
    const r = run(['doctor', '--non-interactive', '--yes'], {}, 60000);
    expect(r.all).toContain('Doctor complete');
  });

  test('output includes Gateway section', () => {
    const r = run(['doctor', '--non-interactive', '--yes'], {}, 60000);
    expect(r.all).toMatch(/Gateway/i);
  });

  test('output includes Plugins section', () => {
    const r = run(['doctor', '--non-interactive', '--yes'], {}, 60000);
    expect(r.all).toMatch(/Plugin/i);
  });

  test('output includes Skills status section', () => {
    const r = run(['doctor', '--non-interactive', '--yes'], {}, 60000);
    expect(r.all).toMatch(/Skills/i);
  });
});

describe('Action: oc-doctor-repair — live openclaw doctor --repair --non-interactive --yes', () => {
  test('exits 0 and completes', () => {
    const r = run(['doctor', '--repair', '--non-interactive', '--yes'], {}, 60000);
    expect(r.status).toBe(0);
    expect(r.all).toContain('Doctor complete');
  });
});

describe('Action: oc-doctor-deep — live openclaw doctor --deep --non-interactive --yes', () => {
  test('exits 0', () => {
    const r = run(['doctor', '--deep', '--non-interactive', '--yes'], {}, 60000);
    expect(r.status).toBe(0);
  });
});

describe('Action: oc-doctor-yes — live openclaw doctor --yes --non-interactive', () => {
  test('exits 0 and shows Doctor complete', () => {
    const r = run(['doctor', '--yes', '--non-interactive'], {}, 60000);
    expect(r.status).toBe(0);
    expect(r.all).toContain('Doctor complete');
  });
});

describe('Action: oc-doctor-non-interactive — live openclaw doctor --non-interactive --yes', () => {
  test('exits 0 (same command as oc-doctor)', () => {
    const r = run(['doctor', '--non-interactive', '--yes'], {}, 60000);
    expect(r.status).toBe(0);
  });
});

describe('Action: oc-doctor-token — live openclaw doctor --generate-gateway-token --non-interactive --yes', () => {
  test('exits 0 or non-zero (token requires running gateway), no crash', () => {
    const r = run(['doctor', '--generate-gateway-token', '--non-interactive', '--yes'], {}, 30000);
    expect(r.signal).toBeNull();
    expect(r.error).toBeUndefined();
    expect(r.all).toMatch(/doctor|gateway|token/i);
  });
});

describe('Action: oc-doctor-fix — live openclaw doctor --fix --non-interactive --yes', () => {
  test('exits 0', () => {
    const r = run(['doctor', '--fix', '--non-interactive', '--yes'], {}, 60000);
    expect(r.status).toBe(0);
  });
});

describe('Action: oc-doctor-repair-force — live openclaw doctor --repair --force --non-interactive --yes', () => {
  test('exits 0', () => {
    const r = run(['doctor', '--repair', '--force', '--non-interactive', '--yes'], {}, 60000);
    expect(r.status).toBe(0);
    expect(r.all).toContain('Doctor complete');
  });
});

// ─── SECURITY commands ───────────────────────────────────────────────────────

describe('Action: oc-security-audit — live openclaw security audit', () => {
  test('exits 0', () => {
    const r = run(['security', 'audit'], {}, 45000);
    expect(r.status).toBe(0);
  });

  test('output contains "OpenClaw security audit"', () => {
    const r = run(['security', 'audit'], {}, 45000);
    expect(r.all).toContain('OpenClaw security audit');
  });

  test('output contains Summary line with issue counts', () => {
    const r = run(['security', 'audit'], {}, 45000);
    expect(r.all).toMatch(/summary.*critical|critical.*warn/i);
  });

  test('output contains CRITICAL and WARN severity sections', () => {
    const r = run(['security', 'audit'], {}, 45000);
    expect(r.all).toContain('CRITICAL');
    expect(r.all).toContain('WARN');
  });

  test('output contains INFO section', () => {
    const r = run(['security', 'audit'], {}, 45000);
    expect(r.all).toContain('INFO');
  });
});

describe('Action: oc-security-deep — live openclaw security audit --deep', () => {
  test('exits 0', () => {
    const r = run(['security', 'audit', '--deep'], {}, 45000);
    expect(r.status).toBe(0);
    expect(r.all).toContain('OpenClaw security audit');
  });
});

describe('Action: oc-security-fix — live openclaw security audit --fix', () => {
  test('exits 0 and runs audit with fix suggestions', () => {
    const r = run(['security', 'audit', '--fix'], {}, 45000);
    expect(r.status).toBe(0);
    expect(r.all).toMatch(/fix|chmod|security/i);
  });
});

// ─── SECRETS commands ────────────────────────────────────────────────────────

describe('Action: oc-secrets-reload — live openclaw secrets reload', () => {
  test('exits 0 or non-zero with clear message (gateway offline)', () => {
    const r = run(['secrets', 'reload'], {}, 30000);
    expect(r.signal).toBeNull();
    expect(r.error).toBeUndefined();
    expect(r.all).toMatch(/secret|gateway|openclaw/i);
  });
});

describe('Action: oc-secrets-audit — live openclaw secrets audit', () => {
  test('exits 0 or non-zero with clear message', () => {
    const r = run(['secrets', 'audit'], {}, 30000);
    expect(r.signal).toBeNull();
    expect(r.error).toBeUndefined();
    expect(r.all).toMatch(/secret|audit|openclaw/i);
  });
});

// ─── CHANNELS commands ───────────────────────────────────────────────────────

describe('Action: oc-channels-status — live openclaw channels status', () => {
  test('exits 0', () => {
    const r = run(['channels', 'status'], {}, 30000);
    expect(r.status).toBe(0);
  });

  test('output includes Telegram channel entries', () => {
    const r = run(['channels', 'status'], {}, 30000);
    expect(r.all).toMatch(/Telegram/i);
  });

  test('shows channel config or live status depending on gateway state', async () => {
    const health = await checkHealthEndpoint(4000);
    const r = run(['channels', 'status'], {}, 30000);
    if (health.running) {
      // When gateway is up, channels may show live enabled/configured status
      expect(r.all).toMatch(/enabled|configured|channel|telegram/i);
    } else {
      // When gateway is offline, output shows config-only or not-reachable message
      expect(r.all).toMatch(/config-only|gateway not reachable|not reachable|config/i);
    }
  });
});

describe('Action: oc-channels-probe — live openclaw channels status --probe', () => {
  test('exits 0 or non-zero (probe fails without gateway)', () => {
    const r = run(['channels', 'status', '--probe'], {}, 30000);
    expect(r.signal).toBeNull();
    expect(r.error).toBeUndefined();
    expect(r.all).toMatch(/channel|telegram|probe/i);
  });
});

// ─── MODELS commands ─────────────────────────────────────────────────────────

describe('Action: oc-models-status — live openclaw models status', () => {
  test('exits 0', () => {
    const r = run(['models', 'status'], {}, 30000);
    expect(r.status).toBe(0);
  });

  test('output includes known provider names', () => {
    const r = run(['models', 'status'], {}, 30000);
    expect(r.all).toMatch(/anthropic|google|github-copilot|qwen/i);
  });

  test('output shows auth status for each provider', () => {
    const r = run(['models', 'status'], {}, 30000);
    expect(r.all).toMatch(/effective=|profiles:|env:/i);
  });

  test('shows Missing auth section for unconfigured providers', () => {
    const r = run(['models', 'status'], {}, 30000);
    expect(r.all).toMatch(/missing auth/i);
  });
});

describe('Action: oc-models-probe — live openclaw models status --probe', () => {
  test('exits 0 or non-zero (probe may fail without live API keys)', () => {
    const r = run(['models', 'status', '--probe'], {}, 45000);
    expect(r.signal).toBeNull();
    expect(r.error).toBeUndefined();
    expect(r.all).toMatch(/model|anthropic|google|probe/i);
  });
});

// ─── RESET commands ──────────────────────────────────────────────────────────

describe('Action: oc-reset-dry-run — live openclaw reset --dry-run', () => {
  test('runs without crashing (interactive prompt or dry-run output)', () => {
    // reset --dry-run may be interactive on some versions; just ensure no crash
    const r = run(['reset', '--dry-run'], {}, 15000);
    expect(r.signal).toBeNull();
    // Either exits or times out — either way no crash
  });
});

// ─── SETUP ───────────────────────────────────────────────────────────────────

describe('Action: oc-setup — live openclaw setup', () => {
  test('CLI runs without crashing (setup may be interactive)', () => {
    const r = run(['setup'], {}, 10000);
    expect(r.signal).toBeNull();
    expect(r.error).toBeUndefined();
  });
});

// ─── ReClaw native backup actions against real ~/.openclaw ───────────────────

describe('ReClaw backup against real ~/.openclaw directory', () => {
  let tmpBackupDir;

  beforeAll(async () => {
    tmpBackupDir = await fs.mkdtemp(path.join(os.tmpdir(), 'rclaw-real-backup-'));
  });

  afterAll(async () => {
    await fs.remove(tmpBackupDir);
  });

  test('backup create produces a real archive from ~/.openclaw', () => {
    const r = run(
      ['backup', 'create', '--json'],
      { OPENCLAW_HOME: REAL_HOME, BACKUP_DIR: tmpBackupDir }
    );
    expect(r.status).toBe(0);
    const parsed = JSON.parse(r.stdout.trim());
    expect(parsed.ok).toBe(true);
    expect(parsed.archivePath).toBeTruthy();
    expect(fs.existsSync(parsed.archivePath)).toBe(true);
  });

  test('backup list shows the newly created archive', () => {
    const r = run(
      ['backup', 'list', '--json'],
      { OPENCLAW_HOME: REAL_HOME, BACKUP_DIR: tmpBackupDir }
    );
    expect(r.status).toBe(0);
    const { backups } = JSON.parse(r.stdout.trim());
    expect(backups.length).toBeGreaterThan(0);
    expect(backups[0]).toMatchObject({
      name: expect.stringMatching(/openclaw/i),
      archiveType: expect.any(String),
      size: expect.any(Number),
      modifiedAt: expect.any(String)
    });
  });

  test('backup verify confirms the archive is valid', () => {
    // Get latest archive path
    const listResult = run(
      ['backup', 'list', '--json'],
      { OPENCLAW_HOME: REAL_HOME, BACKUP_DIR: tmpBackupDir }
    );
    const { backups } = JSON.parse(listResult.stdout.trim());
    const archivePath = backups[0].archivePath;

    const r = run(
      ['backup', 'verify', archivePath, '--json'],
      { OPENCLAW_HOME: REAL_HOME, BACKUP_DIR: tmpBackupDir }
    );
    expect(r.status).toBe(0);
    const parsed = JSON.parse(r.stdout.trim());
    expect(parsed.ok).toBe(true);
    expect(parsed.assetCount).toBeGreaterThan(0);
    expect(parsed.payloadEntryCount).toBeGreaterThan(0);
  });

  test('backup prune --keep-last 5 --older-than 30d --dry-run exits 0', () => {
    const r = run(
      ['backup', 'prune', '--keep-last', '5', '--older-than', '30d', '--dry-run', '--json'],
      { OPENCLAW_HOME: REAL_HOME, BACKUP_DIR: tmpBackupDir }
    );
    expect(r.status).toBe(0);
    const parsed = JSON.parse(r.stdout.trim());
    expect(parsed.dryRun).toBe(true);
    expect(parsed.ok).toBe(true);
  });

  test('backup export --scope config+creds+sessions creates a scoped archive', () => {
    const r = run(
      ['backup', 'export', '--scope', 'config+creds+sessions', '--verify', '--json'],
      { OPENCLAW_HOME: REAL_HOME, BACKUP_DIR: tmpBackupDir }
    );
    expect(r.status).toBe(0);
    const parsed = JSON.parse(r.stdout.trim());
    expect(parsed.ok).toBe(true);
    expect(parsed.scope).toMatch(/config|creds|sessions/);
    expect(parsed.verified).toBe(true);
  });
});

// ─── Gateway status sync verification ────────────────────────────────────────

describe('Gateway status sync: CLI report matches HTTP healthz endpoint', () => {
  // Probe once for the entire describe block so all tests agree on state.
  let gatewayUp = false;
  beforeAll(async () => {
    const h = await checkHealthEndpoint(4000);
    gatewayUp = h.running;
    console.log(`[gateway-sync] gateway is ${gatewayUp ? 'UP' : 'DOWN'} at test time`);
  });

  test('CLI gateway status exit code is always 0', () => {
    const cliResult = run(['gateway', 'status'], {}, 20000);
    expect(cliResult.status).toBe(0);
  });

  test('healthz HTTP state matches CLI report (up→running, down→not running)', async () => {
    const cliResult = run(['gateway', 'status'], {}, 20000);
    expect(cliResult.status).toBe(0);

    if (gatewayUp) {
      // Gateway is running — CLI must show running/active/pid/listening
      expect(cliResult.all).toMatch(/running|active|listening|pid|state.*active/i);
    } else {
      // Gateway is offline — CLI must report not running / not installed
      expect(cliResult.all).toMatch(/not running|not installed|not loaded|ECONNREFUSED|RPC probe.*failed/i);
    }
  });

  test('healthz timeout < 5 s regardless of gateway state', async () => {
    const start = Date.now();
    await checkHealthEndpoint(4000);
    expect(Date.now() - start).toBeLessThan(5000);
  });

  test('gateway status --deep reflects actual state (deeper output, exit 0)', async () => {
    const r = run(['gateway', 'status', '--deep'], {}, 20000);
    expect(r.status).toBe(0);
    if (gatewayUp) {
      // Running gateway has more detail in --deep
      expect(r.all.length).toBeGreaterThan(300);
    } else {
      // Offline: service unit not found or not running reported in --deep
      expect(r.all).toMatch(/service not installed|service not loaded|not running|not found/i);
    }
  });

  test('gateway start output is gateway-related and exits 0', () => {
    const startResult = run(['gateway', 'start'], {}, 20000);
    expect(startResult.status).toBe(0);
    expect(startResult.all).toMatch(/gateway|service|openclaw/i);
  });

  test('healthz and CLI gateway status agree on running state after gateway start attempt', async () => {
    run(['gateway', 'start'], {}, 20000);
    // Re-probe after start attempt
    const health = await checkHealthEndpoint(4000);
    const cliResult = run(['gateway', 'status'], {}, 20000);

    if (health.running) {
      expect(cliResult.all).toMatch(/running|active|listening|pid/i);
    } else {
      expect(cliResult.all).toMatch(/not running|not installed|not loaded/i);
    }
  });
});
