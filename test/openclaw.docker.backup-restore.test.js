const fs = require('fs-extra');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');
const BackupService = require('../lib/index');

function run(command, args, options = {}) {
  return spawnSync(command, args, {
    encoding: 'utf-8',
    timeout: options.timeout || 120000,
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

describe('Docker backup/restore integration', () => {
  jest.setTimeout(900000);
  const enabled = process.env.RUN_DOCKER_TESTS === '1';
  const testIf = enabled ? test : test.skip;

  let tempBackupDir;

  beforeAll(async () => {
    tempBackupDir = await fs.mkdtemp(path.join(os.tmpdir(), 'reclaw-docker-backup-'));
  });

  afterAll(async () => {
    if (tempBackupDir) {
      await fs.remove(tempBackupDir);
    }
  });

  testIf('creates encrypted backup and restores into Docker-mounted OpenClaw home', async () => {
    const reclawRepo = process.env.RECLAW_REPO || '/Users/jacob/githuff/ReClaw';
    const openclawRepo = process.env.OPENCLAW_REPO || '/Users/jacob/githuff/openclaw';
    const archivePathFromEnv = process.env.RECLAW_ARCHIVE || '/Users/jacob/githuff/claw-backup/backup.zip';
    const homeDir = process.env.OPENCLAW_HOME || path.join(os.homedir(), '.openclaw');
    const password = process.env.RECLAW_PASSWORD || 'reclaw123';

    if (!(await fs.pathExists(path.join(homeDir, 'openclaw.json')))) {
      const drill = run(
        'bash',
        [
          path.join(reclawRepo, 'scripts', 'docker-recovery-drill.sh'),
          '--openclaw-repo', openclawRepo,
          '--archive', archivePathFromEnv,
          '--password', password
        ],
        { cwd: reclawRepo, timeout: 0, env: process.env }
      );
      assertSuccess(drill, 'docker-recovery-drill bootstrap');
    }

    expect(await fs.pathExists(path.join(homeDir, 'openclaw.json'))).toBe(true);

    const markerPath = path.join(homeDir, 'workspace', 'reclaw-docker-marker.txt');
    const originalMarker = `original-${Date.now()}`;
    const changedMarker = `changed-${Date.now()}`;

    await fs.ensureDir(path.dirname(markerPath));
    await fs.writeFile(markerPath, originalMarker);

    const backupService = new BackupService({
      home: homeDir,
      dest: tempBackupDir,
      password
    });

    const archivePath = await backupService.createSnapshot();
    expect(await fs.pathExists(archivePath)).toBe(true);

    await fs.writeFile(markerPath, changedMarker);

    const restoreService = new BackupService({
      home: homeDir,
      dest: tempBackupDir,
      password
    });

    const previousDockerHome = process.env.OPENCLAW_DOCKER_HOME;
    process.env.OPENCLAW_DOCKER_HOME = '/home/node/.openclaw';
    await restoreService.restore(archivePath);
    if (previousDockerHome === undefined) {
      delete process.env.OPENCLAW_DOCKER_HOME;
    } else {
      process.env.OPENCLAW_DOCKER_HOME = previousDockerHome;
    }

    const restoredMarker = await fs.readFile(markerPath, 'utf8');
    expect(restoredMarker).toBe(originalMarker);

    const health = run('curl', ['-fsS', 'http://127.0.0.1:18789/healthz']);
    expect(health.status).toBe(0);
    expect((health.stdout || '').trim()).toContain('"ok":true');
  });
});
