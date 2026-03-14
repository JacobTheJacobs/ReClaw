const fs = require('fs-extra');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');

function run(command, args, options = {}) {
  return spawnSync(command, args, {
    encoding: 'utf-8',
    timeout: options.timeout || 0,
    cwd: options.cwd,
    env: options.env || process.env,
    stdio: 'pipe'
  });
}

function assertSuccess(result, label) {
  if (result.status === 0) return;
  const details = [
    `step=${label}`,
    `status=${result.status}`,
    `error=${result.error ? result.error.message : 'none'}`,
    `stdout=${(result.stdout || '').trim()}`,
    `stderr=${(result.stderr || '').trim()}`
  ].join('\n');
  throw new Error(details);
}

describe('Docker recovery drill integration', () => {
  jest.setTimeout(900000);
  const enabled = process.env.RUN_DOCKER_TESTS === '1';
  const testIf = enabled ? test : test.skip;

  const reclawRepo = process.env.RECLAW_REPO || '/Users/jacob/githuff/ReClaw';
  const openclawRepo = process.env.OPENCLAW_REPO || '/Users/jacob/githuff/openclaw';
  const archivePath = process.env.RECLAW_ARCHIVE || '/Users/jacob/githuff/claw-backup/backup.zip';
  const archivePassword = process.env.RECLAW_PASSWORD || 'reclaw123';

  testIf('runs docker recovery drill script and leaves gateway healthy', () => {
    expect(fs.existsSync(path.join(reclawRepo, 'scripts', 'docker-recovery-drill.sh'))).toBe(true);
    expect(fs.existsSync(path.join(openclawRepo, 'docker-compose.yml'))).toBe(true);
    expect(fs.existsSync(archivePath)).toBe(true);

    const drill = run(
      'bash',
      [
        path.join(reclawRepo, 'scripts', 'docker-recovery-drill.sh'),
        '--openclaw-repo', openclawRepo,
        '--archive', archivePath,
        '--password', archivePassword
      ],
      {
        cwd: reclawRepo,
        timeout: 0,
        env: {
          ...process.env,
          OPENCLAW_HOME: process.env.OPENCLAW_HOME || path.join(os.homedir(), '.openclaw')
        }
      }
    );
    assertSuccess(drill, 'docker-recovery-drill.sh');

    const health = run('curl', ['-fsS', 'http://127.0.0.1:18789/healthz']);
    assertSuccess(health, 'curl healthz');

    expect((health.stdout || '').trim()).toContain('"ok":true');
  });
});
