const fs = require('fs-extra');
const os = require('os');
const path = require('path');
const AdmZip = require('adm-zip');
const BackupService = require('../lib/index');

describe('BackupService backup', () => {
  let rootDir;
  let homeDir;
  let backupDir;

  beforeEach(async () => {
    rootDir = await fs.mkdtemp(path.join(os.tmpdir(), 'reclaw-test-backup-'));
    homeDir = path.join(rootDir, 'home');
    backupDir = path.join(rootDir, 'backups');

    await fs.ensureDir(homeDir);
    await fs.ensureDir(backupDir);

    await fs.outputFile(path.join(homeDir, 'openclaw.json'), JSON.stringify({ hello: 'world' }, null, 2));
    await fs.outputFile(path.join(homeDir, '.env'), 'OPENCLAW_TEST=1\n');
    await fs.outputFile(path.join(homeDir, 'workspace', 'notes.txt'), 'backup me');
    await fs.outputFile(path.join(homeDir, 'memory', 'main.sqlite'), 'fake-sqlite-content');
  });

  afterEach(async () => {
    if (rootDir) {
      await fs.remove(rootDir);
    }
  });

  test('creates a zip archive containing manifest and expected files', async () => {
    const service = new BackupService({ home: homeDir, dest: backupDir });
    const archivePath = await service.createSnapshot();

    expect(await fs.pathExists(archivePath)).toBe(true);

    const zip = new AdmZip(archivePath);
    const names = zip.getEntries().map((e) => e.entryName);

    expect(names).toContain('manifest.json');
    expect(names).toContain('openclaw.json');
    expect(names).toContain('.env');
    expect(names).toContain('workspace/notes.txt');

    const manifest = JSON.parse(zip.readAsText('manifest.json'));
    expect(manifest.schemaVersion).toBe(1);
    expect(Array.isArray(manifest.files)).toBe(true);
    expect(Array.isArray(manifest.assets)).toBe(true);
    expect(Array.isArray(manifest.payload)).toBe(true);
    expect(manifest.files).toContain('openclaw.json');
    expect(manifest.files).toContain('workspace');

    expect(manifest.assets).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ archivePath: 'workspace' }),
        expect.objectContaining({ archivePath: 'openclaw.json' })
      ])
    );

    const notePayload = manifest.payload.find((entry) => entry.archivePath === 'workspace/notes.txt');
    expect(notePayload).toEqual(
      expect.objectContaining({
        archivePath: 'workspace/notes.txt',
        size: expect.any(Number),
        sha256: expect.stringMatching(/^[a-f0-9]{64}$/i)
      })
    );
  });
});
