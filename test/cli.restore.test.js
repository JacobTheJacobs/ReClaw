/**
 * cli.restore.test.js
 *
 * Live end-to-end tests for the CLI restore commands.
 * Spawns the real CLI process (no mocks).
 * Lifecycle hooks (gateway stop/restart, doctor) are disabled via env vars
 * that the production code already supports.
 */

const fs = require('fs-extra');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');

const CLI_PATH = path.join(__dirname, '..', 'bin', 'cli.js');

function runCli(args, env, timeout = 120000) {
  return spawnSync(process.execPath, [CLI_PATH, ...args], {
    encoding: 'utf-8',
    timeout,
    env,
    cwd: path.join(__dirname, '..')
  });
}

function baseEnv(homeDir, backupDir, extra = {}) {
  return {
    ...process.env,
    OPENCLAW_HOME: homeDir,
    BACKUP_DIR: backupDir,
    // Bypass lifecycle commands that require a running OpenClaw install
    RECLAW_SKIP_GATEWAY_STOP: '1',
    RECLAW_SKIP_GATEWAY_RESTART: '1',
    RECLAW_DOCTOR_REPAIR: '0',
    ...extra
  };
}

describe('CLI: top-level restore command', () => {
  let rootDir;
  let homeDir;
  let backupDir;

  beforeEach(async () => {
    rootDir = await fs.mkdtemp(path.join(os.tmpdir(), 'reclaw-cli-restore-'));
    homeDir = path.join(rootDir, 'home');
    backupDir = path.join(rootDir, 'backups');

    await fs.ensureDir(homeDir);
    await fs.ensureDir(backupDir);

    await fs.outputFile(path.join(homeDir, 'openclaw.json'), JSON.stringify({ token: 'original' }));
    await fs.outputFile(path.join(homeDir, 'workspace', 'note.txt'), 'original note');
    await fs.outputFile(path.join(homeDir, 'credentials', 'api.txt'), 'original-key');
  });

  afterEach(async () => {
    await fs.remove(rootDir);
  });

  test('restore <archive> restores files and exits 0', async () => {
    const env = baseEnv(homeDir, backupDir);

    // Create backup via CLI
    const createResult = runCli(['backup', 'create', '--json'], env);
    expect(createResult.status).toBe(0);
    const { archivePath } = JSON.parse(createResult.stdout.trim());

    // Mutate state
    await fs.writeJson(path.join(homeDir, 'openclaw.json'), { token: 'mutated' });
    await fs.writeFile(path.join(homeDir, 'workspace', 'note.txt'), 'mutated note');

    // Restore via top-level restore command
    const restoreResult = runCli(['restore', archivePath], env);
    expect(restoreResult.status).toBe(0);

    const cfg = await fs.readJson(path.join(homeDir, 'openclaw.json'));
    expect(cfg.token).toBe('original');

    const note = await fs.readFile(path.join(homeDir, 'workspace', 'note.txt'), 'utf8');
    expect(note).toBe('original note');
  });

  test('restore --dry-run exits 0 and does not modify files', async () => {
    const env = baseEnv(homeDir, backupDir);

    const createResult = runCli(['backup', 'create', '--json'], env);
    expect(createResult.status).toBe(0);
    const { archivePath } = JSON.parse(createResult.stdout.trim());

    await fs.writeJson(path.join(homeDir, 'openclaw.json'), { token: 'mutated' });

    const dryResult = runCli(['restore', archivePath, '--dry-run'], env);
    expect(dryResult.status).toBe(0);
    // The CLI prints a DRY RUN message (not JSON) but must exit 0
    expect(dryResult.stdout + dryResult.stderr).toMatch(/dry run/i);

    // File should NOT have been restored
    const cfg = await fs.readJson(path.join(homeDir, 'openclaw.json'));
    expect(cfg.token).toBe('mutated');
  });

  test('restore --scope config+creds restores only config and credentials', async () => {
    const env = baseEnv(homeDir, backupDir);

    const createResult = runCli(['backup', 'create', '--json'], env);
    expect(createResult.status).toBe(0);
    const { archivePath } = JSON.parse(createResult.stdout.trim());

    await fs.writeJson(path.join(homeDir, 'openclaw.json'), { token: 'changed' });
    await fs.writeFile(path.join(homeDir, 'credentials', 'api.txt'), 'changed-key');
    await fs.writeFile(path.join(homeDir, 'workspace', 'note.txt'), 'changed note');

    const restoreResult = runCli(['restore', archivePath, '--scope', 'config+creds'], env);
    expect(restoreResult.status).toBe(0);

    const cfg = await fs.readJson(path.join(homeDir, 'openclaw.json'));
    expect(cfg.token).toBe('original');

    const key = await fs.readFile(path.join(homeDir, 'credentials', 'api.txt'), 'utf8');
    expect(key).toBe('original-key');

    // workspace should NOT be restored
    const note = await fs.readFile(path.join(homeDir, 'workspace', 'note.txt'), 'utf8');
    expect(note).toBe('changed note');
  });

  test('restore --verify validates archive before restoring', async () => {
    const env = baseEnv(homeDir, backupDir);

    const createResult = runCli(['backup', 'create', '--json'], env);
    expect(createResult.status).toBe(0);
    const { archivePath } = JSON.parse(createResult.stdout.trim());

    const restoreResult = runCli(['restore', archivePath, '--verify'], env);
    expect(restoreResult.status).toBe(0);
  });

  test('restore from a non-existent archive exits non-zero', async () => {
    const env = baseEnv(homeDir, backupDir);
    const restoreResult = runCli(['restore', '/tmp/does-not-exist.tar.gz'], env);
    expect(restoreResult.status).not.toBe(0);
  });

  test('restore --password decrypts an encrypted archive', async () => {
    const password = 'cli-test-pass';
    const env = baseEnv(homeDir, backupDir);

    const createResult = runCli(
      ['backup', 'create', '--format', 'tar.gz', '--password', password, '--json'],
      env
    );
    expect(createResult.status).toBe(0);
    const { archivePath } = JSON.parse(createResult.stdout.trim());

    await fs.writeJson(path.join(homeDir, 'openclaw.json'), { token: 'changed-enc' });

    const restoreResult = runCli(['restore', archivePath, '--password', password], env);
    expect(restoreResult.status).toBe(0);

    const cfg = await fs.readJson(path.join(homeDir, 'openclaw.json'));
    expect(cfg.token).toBe('original');
  });
});

