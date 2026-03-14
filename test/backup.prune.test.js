/**
 * backup.prune.test.js
 *
 * Live tests for pruneBackups edge cases not covered in backup.verify.test.js.
 * NO mocks.
 */

const fs = require('fs-extra');
const os = require('os');
const path = require('path');
const BackupService = require('../lib/index');

describe('BackupService.pruneBackups edge cases', () => {
  let rootDir;
  let homeDir;
  let backupDir;

  beforeEach(async () => {
    rootDir = await fs.mkdtemp(path.join(os.tmpdir(), 'reclaw-prune-'));
    homeDir = path.join(rootDir, 'home');
    backupDir = path.join(rootDir, 'backups');

    await fs.ensureDir(homeDir);
    await fs.ensureDir(backupDir);
    await fs.outputFile(path.join(homeDir, 'openclaw.json'), '{}');
  });

  afterEach(async () => {
    await fs.remove(rootDir);
  });

  test('throws when no pruning policy is specified', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir });
    await expect(svc.pruneBackups({})).rejects.toThrow(/no prune policy/i);
  });

  test('--keep-last 0 deletes every backup', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir });

    const paths = [
      path.join(backupDir, 'openclaw-a.zip'),
      path.join(backupDir, 'openclaw-b.zip'),
      path.join(backupDir, 'openclaw-c.zip'),
    ];
    for (const p of paths) {
      await fs.outputFile(p, 'dummy');
    }

    const result = await svc.pruneBackups({ keepLast: 0 });

    expect(result.deletedCount).toBe(3);
    for (const p of paths) {
      expect(await fs.pathExists(p)).toBe(false);
    }
  });

  test('--older-than only deletes old files, keeps recent ones', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir });
    const now = Date.now();

    const oldPath = path.join(backupDir, 'openclaw-old.zip');
    const newPath = path.join(backupDir, 'openclaw-new.zip');

    await fs.outputFile(oldPath, 'old');
    await fs.outputFile(newPath, 'new');

    // Make old file look 60 days old
    const sixtyDaysAgo = new Date(now - 60 * 24 * 60 * 60 * 1000);
    await fs.utimes(oldPath, sixtyDaysAgo, sixtyDaysAgo);

    const result = await svc.pruneBackups({ olderThan: '30d' });

    expect(result.deletedCount).toBeGreaterThanOrEqual(1);
    expect(await fs.pathExists(oldPath)).toBe(false);
    expect(await fs.pathExists(newPath)).toBe(true);
  });

  test('--keep-last combined with --older-than deletes union of eligible files', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir });
    const now = Date.now();

    const names = ['openclaw-1.zip', 'openclaw-2.zip', 'openclaw-3.zip', 'openclaw-4.zip'];
    for (const name of names) {
      await fs.outputFile(path.join(backupDir, name), 'x');
    }

    // Age the first two files beyond 30 days
    for (const name of names.slice(0, 2)) {
      const old = new Date(now - 40 * 24 * 60 * 60 * 1000);
      await fs.utimes(path.join(backupDir, name), old, old);
    }

    // keep-last 2 + older-than 30d → union policy
    const result = await svc.pruneBackups({ keepLast: 2, olderThan: '30d' });
    expect(result.deletedCount).toBeGreaterThanOrEqual(2);
  });

  test('dry-run reports deletions without removing files', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir });

    const p1 = path.join(backupDir, 'openclaw-x.zip');
    const p2 = path.join(backupDir, 'openclaw-y.zip');
    await fs.outputFile(p1, 'x');
    await fs.outputFile(p2, 'y');

    const result = await svc.pruneBackups({ keepLast: 0, dryRun: true });

    expect(result.dryRun).toBe(true);
    expect(result.deletedCount).toBe(2);
    expect(await fs.pathExists(p1)).toBe(true);
    expect(await fs.pathExists(p2)).toBe(true);
  });

  test('listBackups throws for invalid limit value', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir });
    await expect(svc.listBackups({ limit: -5 })).rejects.toThrow(/invalid.*limit/i);
    await expect(svc.listBackups({ limit: 0 })).rejects.toThrow(/invalid.*limit/i);
    await expect(svc.listBackups({ limit: 'abc' })).rejects.toThrow(/invalid.*limit/i);
  });

  test('listBackups returns empty array when backup dir is missing', async () => {
    const svc = new BackupService({ home: homeDir, dest: path.join(rootDir, 'no-such-dir') });
    const list = await svc.listBackups();
    expect(list).toEqual([]);
  });

  test('listBackups only surfaces files that look like backups', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir });

    await fs.outputFile(path.join(backupDir, 'openclaw_backup_2026.zip'), 'real');
    await fs.outputFile(path.join(backupDir, 'unrelated-file.txt'), 'ignored');
    await fs.outputFile(path.join(backupDir, 'random.zip'), 'ignored-no-openclaw');

    const list = await svc.listBackups();
    const names = list.map((b) => b.name);

    expect(names).toContain('openclaw_backup_2026.zip');
    expect(names).not.toContain('unrelated-file.txt');
    expect(names).not.toContain('random.zip');
  });
});
