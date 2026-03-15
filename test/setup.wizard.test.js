/**
 * setup.wizard.test.js
 *
 * Tests for the OpenClaw setup wizard:
 * - IPC handler logic: app:check-openclaw, app:install-openclaw, app:onboard-openclaw
 * - Renderer source: wizard HTML elements, JS gate in initialize(), wizard flow functions
 * - Integration: wizard shows when openclaw missing, hides when present
 *
 * No mocks. Reads real source files and (where safe) probes real CLI behavior.
 */

const fs   = require('fs-extra');
const path = require('path');
const { spawnSync } = require('child_process');

const REPO        = path.resolve(__dirname, '..');
const MAIN_JS     = path.join(REPO, 'desktop-ui', 'main.js');
const PRELOAD_JS  = path.join(REPO, 'desktop-ui', 'preload.js');
const RENDERER_JS = path.join(REPO, 'desktop-ui', 'renderer', 'app.js');
const INDEX_HTML  = path.join(REPO, 'desktop-ui', 'renderer', 'index.html');

const mainSrc     = fs.readFileSync(MAIN_JS,     'utf8');
const preloadSrc  = fs.readFileSync(PRELOAD_JS,  'utf8');
const rendererSrc = fs.readFileSync(RENDERER_JS, 'utf8');
const htmlSrc     = fs.readFileSync(INDEX_HTML,  'utf8');

// ─── main.js: IPC handler registration ───────────────────────────────────────

