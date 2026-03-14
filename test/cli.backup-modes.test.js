const fs = require('fs-extra');
const os = require('os');
const path = require('path');
const { spawnSync } = require('child_process');

function runCli(args, options = {}) {
  const cliPath = path.join(__dirname, '..', 'bin', 'cli.js');
  return spawnSync(process.execPath, [cliPath, ...args], {
    encoding: 'utf-8',
    timeout: options.timeout || 120000,
    env: options.env || process.env,
    cwd: path.join(__dirname, '..')
  });
}

describe('backup CLI modes', () => {
  let rootDir;
  let homeDir;
  let backupDir;

  beforeEach(async () => {
    rootDir = await fs.mkdtemp(path.join(os.tmpdir(), 'reclaw-test-cli-backup-'));
    homeDir = path.join(rootDir, 'home');
    backupDir = path.join(rootDir, 'backups');

    await fs.ensureDir(path.join(homeDir, 'workspace'));
    await fs.outputFile(path.join(homeDir, 'openclaw.json'), JSON.stringify({ hello: 'world' }, null, 2));
    await fs.outputFile(path.join(homeDir, 'workspace', 'notes.txt'), 'hello from cli test');
    await fs.outputFile(path.join(homeDir, 'credentials', 'token.txt'), 'cli-token');
  });

  afterEach(async () => {
    if (rootDir) {
      await fs.remove(rootDir);
    }
  });

  test('backup create --dry-run --json emits JSON only and writes no archive', async () => {
    const env = {
      ...process.env,
      OPENCLAW_HOME: homeDir,
      BACKUP_DIR: backupDir
    };

    const result = runCli(['backup', 'create', '--dry-run', '--json'], { env });

    expect(result.status).toBe(0);
    expect(result.stderr.trim()).toBe('');

    const parsed = JSON.parse(result.stdout.trim());
    expect(parsed.dryRun).toBe(true);
    expect(parsed.archivePath).toContain(path.join('backups', 'openclaw_backup_'));

    const archives = await fs.readdir(backupDir).catch(() => []);
    expect(archives).toHaveLength(0);
  });

  test('backup verify --json validates archive created by backup create --json', async () => {
    const env = {
      ...process.env,
      OPENCLAW_HOME: homeDir,
      BACKUP_DIR: backupDir
    };

    const createResult = runCli(['backup', 'create', '--json'], { env });
    expect(createResult.status).toBe(0);
    const createParsed = JSON.parse(createResult.stdout.trim());

    const verifyResult = runCli(['backup', 'verify', createParsed.archivePath, '--json'], { env });
    expect(verifyResult.status).toBe(0);

    const verifyParsed = JSON.parse(verifyResult.stdout.trim());
    expect(verifyParsed.ok).toBe(true);
    expect(verifyParsed.archivePath).toBe(createParsed.archivePath);
    expect(verifyParsed.assetCount).toBeGreaterThan(0);
  });

  test('backup create defaults to tar.gz and automatic verification', async () => {
    const env = {
      ...process.env,
      OPENCLAW_HOME: homeDir,
      BACKUP_DIR: backupDir
    };

    const createResult = runCli(['backup', 'create', '--json'], { env });
    expect(createResult.status).toBe(0);

    const createParsed = JSON.parse(createResult.stdout.trim());
    expect(createParsed.archiveFormat).toBe('tar.gz');
    expect(createParsed.archivePath.endsWith('.tar.gz')).toBe(true);
    expect(createParsed.verified).toBe(true);
  });

  test('backup create/verify supports encrypted tar.gz format with password', async () => {
    const password = 'secret123';
    const env = {
      ...process.env,
      OPENCLAW_HOME: homeDir,
      BACKUP_DIR: backupDir
    };

    const createResult = runCli(['backup', 'create', '--format', 'tar.gz', '--password', password, '--json'], { env });
    expect(createResult.status).toBe(0);
    const createParsed = JSON.parse(createResult.stdout.trim());

    expect(createParsed.archiveFormat).toBe('tar.gz');
    expect(createParsed.archivePath.endsWith('.tar.gz.enc')).toBe(true);

    const verifyResult = runCli(['backup', 'verify', createParsed.archivePath, '--password', password, '--json'], { env });
    expect(verifyResult.status).toBe(0);
    const verifyParsed = JSON.parse(verifyResult.stdout.trim());

    expect(verifyParsed.ok).toBe(true);
    expect(verifyParsed.archiveType).toBe('tar.gz');
  });

  test('backup list and prune support JSON + limits', async () => {
    const env = {
      ...process.env,
      OPENCLAW_HOME: homeDir,
      BACKUP_DIR: backupDir
    };

    const firstCreate = runCli(['backup', 'create', '--output', path.join(backupDir, 'openclaw-one.zip'), '--json'], { env });
    expect(firstCreate.status).toBe(0);
    const secondCreate = runCli(['backup', 'create', '--output', path.join(backupDir, 'openclaw-two.tar.gz'), '--json'], { env });
    expect(secondCreate.status).toBe(0);

    const listResult = runCli(['backup', 'list', '--json', '--limit', '1'], { env });
    expect(listResult.status).toBe(0);
    const listParsed = JSON.parse(listResult.stdout.trim());
    expect(Array.isArray(listParsed.backups)).toBe(true);
    expect(listParsed.backups).toHaveLength(1);

    const pruneResult = runCli(['backup', 'prune', '--keep-last', '1', '--dry-run', '--json'], { env });
    expect(pruneResult.status).toBe(0);
    const pruneParsed = JSON.parse(pruneResult.stdout.trim());
    expect(pruneParsed.dryRun).toBe(true);
    expect(pruneParsed.deletedCount).toBeGreaterThanOrEqual(1);
  });

  test('backup export supports scoped credentials archive', async () => {
    const env = {
      ...process.env,
      OPENCLAW_HOME: homeDir,
      BACKUP_DIR: backupDir
    };

    const exportPath = path.join(backupDir, 'openclaw-creds.tar.gz');
    const exportResult = runCli(['backup', 'export', '--scope', 'credentials', '--output', exportPath, '--json'], { env });

    expect(exportResult.status).toBe(0);
    const exportParsed = JSON.parse(exportResult.stdout.trim());
    expect(exportParsed.archivePath).toBe(exportPath);
    expect(exportParsed.archiveFormat).toBe('tar.gz');
    expect(exportParsed.scope).toBe('creds');

    const verifyResult = runCli(['backup', 'verify', exportPath, '--json'], { env });
    expect(verifyResult.status).toBe(0);
    const verifyParsed = JSON.parse(verifyResult.stdout.trim());
    expect(verifyParsed.ok).toBe(true);
  });
});
