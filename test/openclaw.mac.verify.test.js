const fs = require('fs-extra');
const path = require('path');
const { spawnSync } = require('child_process');

function run(command, args, options = {}) {
  return spawnSync(command, args, {
    encoding: 'utf-8',
    timeout: options.timeout || 180000,
    cwd: options.cwd,
    env: options.env || process.env,
    stdio: 'pipe'
  });
}

function expectSuccess(result, stepLabel) {
  const details = [
    `step=${stepLabel}`,
    `status=${result.status}`,
    `error=${result.error ? result.error.message : 'none'}`,
    `stdout=${(result.stdout || '').trim()}`,
    `stderr=${(result.stderr || '').trim()}`
  ].join('\n');

  if (result.status !== 0) {
    throw new Error(details);
  }
}

describe('OpenClaw mac restore verification', () => {
  const testIf = process.env.RUN_MAC_VERIFY_TESTS === '1' ? test : test.skip;
  const openclawRepo = process.env.OPENCLAW_REPO || '/Users/jacob/githuff/openclaw';
  const openclawHome = process.env.OPENCLAW_HOME || '/Users/jacob/.openclaw';
  const preferredNode = '/opt/homebrew/opt/node@22/bin/node';

  testIf('restored OpenClaw files and CLI health checks are valid', () => {
    expect(fs.existsSync(path.join(openclawHome, 'openclaw.json'))).toBe(true);
    expect(fs.existsSync(path.join(openclawHome, 'workspace'))).toBe(true);
    expect(fs.existsSync(path.join(openclawHome, 'agents'))).toBe(true);

    expect(fs.existsSync(path.join(openclawRepo, 'package.json'))).toBe(true);
    expect(fs.existsSync(path.join(openclawRepo, 'docs'))).toBe(true);

    const installResult = run('pnpm', ['install'], { cwd: openclawRepo, timeout: 0 });
    expectSuccess(installResult, 'pnpm install');

    if (!fs.existsSync(path.join(openclawRepo, 'dist', 'entry.js'))) {
      const buildResult = run('pnpm', ['build'], { cwd: openclawRepo, timeout: 0 });
      expectSuccess(buildResult, 'pnpm build');
    }

    const nodeBin = fs.existsSync(preferredNode) ? preferredNode : process.execPath;

    const versionResult = run(nodeBin, ['openclaw.mjs', '--version'], { cwd: openclawRepo });
    expectSuccess(versionResult, 'openclaw --version');

    const doctorResult = run(nodeBin, ['openclaw.mjs', 'doctor', '--fix'], {
      cwd: openclawRepo,
      timeout: 180000
    });
    expectSuccess(doctorResult, 'openclaw doctor --fix');

    // Docs-aligned operator check without WS probe dependency.
    const statusResult = run(nodeBin, ['openclaw.mjs', 'gateway', 'status', '--no-probe'], {
      cwd: openclawRepo,
      timeout: 120000
    });
    expectSuccess(statusResult, 'openclaw gateway status --no-probe');
  });
});
