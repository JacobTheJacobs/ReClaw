const fs = require('fs-extra');
const os = require('os');
const path = require('path');
const BackupService = require('../lib/index');

describe('BackupService restore', () => {
  let rootDir;
  let homeDir;
  let backupDir;

  beforeEach(async () => {
    process.env.RECLAW_SKIP_GATEWAY_STOP = '1';
    process.env.RECLAW_SKIP_GATEWAY_RESTART = '1';

    rootDir = await fs.mkdtemp(path.join(os.tmpdir(), 'reclaw-test-restore-'));
    homeDir = path.join(rootDir, 'home');
    backupDir = path.join(rootDir, 'backups');

    await fs.ensureDir(homeDir);
    await fs.ensureDir(backupDir);

    await fs.outputFile(path.join(homeDir, 'openclaw.json'), JSON.stringify({ token: 'abc123' }, null, 2));
    await fs.outputFile(path.join(homeDir, 'workspace', 'todo.md'), 'original content');
    await fs.outputFile(path.join(homeDir, 'memory', 'main.sqlite'), 'sqlite-seed');
    await fs.outputFile(path.join(homeDir, 'credentials', 'provider.txt'), 'original-cred');
  });

  afterEach(async () => {
    delete process.env.RECLAW_SKIP_GATEWAY_STOP;
    delete process.env.RECLAW_SKIP_GATEWAY_RESTART;

    if (rootDir) {
      await fs.remove(rootDir);
    }
  });

  test('restores modified files from a created backup', async () => {
    const service = new BackupService({ home: homeDir, dest: backupDir });
    const archivePath = await service.createSnapshot();

    await fs.outputFile(path.join(homeDir, 'openclaw.json'), JSON.stringify({ token: 'changed' }, null, 2));
    await fs.outputFile(path.join(homeDir, 'workspace', 'todo.md'), 'changed content');

    await service.restore(archivePath);

    const restoredConfig = await fs.readJson(path.join(homeDir, 'openclaw.json'));
    const restoredTodo = await fs.readFile(path.join(homeDir, 'workspace', 'todo.md'), 'utf8');

    expect(restoredConfig).toEqual(expect.objectContaining({ token: 'abc123' }));
    expect(restoredConfig.gateway).toEqual(
      expect.objectContaining({
        auth: expect.objectContaining({
          token: expect.any(String)
        })
      })
    );
    expect(restoredTodo).toBe('original content');
  });

  test('restores modified files from encrypted tar.gz backup when password is provided', async () => {
    const password = 'secret123';
    const backupService = new BackupService({ home: homeDir, dest: backupDir, password });
    const createResult = await backupService.createBackup({ format: 'tar.gz' });

    await fs.outputFile(path.join(homeDir, 'openclaw.json'), JSON.stringify({ token: 'changed-tar' }, null, 2));
    await fs.outputFile(path.join(homeDir, 'workspace', 'todo.md'), 'changed tar content');

    const restoreService = new BackupService({ home: homeDir, dest: backupDir, password });
    await restoreService.restore(createResult.archivePath);

    const restoredConfig = await fs.readJson(path.join(homeDir, 'openclaw.json'));
    const restoredTodo = await fs.readFile(path.join(homeDir, 'workspace', 'todo.md'), 'utf8');

    expect(restoredConfig).toEqual(expect.objectContaining({ token: 'abc123' }));
    expect(restoredTodo).toBe('original content');
  });

  test('rejects invalid safe-reset scope before restore', async () => {
    const service = new BackupService({ home: homeDir, dest: backupDir });
    const archivePath = await service.createSnapshot();

    await expect(
      service.restore(archivePath, { safeReset: true, resetScope: 'not-a-scope' }),
    ).rejects.toThrow(/invalid reset scope/i);
  });

  test('supports partial restore with --scope config+creds', async () => {
    const service = new BackupService({ home: homeDir, dest: backupDir });
    const archivePath = await service.createSnapshot();

    await fs.outputFile(path.join(homeDir, 'openclaw.json'), JSON.stringify({ token: 'changed-config' }, null, 2));
    await fs.outputFile(path.join(homeDir, 'credentials', 'provider.txt'), 'changed-cred');
    await fs.outputFile(path.join(homeDir, 'workspace', 'todo.md'), 'changed-workspace');

    await service.restore(archivePath, { scope: 'config+creds' });

    const restoredConfig = await fs.readJson(path.join(homeDir, 'openclaw.json'));
    const restoredCred = await fs.readFile(path.join(homeDir, 'credentials', 'provider.txt'), 'utf8');
    const workspaceContent = await fs.readFile(path.join(homeDir, 'workspace', 'todo.md'), 'utf8');

    expect(restoredConfig.token).toBe('abc123');
    expect(restoredCred).toBe('original-cred');
    expect(workspaceContent).toBe('changed-workspace');
  });
});
