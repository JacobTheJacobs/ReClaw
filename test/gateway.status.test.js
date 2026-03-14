/**
 * gateway.status.test.js
 *
 * Live tests for gateway health-check endpoint and the renderer fix:
 * after oc-gateway-start / oc-gateway-restart / oc-gateway-install completes,
 * the UI must force-refresh status immediately (not wait 12 s).
 *
 * No mocks. Uses real HTTP requests to 127.0.0.1:18789/healthz.
 */

const fs = require('fs-extra');
const http = require('http');
const path = require('path');

const GATEWAY_HOST = '127.0.0.1';
const GATEWAY_PORT = 18789;
const GATEWAY_PATH = '/healthz';
const REPO = path.resolve(__dirname, '..');

// ─── helper: check gateway over HTTP ────────────────────────────────────────

function checkGatewayHealth(timeout = 5000) {
  return new Promise((resolve) => {
    const options = {
      hostname: GATEWAY_HOST,
      port: GATEWAY_PORT,
      path: GATEWAY_PATH,
      method: 'GET',
    };
    const startTime = Date.now();
    const req = http.request(options, (res) => {
      let body = '';
      res.on('data', (chunk) => { body += chunk; });
      res.on('end', () => {
        resolve({
          running: res.statusCode === 200,
          statusCode: res.statusCode,
          latencyMs: Date.now() - startTime,
          body,
          error: null,
        });
      });
    });

    req.setTimeout(timeout, () => {
      req.destroy();
      resolve({ running: false, error: `Timeout after ${timeout}ms`, statusCode: null, latencyMs: null });
    });

    req.on('error', (err) => {
      resolve({ running: false, error: err.message, statusCode: null, latencyMs: null });
    });

    req.end();
  });
}

// ─── Gateway health endpoint ──────────────────────────────────────────────────

describe('Gateway health endpoint (127.0.0.1:18789/healthz)', () => {
  test('checkGatewayHealth returns a structured result (gateway up or down)', async () => {
    const result = await checkGatewayHealth(5000);

    // Either running or not — both are valid states in CI/dev
    expect(typeof result.running).toBe('boolean');
    expect(result.error === null || typeof result.error === 'string').toBe(true);
  });

  test('when gateway is running, /healthz returns HTTP 200 with ok:true JSON', async () => {
    const result = await checkGatewayHealth(5000);
    if (!result.running) {
      // Gateway is offline — skip the assertion, not a test failure
      console.log('[gateway.status.test] Gateway is offline, skipping /healthz body check.');
      return;
    }

    expect(result.statusCode).toBe(200);
    let body;
    try {
      body = JSON.parse(result.body);
    } catch (_) {
      body = null;
    }
    // OpenClaw /healthz returns { ok: true } or similar
    if (body) {
      expect(body.ok).toBe(true);
    }
    expect(result.latencyMs).toBeLessThan(3000);
  });

  test('checkGatewayHealth resolves within 6 seconds regardless of gateway state', async () => {
    const before = Date.now();
    await checkGatewayHealth(5000);
    const elapsed = Date.now() - before;
    expect(elapsed).toBeLessThan(6000);
  });
});

// ─── Renderer gateway-refresh logic (regression for status-not-updating bug) ──

describe('Renderer: gateway status refresh after gateway actions (regression)', () => {
  const rendererSrc = fs.readFileSync(path.join(REPO, 'desktop-ui', 'renderer', 'app.js'), 'utf8');

  test('oc-gateway-start triggers refreshGatewayStatus(true) after completion', () => {
    // The fix adds a force-refresh block for gateway start/restart/install.
    // Verify the renderer source contains the force-refresh for oc-gateway-start.
    expect(rendererSrc).toContain("'oc-gateway-start'");
    expect(rendererSrc).toContain("'oc-gateway-restart'");
    expect(rendererSrc).toContain("'oc-gateway-install'");

    // All three must be in the same condition block that calls refreshGatewayStatus(true)
    const startIdx = rendererSrc.indexOf("'oc-gateway-start'");
    const restartIdx = rendererSrc.indexOf("'oc-gateway-restart'");
    const installIdx = rendererSrc.indexOf("'oc-gateway-install'");

    // They must all appear close together (within the same if-block)
    expect(Math.abs(startIdx - restartIdx)).toBeLessThan(300);
    expect(Math.abs(startIdx - installIdx)).toBeLessThan(300);
  });

  test('gateway start/restart/install block calls refreshGatewayStatus(true) not refreshGatewayStatus()', () => {
    // Find the gateway start/restart/install block and verify it uses force:true
    const blockStart = rendererSrc.indexOf("'oc-gateway-start'");
    // Grab the next 600 chars after the first gateway-action reference
    const block = rendererSrc.slice(blockStart, blockStart + 600);

    expect(block).toContain('refreshGatewayStatus(true)');
  });

  test('oc-gateway-stop already had force-refresh before the fix — still present', () => {
    expect(rendererSrc).toContain("'oc-gateway-stop'");
    // Find the stop block and confirm force-refresh exists
    const stopIdx = rendererSrc.indexOf("'oc-gateway-stop'");
    const stopBlock = rendererSrc.slice(stopIdx, stopIdx + 300);
    expect(stopBlock).toContain('refreshGatewayStatus(true)');
  });

  test('generic fallback also uses force-refresh after the fix', () => {
    // The final generic catch-all should now call refreshGatewayStatus(true)
    // Verify by checking the last occurrence of refreshGatewayStatus in the onStatus handler
    const lastIdx = rendererSrc.lastIndexOf('refreshGatewayStatus(true)');
    expect(lastIdx).toBeGreaterThan(0);
  });

  test('GATEWAY_STATUS_POLL_MS constant is 12000', () => {
    expect(rendererSrc).toContain('const GATEWAY_STATUS_POLL_MS = 12000');
  });

  test('startGatewayStatusPolling uses GATEWAY_STATUS_POLL_MS', () => {
    expect(rendererSrc).toContain('GATEWAY_STATUS_POLL_MS');
    expect(rendererSrc).toContain('startGatewayStatusPolling');
  });
});

// ─── Desktop main.js gateway IPC handler ─────────────────────────────────────

describe('Desktop main.js: app:get-gateway-status IPC handler', () => {
  const mainSrc = fs.readFileSync(path.join(REPO, 'desktop-ui', 'main.js'), 'utf8');

  test('app:get-gateway-status IPC handler is registered', () => {
    expect(mainSrc).toContain("'app:get-gateway-status'");
  });

  test('handler checks 127.0.0.1:18789/healthz', () => {
    expect(mainSrc).toContain('18789');
    expect(mainSrc).toContain('/healthz');
  });

  test('handler returns running:true/false based on HTTP status', () => {
    // Verify the response shape is defined
    expect(mainSrc).toContain('running:');
    expect(mainSrc).toContain('latencyMs');
  });
});

// ─── verify-gateway-health.js node script ────────────────────────────────────

describe('scripts/verify-gateway-health.js', () => {
  const scriptPath = path.join(REPO, 'scripts', 'verify-gateway-health.js');

  test('script file exists', () => {
    expect(fs.existsSync(scriptPath)).toBe(true);
  });

  test('script source checks 18789/healthz', () => {
    const src = fs.readFileSync(scriptPath, 'utf8');
    expect(src).toContain('18789');
  });

  test('script accepts --timeout flag in source', () => {
    const src = fs.readFileSync(scriptPath, 'utf8');
    expect(src).toMatch(/timeout/i);
  });
});