describe('CLI: backup restore subcommand', () => {
  let rootDir;
  let homeDir;
  let backupDir;

  beforeEach(async () => {
    rootDir = await fs.mkdtemp(path.join(os.tmpdir(), 'reclaw-cli-bakup-restore-'));
    homeDir = path.join(rootDir, 'home');
    backupDir = path.join(rootDir, 'backups');

    await fs.ensureDir(homeDir);
    await fs.ensureDir(backupDir);

    await fs.outputFile(path.join(homeDir, 'openclaw.json'), JSON.stringify({ token: 'sub-orig' }));
    await fs.outputFile(path.join(homeDir, 'workspace', 'file.txt'), 'sub-original');
  });

  afterEach(async () => {
    await fs.remove(rootDir);
  });

  test('backup restore <archive> subcommand restores files and exits 0', async () => {
    const env = baseEnv(homeDir, backupDir);

    const createResult = runCli(['backup', 'create', '--json'], env);
    expect(createResult.status).toBe(0);
    const { archivePath } = JSON.parse(createResult.stdout.trim());

    await fs.writeJson(path.join(homeDir, 'openclaw.json'), { token: 'sub-changed' });

    const restoreResult = runCli(['backup', 'restore', archivePath], env);
    expect(restoreResult.status).toBe(0);

    const cfg = await fs.readJson(path.join(homeDir, 'openclaw.json'));
    expect(cfg.token).toBe('sub-orig');
  });

  test('backup restore --dry-run exits 0 and does not modify files', async () => {
    const env = baseEnv(homeDir, backupDir);

    const createResult = runCli(['backup', 'create', '--json'], env);
    expect(createResult.status).toBe(0);
    const { archivePath } = JSON.parse(createResult.stdout.trim());

    await fs.writeJson(path.join(homeDir, 'openclaw.json'), { token: 'should-stay' });

    const dryResult = runCli(['backup', 'restore', archivePath, '--dry-run'], env);
    expect(dryResult.status).toBe(0);
    expect(dryResult.stdout + dryResult.stderr).toMatch(/dry run/i);

    const cfg = await fs.readJson(path.join(homeDir, 'openclaw.json'));
    expect(cfg.token).toBe('should-stay');
  });

  test('backup restore with --safe-reset and invalid scope exits non-zero', async () => {
    const env = baseEnv(homeDir, backupDir);

    const createResult = runCli(['backup', 'create', '--json'], env);
    expect(createResult.status).toBe(0);
    const { archivePath } = JSON.parse(createResult.stdout.trim());

    const result = runCli(
      ['backup', 'restore', archivePath, '--safe-reset', '--reset-scope', 'bad-scope'],
      env
    );
    expect(result.status).not.toBe(0);
  });
});