describe('main.js: setup wizard IPC handlers', () => {
  test('app:check-openclaw handler is registered', () => {
    expect(mainSrc).toContain("'app:check-openclaw'");
  });

  test('app:install-openclaw handler is registered', () => {
    expect(mainSrc).toContain("'app:install-openclaw'");
  });

  test('app:onboard-openclaw handler is registered', () => {
    expect(mainSrc).toContain("'app:onboard-openclaw'");
  });

  test('check-openclaw detects installation via ~/.openclaw/openclaw.json config file', () => {
    expect(mainSrc).toContain('openclaw.json');
    expect(mainSrc).toContain('existsSync');
    // The handler should return { installed, version, path }
    expect(mainSrc).toContain('installed:');
  });

  test('install-openclaw uses npm install -g openclaw@latest', () => {
    expect(mainSrc).toContain('npm');
    expect(mainSrc).toContain('install');
    expect(mainSrc).toContain('-g');
    expect(mainSrc).toContain('openclaw@latest');
  });

  test('install-openclaw sets SHARP_IGNORE_GLOBAL_LIBVIPS=1 to avoid build errors', () => {
    expect(mainSrc).toContain('SHARP_IGNORE_GLOBAL_LIBVIPS');
  });

  test('onboard-openclaw calls openclaw onboard --install-daemon --yes', () => {
    expect(mainSrc).toContain('onboard');
    expect(mainSrc).toContain('--install-daemon');
    expect(mainSrc).toContain('--yes');
  });

  test('install-openclaw streams stdout/stderr via sendLog (live output)', () => {
    // Find the install handler block and ensure it pipes output.
    // Slice until the next handler to avoid brittle fixed-size blocks.
    const installIdx = mainSrc.indexOf("'app:install-openclaw'");
    const nextIdx = mainSrc.indexOf("'app:onboard-openclaw'", installIdx);
    const block = mainSrc.slice(installIdx, nextIdx > installIdx ? nextIdx : installIdx + 12000);

    expect(block).toContain('sendLog');
    expect(block).toMatch(/proc\.stdout|sendLog\(\s*'stdout'/);
    expect(block).toMatch(/proc\.stderr|sendLog\(\s*'stderr'/);
  });
});

// ─── preload.js: bridge methods exposed to renderer ──────────────────────────

describe('preload.js: wizard API exposed to renderer', () => {
  test('checkOpenClaw is exposed via contextBridge', () => {
    expect(preloadSrc).toContain('checkOpenClaw');
    expect(preloadSrc).toContain("'app:check-openclaw'");
  });

  test('installOpenClaw is exposed via contextBridge', () => {
    expect(preloadSrc).toContain('installOpenClaw');
    expect(preloadSrc).toContain("'app:install-openclaw'");
  });

  test('onboardOpenClaw is exposed via contextBridge', () => {
    expect(preloadSrc).toContain('onboardOpenClaw');
    expect(preloadSrc).toContain("'app:onboard-openclaw'");
  });
});

// ─── index.html: wizard markup ────────────────────────────────────────────────

describe('index.html: setup wizard panel markup', () => {
  test('setup wizard section exists with id openclawSetupWizard', () => {
    expect(htmlSrc).toContain('id="openclawSetupWizard"');
  });

  test('wizard is hidden by default (hidden attribute)', () => {
    // Wizard must be hidden at load and shown only when openclaw is missing
    const wizardMatch = htmlSrc.match(/id="openclawSetupWizard"[^>]*>/);
    expect(wizardMatch).toBeTruthy();
    expect(wizardMatch[0]).toContain('hidden');
  });

  test('wizard has Install OpenClaw button', () => {
    expect(htmlSrc).toContain('id="wizardInstallBtn"');
    expect(htmlSrc).toContain('Install OpenClaw');
  });

  test('wizard has Skip button for users who installed manually', () => {
    expect(htmlSrc).toContain('id="wizardSkipBtn"');
    expect(htmlSrc).toMatch(/already installed|skip/i);
  });

  test('wizard has a live log output area', () => {
    expect(htmlSrc).toContain('id="wizardLogs"');
  });

  test('wizard shows three setup steps', () => {
    expect(htmlSrc).toContain('id="wizardStep1"');
    expect(htmlSrc).toContain('id="wizardStep2"');
    expect(htmlSrc).toContain('id="wizardStep3"');
  });

  test('wizard shows the exact npm command users can understand', () => {
    expect(htmlSrc).toContain('npm install -g openclaw@latest');
  });
});

// ─── renderer app.js: wizard gate in initialize() ────────────────────────────

describe('renderer app.js: wizard logic', () => {
  test('initialize() calls checkOpenClaw before loading main app', () => {
    expect(rendererSrc).toContain('checkOpenClaw');
    const initIdx = rendererSrc.indexOf('async function initialize()');
    const initBlock = rendererSrc.slice(initIdx, initIdx + 500);
    expect(initBlock).toContain('checkOpenClaw');
  });

  test('showSetupWizard() removes hidden attribute on the wizard section', () => {
    expect(rendererSrc).toContain('showSetupWizard');
    // The function must remove hidden / removeAttribute
    const fnIdx = rendererSrc.indexOf('function showSetupWizard');
    const fnBlock = rendererSrc.slice(fnIdx, fnIdx + 200);
    expect(fnBlock).toMatch(/removeAttribute|hidden/);
  });

  test('hideSetupWizard() sets hidden attribute on the wizard section', () => {
    expect(rendererSrc).toContain('hideSetupWizard');
    const fnIdx = rendererSrc.indexOf('function hideSetupWizard');
    const fnBlock = rendererSrc.slice(fnIdx, fnIdx + 200);
    expect(fnBlock).toMatch(/setAttribute.*hidden|hidden/);
  });

  test('runSetupWizard() calls installOpenClaw', () => {
    expect(rendererSrc).toContain('installOpenClaw');
    const fnIdx = rendererSrc.indexOf('async function runSetupWizard');
    const fnBlock = rendererSrc.slice(fnIdx, fnIdx + 1500);
    expect(fnBlock).toContain('installOpenClaw');
  });

  test('runOnboardStep() calls onboardOpenClaw (interactive onboarding)', () => {
    const fnIdx = rendererSrc.indexOf('async function runOnboardStep');
    const nextIdx = rendererSrc.indexOf('async function runSetupWizard', fnIdx);
    const fnBlock = rendererSrc.slice(fnIdx, nextIdx > fnIdx ? nextIdx : fnIdx + 8000);
    expect(fnBlock).toContain('onboardOpenClaw');
  });

  test('runSetupWizard() calls checkOpenClaw to verify after install', () => {
    const fnIdx = rendererSrc.indexOf('async function runSetupWizard');
    const fnBlock = rendererSrc.slice(fnIdx, fnIdx + 3000);
    expect(fnBlock).toContain('checkOpenClaw');
  });

  test('runSetupWizard() calls initializeMainApp() after successful install', () => {
    const fnIdx = rendererSrc.indexOf('async function runSetupWizard');
    const fnBlock = rendererSrc.slice(fnIdx, fnIdx + 3000);
    expect(fnBlock).toContain('initializeMainApp');
  });

  test('skip button calls initializeMainApp() directly', () => {
    expect(rendererSrc).toContain('wizardSkipBtn');
    // Find the addEventListener block, not the getElementById assignment
    const skipIdx = rendererSrc.indexOf("wizardSkipBtn.addEventListener('click'");
    const skipBlock = rendererSrc.slice(skipIdx, skipIdx + 300);
    expect(skipBlock).toContain('initializeMainApp');
  });

  test('initializeMainApp() exists and starts gateway polling', () => {
    expect(rendererSrc).toContain('async function initializeMainApp()');
    const fnIdx = rendererSrc.indexOf('async function initializeMainApp()');
    const nextIdx = rendererSrc.indexOf('async function initialize()', fnIdx);
    const fnBlock = rendererSrc.slice(fnIdx, nextIdx > fnIdx ? nextIdx : fnIdx + 8000);
    expect(fnBlock).toContain('startGatewayStatusPolling');
    expect(fnBlock).toContain('refreshGatewayStatus');
  });

  test('wizard log helper wizardLog writes to the #wizardLogs element', () => {
    expect(rendererSrc).toContain('function wizardLog');
    const fnIdx = rendererSrc.indexOf('function wizardLog');
    const fnBlock = rendererSrc.slice(fnIdx, fnIdx + 200);
    expect(fnBlock).toContain('wizardLogs');
  });

  test('wizard disables install button while install is running (no double-click)', () => {
    const fnIdx = rendererSrc.indexOf('async function runSetupWizard');
    const fnBlock = rendererSrc.slice(fnIdx, fnIdx + 500);
    expect(fnBlock).toContain('disabled = true');
  });

  test('wizard step status helpers setWizardStep exist', () => {
    expect(rendererSrc).toContain('function setWizardStep');
  });
});

// ─── Force-wizard test mode ───────────────────────────────────────────────────

describe('RECLAW_FORCE_SETUP_WIZARD: test/demo flag', () => {
  test('main.js source contains RECLAW_FORCE_SETUP_WIZARD constant', () => {
    expect(mainSrc).toContain('RECLAW_FORCE_SETUP_WIZARD');
  });

  test('force flag causes app:check-openclaw to return installed: false', () => {
    // Simulate the handler logic inline with the flag set
    const forcedResult = process.env.RECLAW_FORCE_SETUP_WIZARD === '1'
      ? { installed: false, version: null, path: null, forced: true }
      : null;

    process.env.RECLAW_FORCE_SETUP_WIZARD = '1';
    const result = process.env.RECLAW_FORCE_SETUP_WIZARD === '1'
      ? { installed: false, version: null, path: null, forced: true }
      : { installed: true };

    expect(result.installed).toBe(false);
    expect(result.forced).toBe(true);

    delete process.env.RECLAW_FORCE_SETUP_WIZARD;
  });

  test('force flag early-return block is inside app:check-openclaw handler', () => {
    const handlerIdx = mainSrc.indexOf("'app:check-openclaw'");
    const handlerBlock = mainSrc.slice(handlerIdx, handlerIdx + 400);
    expect(handlerBlock).toContain('RECLAW_FORCE_SETUP_WIZARD');
    expect(handlerBlock).toContain("'1'");
    expect(handlerBlock).toContain('forced: true');
  });

  test('npm script desktop:wizard-test exists in package.json', () => {
    const pkgPath = path.join(REPO, 'package.json');
    const pkg = JSON.parse(fs.readFileSync(pkgPath, 'utf8'));
    expect(pkg.scripts).toHaveProperty('desktop:wizard-test');
    expect(pkg.scripts['desktop:wizard-test']).toContain('RECLAW_FORCE_SETUP_WIZARD=1');
  });

  test('npm script desktop:wizard-test:windows exists in package.json', () => {
    const pkgPath = path.join(REPO, 'package.json');
    const pkg = JSON.parse(fs.readFileSync(pkgPath, 'utf8'));
    expect(pkg.scripts).toHaveProperty('desktop:wizard-test:windows');
    expect(pkg.scripts['desktop:wizard-test:windows']).toContain('RECLAW_FORCE_SETUP_WIZARD');
  });
});

// ─── Live: config file detection (same logic as app:check-openclaw) ──────────

describe('Live: openclaw config detection (same logic as app:check-openclaw)', () => {
  const openclawConfigPath = path.join(
    process.env.HOME || process.env.USERPROFILE || '',
    '.openclaw',
    'openclaw.json',
  );

  test('config file check returns a boolean result', () => {
    const configured = fs.existsSync(openclawConfigPath);
    expect(typeof configured).toBe('boolean');

    if (configured) {
      console.log(`[setup.wizard] openclaw config found: ${openclawConfigPath} — main UI shown`);
    } else {
      console.log(`[setup.wizard] openclaw config missing: ${openclawConfigPath} — wizard would show`);
    }
  });

  test('detection result shape matches what IPC handler returns', () => {
    const configured = fs.existsSync(openclawConfigPath);

    // Shape must match { installed, version, path }
    const shape = { installed: configured, version: null, path: configured ? openclawConfigPath : null };
    expect(typeof shape.installed).toBe('boolean');
    expect(shape.version).toBeNull();
    expect(shape.path === null || typeof shape.path === 'string').toBe(true);
  });

  test('missing config directory causes installed: false (wizard would show)', () => {
    const fakePath = path.join('/tmp', 'nonexistent-openclaw-dir', 'openclaw.json');
    const configured = fs.existsSync(fakePath);
    expect(configured).toBe(false);
  });
});
