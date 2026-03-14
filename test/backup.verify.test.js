const fs = require('fs-extra');
const os = require('os');
const path = require('path');
const AdmZip = require('adm-zip');
const tar = require('tar');
const BackupService = require('../lib/index');

describe('BackupService verification and planning', () => {
  let rootDir;
  let homeDir;
  let backupDir;

  beforeEach(async () => {
    rootDir = await fs.mkdtemp(path.join(os.tmpdir(), 'reclaw-test-verify-'));
    homeDir = path.join(rootDir, 'home');
    backupDir = path.join(rootDir, 'backups');

    await fs.ensureDir(homeDir);
    await fs.ensureDir(backupDir);

    await fs.outputFile(path.join(homeDir, 'openclaw.json'), JSON.stringify({ hello: 'world' }, null, 2));
    await fs.outputFile(path.join(homeDir, 'workspace', 'notes.txt'), 'verify me');
    await fs.outputFile(path.join(homeDir, 'memory', 'events.sqlite'), 'sqlite-seed');
    await fs.outputFile(path.join(homeDir, 'credentials', 'api-token.txt'), 'token-123');
  });

  afterEach(async () => {
    if (rootDir) {
      await fs.remove(rootDir);
    }
  });

  test('supports dry-run create plan without writing archive', async () => {
    const service = new BackupService({ home: homeDir, dest: backupDir });
    const result = await service.createBackup({ dryRun: true, onlyConfig: true });

    expect(result.dryRun).toBe(true);
    expect(result.onlyConfig).toBe(true);
    expect(result.assets).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          kind: 'config',
          archivePath: 'openclaw.json'
        })
      ])
    );

    expect(await fs.pathExists(result.archivePath)).toBe(false);
  });

  test('verifies archives produced by createBackup', async () => {
    const service = new BackupService({ home: homeDir, dest: backupDir });
    const createResult = await service.createBackup({ verify: true });

    expect(createResult.verified).toBe(true);

    const verifyResult = await service.verifySnapshot(createResult.archivePath, { silent: true });
    expect(verifyResult.ok).toBe(true);
    expect(verifyResult.assetCount).toBeGreaterThan(0);
    expect(verifyResult.payloadEntryCount).toBeGreaterThan(0);
  });

  test('verifies tar.gz archives and encrypted tar.gz archives', async () => {
    const plainService = new BackupService({ home: homeDir, dest: backupDir });
    const tarResult = await plainService.createBackup({ format: 'tar.gz', verify: true });

    expect(tarResult.archivePath.endsWith('.tar.gz')).toBe(true);
    expect(tarResult.verified).toBe(true);

    const tarVerifyResult = await plainService.verifySnapshot(tarResult.archivePath, { silent: true });
    expect(tarVerifyResult.ok).toBe(true);
    expect(tarVerifyResult.archiveType).toBe('tar.gz');

    const password = 'secret123';
    const encryptedService = new BackupService({ home: homeDir, dest: backupDir, password });
    const encryptedResult = await encryptedService.createBackup({ format: 'tar.gz', verify: true });

    expect(encryptedResult.archivePath.endsWith('.tar.gz.enc')).toBe(true);

    const encryptedVerifyResult = await encryptedService.verifySnapshot(encryptedResult.archivePath, { silent: true });
    expect(encryptedVerifyResult.ok).toBe(true);

    const noPasswordService = new BackupService({ home: homeDir, dest: backupDir });
    await expect(noPasswordService.verifySnapshot(encryptedResult.archivePath, { silent: true })).rejects.toThrow(
      /password/i,
    );
  });

  test('detects payload tampering via checksum verification', async () => {
    const service = new BackupService({ home: homeDir, dest: backupDir });
    const createResult = await service.createBackup();

    const sourceZip = new AdmZip(createResult.archivePath);
    const extractDir = path.join(rootDir, 'tamper');
    sourceZip.extractAllTo(extractDir, true);

    await fs.outputFile(path.join(extractDir, 'workspace', 'notes.txt'), 'tampered-content');

    const tamperedPath = path.join(backupDir, 'tampered.zip');
    const tamperedZip = new AdmZip();
    tamperedZip.addLocalFolder(extractDir);
    tamperedZip.writeZip(tamperedPath);

    await expect(service.verifySnapshot(tamperedPath, { silent: true })).rejects.toThrow(
      /hash mismatch|size mismatch/i,
    );
  });

  test('fails fast on invalid config when workspace backup is enabled', async () => {
    const service = new BackupService({ home: homeDir, dest: backupDir });
    await fs.writeFile(path.join(homeDir, 'openclaw.json'), '{invalid-json', 'utf8');

    await expect(service.createBackup()).rejects.toThrow(/--no-include-workspace/i);

    const partial = await service.createBackup({ includeWorkspace: false });
    expect(partial.ok).toBe(true);
  });

  test('lists backups with metadata and respects --limit', async () => {
    const service = new BackupService({ home: homeDir, dest: backupDir });

    await service.createBackup({ output: path.join(backupDir, 'openclaw-a.zip') });
    await service.createBackup({ output: path.join(backupDir, 'openclaw-b.tar.gz') });

    const allBackups = await service.listBackups();
    expect(allBackups.length).toBeGreaterThanOrEqual(2);
    expect(allBackups[0]).toEqual(
      expect.objectContaining({
        name: expect.any(String),
        archivePath: expect.any(String),
        archiveType: expect.any(String),
        modifiedAt: expect.any(String),
        size: expect.any(Number)
      }),
    );

    const limited = await service.listBackups({ limit: 1 });
    expect(limited).toHaveLength(1);
  });

  test('prunes backups using keep-last and older-than policies', async () => {
    const service = new BackupService({ home: homeDir, dest: backupDir });

    const oldPath = path.join(backupDir, 'openclaw-old.zip');
    const midPath = path.join(backupDir, 'openclaw-mid.zip');
    const newPath = path.join(backupDir, 'openclaw-new.zip');

    await fs.outputFile(oldPath, 'old');
    await fs.outputFile(midPath, 'mid');
    await fs.outputFile(newPath, 'new');

    const now = Date.now();
    await fs.utimes(oldPath, new Date(now - 40 * 24 * 60 * 60 * 1000), new Date(now - 40 * 24 * 60 * 60 * 1000));
    await fs.utimes(midPath, new Date(now - 5 * 24 * 60 * 60 * 1000), new Date(now - 5 * 24 * 60 * 60 * 1000));
    await fs.utimes(newPath, new Date(now), new Date(now));

    const dryRun = await service.pruneBackups({ keepLast: 1, olderThan: '30d', dryRun: true });
    expect(dryRun.dryRun).toBe(true);
    expect(dryRun.deletedCount).toBeGreaterThanOrEqual(2);
    expect(await fs.pathExists(oldPath)).toBe(true);

    const prune = await service.pruneBackups({ keepLast: 1, olderThan: '30d' });
    expect(prune.deletedCount).toBeGreaterThanOrEqual(2);
    expect(await fs.pathExists(oldPath)).toBe(false);
    expect(await fs.pathExists(newPath)).toBe(true);
  });

  test('exports scoped credentials backup with tar.gz output', async () => {
    const service = new BackupService({ home: homeDir, dest: backupDir });
    const outputPath = path.join(backupDir, 'openclaw-creds-export.tar.gz');

    const exportResult = await service.exportBackup({
      scope: 'credentials',
      output: outputPath,
      verify: true
    });

    expect(exportResult.archivePath).toBe(outputPath);
    expect(exportResult.archiveFormat).toBe('tar.gz');
    expect(exportResult.verified).toBe(true);

    const extractDir = path.join(rootDir, 'export-extract');
    await fs.ensureDir(extractDir);
    await tar.x({ file: outputPath, cwd: extractDir, gzip: true, strict: true });

    const manifest = await fs.readJson(path.join(extractDir, 'manifest.json'));
    const payloadPaths = manifest.payload.map((entry) => entry.archivePath);
    expect(payloadPaths).toContain('credentials/api-token.txt');
    expect(payloadPaths).not.toContain('workspace/notes.txt');
  });
});
