/**
 * backup.scopes.test.js
 *
 * Live tests for every backup scope combination.
 * Creates real archives in temp dirs. NO mocks.
 */

const fs = require('fs-extra');
const os = require('os');
const path = require('path');
const tar = require('tar');
const AdmZip = require('adm-zip');
const BackupService = require('../lib/index');

function readZipManifest(archivePath) {
  const zip = new AdmZip(archivePath);
  return JSON.parse(zip.readAsText('manifest.json'));
}

async function readTarManifest(archivePath, extractDir) {
  await fs.ensureDir(extractDir);
  await tar.x({ file: archivePath, cwd: extractDir, gzip: true, strict: true });
  return fs.readJson(path.join(extractDir, 'manifest.json'));
}

describe('Scope-restricted backup creation', () => {
  let rootDir;
  let homeDir;
  let backupDir;

  beforeEach(async () => {
    rootDir = await fs.mkdtemp(path.join(os.tmpdir(), 'reclaw-scopes-'));
    homeDir = path.join(rootDir, 'home');
    backupDir = path.join(rootDir, 'backups');

    await fs.ensureDir(homeDir);
    await fs.ensureDir(backupDir);

    // Seed all categories
    await fs.outputFile(path.join(homeDir, 'openclaw.json'), JSON.stringify({ ver: 1 }));
    await fs.outputFile(path.join(homeDir, '.env'), 'FOO=bar');
    await fs.outputFile(path.join(homeDir, 'credentials', 'token.txt'), 'top-secret');
    await fs.outputFile(path.join(homeDir, 'sessions', 'current.json'), '{"id":1}');
    await fs.outputFile(path.join(homeDir, 'memory', 'data.txt'), 'memory');
    await fs.outputFile(path.join(homeDir, 'workspace', 'doc.md'), '# doc');
  });

  afterEach(async () => {
    await fs.remove(rootDir);
  });

  // ─── config scope ─────────────────────────────────────────────────────────

  test('scope=config includes openclaw.json and .env, excludes credentials and workspace', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir });
    const result = await svc.createBackup({ format: 'tar.gz', scope: 'config', includeWorkspace: false });

    expect(result.scope).toBe('config');
    const extractDir = path.join(rootDir, 'ext-config');
    const manifest = await readTarManifest(result.archivePath, extractDir);

    const paths = manifest.payload.map((e) => e.archivePath);
    expect(paths.some((p) => p === 'openclaw.json' || p.startsWith('openclaw.json'))).toBe(true);
    expect(paths.every((p) => !p.startsWith('credentials/'))).toBe(true);
    expect(paths.every((p) => !p.startsWith('workspace/'))).toBe(true);
  });

  // ─── creds scope ──────────────────────────────────────────────────────────

  test('scope=credentials includes credentials, excludes openclaw.json and workspace', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir });
    const result = await svc.createBackup({ format: 'tar.gz', scope: 'credentials' });

    expect(result.scope).toBe('creds');
    const extractDir = path.join(rootDir, 'ext-creds');
    const manifest = await readTarManifest(result.archivePath, extractDir);

    const paths = manifest.payload.map((e) => e.archivePath);
    expect(paths.some((p) => p.startsWith('credentials/'))).toBe(true);
    expect(paths.every((p) => !p.startsWith('workspace/'))).toBe(true);
    expect(paths.every((p) => p !== 'openclaw.json')).toBe(true);
  });

  // ─── sessions scope ───────────────────────────────────────────────────────

  test('scope=sessions includes sessions/memory dirs, excludes config and workspace', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir });
    const result = await svc.createBackup({ format: 'tar.gz', scope: 'sessions' });

    expect(result.scope).toBe('sessions');
    const extractDir = path.join(rootDir, 'ext-sessions');
    const manifest = await readTarManifest(result.archivePath, extractDir);

    const paths = manifest.payload.map((e) => e.archivePath);
    expect(paths.some((p) => p.startsWith('sessions/') || p.startsWith('memory/'))).toBe(true);
    expect(paths.every((p) => p !== 'openclaw.json' && !p.startsWith('workspace/'))).toBe(true);
  });

  // ─── workspace scope ──────────────────────────────────────────────────────

  test('scope=workspace includes workspace dir, excludes config/creds/sessions', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir });
    const result = await svc.createBackup({ format: 'tar.gz', scope: 'workspace' });

    expect(result.scope).toBe('workspace');
    const extractDir = path.join(rootDir, 'ext-workspace');
    const manifest = await readTarManifest(result.archivePath, extractDir);

    const paths = manifest.payload.map((e) => e.archivePath);
    expect(paths.some((p) => p.startsWith('workspace/'))).toBe(true);
    expect(paths.every((p) => p !== 'openclaw.json')).toBe(true);
    expect(paths.every((p) => !p.startsWith('credentials/'))).toBe(true);
  });

  // ─── config+creds scope ───────────────────────────────────────────────────

  test('scope=config+creds includes config and creds, excludes sessions and workspace', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir });
    const result = await svc.createBackup({ format: 'tar.gz', scope: 'config+creds' });

    expect(result.scope).toBe('config+creds');
    const extractDir = path.join(rootDir, 'ext-cc');
    const manifest = await readTarManifest(result.archivePath, extractDir);

    const paths = manifest.payload.map((e) => e.archivePath);
    expect(paths.some((p) => p === 'openclaw.json')).toBe(true);
    expect(paths.some((p) => p.startsWith('credentials/'))).toBe(true);
    expect(paths.every((p) => !p.startsWith('workspace/'))).toBe(true);
    expect(paths.every((p) => !p.startsWith('sessions/'))).toBe(true);
  });

  // ─── full scope ───────────────────────────────────────────────────────────

  test('scope=full includes all categories', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir });
    const result = await svc.createBackup({ format: 'tar.gz', scope: 'full' });

    expect(result.scope).toBe('full');
    const extractDir = path.join(rootDir, 'ext-full');
    const manifest = await readTarManifest(result.archivePath, extractDir);

    const paths = manifest.payload.map((e) => e.archivePath);
    expect(paths.some((p) => p === 'openclaw.json')).toBe(true);
    expect(paths.some((p) => p.startsWith('credentials/'))).toBe(true);
    expect(paths.some((p) => p.startsWith('workspace/'))).toBe(true);
  });

  // ─── only-config mode ─────────────────────────────────────────────────────

  test('--only-config mode produces a backup containing only openclaw.json', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir });
    const result = await svc.createBackup({ format: 'tar.gz', onlyConfig: true });

    expect(result.onlyConfig).toBe(true);
    const extractDir = path.join(rootDir, 'ext-onlyconfig');
    const manifest = await readTarManifest(result.archivePath, extractDir);

    const paths = manifest.payload.map((e) => e.archivePath);
    expect(paths).toContain('openclaw.json');
    expect(paths.every((p) => p === 'openclaw.json' || p.startsWith('manifest'))).toBe(true);
  });

  // ─── no-include-workspace mode ────────────────────────────────────────────

  test('--no-include-workspace excludes workspace directory from the archive', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir });
    const result = await svc.createBackup({ format: 'tar.gz', includeWorkspace: false });

    const extractDir = path.join(rootDir, 'ext-noworkspace');
    const manifest = await readTarManifest(result.archivePath, extractDir);

    const paths = manifest.payload.map((e) => e.archivePath);
    expect(paths.every((p) => !p.startsWith('workspace/'))).toBe(true);
  });

  // ─── ZIP format with scope ────────────────────────────────────────────────

  test('ZIP format with scope=config contains manifest and only config files', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir });
    const result = await svc.createBackup({ format: 'zip', scope: 'config', includeWorkspace: false });

    expect(result.archivePath.endsWith('.zip')).toBe(true);
    const manifest = readZipManifest(result.archivePath);

    const assetPaths = manifest.assets.map((a) => a.archivePath);
    expect(assetPaths.some((p) => p === 'openclaw.json')).toBe(true);
    expect(assetPaths.every((p) => !p.startsWith('credentials'))).toBe(true);
  });

  // ─── dry-run does not write archive ───────────────────────────────────────

  test('dry-run with scope=creds plans but writes no file', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir });
    const result = await svc.createBackup({ dryRun: true, scope: 'credentials' });

    expect(result.dryRun).toBe(true);
    expect(await fs.pathExists(result.archivePath)).toBe(false);

    const assetPaths = result.assets.map((a) => a.archivePath);
    expect(assetPaths.some((p) => p.startsWith('credentials'))).toBe(true);
  });

  // ─── assertOutputPathReady refuses to overwrite ───────────────────────────

  test('creating a backup at an existing path throws', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir });
    const outputPath = path.join(backupDir, 'existing.tar.gz');
    await fs.outputFile(outputPath, 'stale data');

    await expect(
      svc.createBackup({ format: 'tar.gz', output: outputPath })
    ).rejects.toThrow(/refusing to overwrite/i);
  });

  // ─── assertOutputPathOutsideSources blocks archive inside a backed-up dir ──

  test('creating archive inside the workspace source directory throws', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir });
    // The workspace dir gets added to sourcePathsForSafety when included in backup.
    // Writing the output archive inside workspace/ must be rejected.
    const insidePath = path.join(homeDir, 'workspace', 'inside.tar.gz');

    await expect(
      svc.createBackup({ format: 'tar.gz', output: insidePath })
    ).rejects.toThrow(/must not be written inside a source path/i);
  });
});
