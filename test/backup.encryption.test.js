/**
 * backup.encryption.test.js
 *
 * Live tests for AES-256-GCM encrypt/decrypt round-trip and full
 * encrypted-backup lifecycle.
 *
 * NO mocks. Real crypto, real disk I/O.
 */

const fs = require('fs-extra');
const os = require('os');
const path = require('path');
const BackupService = require('../lib/index');

describe('AES-256-GCM encrypt / decrypt round-trip', () => {
  let tmpDir;

  beforeEach(async () => {
    tmpDir = await fs.mkdtemp(path.join(os.tmpdir(), 'reclaw-enc-'));
  });

  afterEach(async () => {
    await fs.remove(tmpDir);
  });

  test('decrypting an encrypted file reproduces the original content', async () => {
    const svc = new BackupService({ home: tmpDir, dest: tmpDir });
    const inputPath = path.join(tmpDir, 'plain.tar.gz');
    const encPath = path.join(tmpDir, 'plain.tar.gz.enc');
    const decPath = path.join(tmpDir, 'decrypted.tar.gz');
    const content = Buffer.from('hello openclaw encryption world', 'utf8');

    await fs.writeFile(inputPath, content);
    await svc.encryptFileWithPassword(inputPath, encPath, 'testpassword');

    expect(await fs.pathExists(encPath)).toBe(true);

    await svc.decryptFileWithPassword(encPath, decPath, 'testpassword');
    const recovered = await fs.readFile(decPath);

    expect(recovered).toEqual(content);
  });

  test('encrypted file starts with RCLAWENC1 magic bytes', async () => {
    const svc = new BackupService({ home: tmpDir, dest: tmpDir });
    const inputPath = path.join(tmpDir, 'plain2.bin');
    const encPath = path.join(tmpDir, 'plain2.bin.enc');

    await fs.writeFile(inputPath, Buffer.alloc(256, 0xab));
    await svc.encryptFileWithPassword(inputPath, encPath, 'pw');

    const raw = await fs.readFile(encPath);
    expect(raw.subarray(0, 9).toString('ascii')).toBe('RCLAWENC1');
  });

  test('decryption fails with wrong password', async () => {
    const svc = new BackupService({ home: tmpDir, dest: tmpDir });
    const inputPath = path.join(tmpDir, 'secret.bin');
    const encPath = path.join(tmpDir, 'secret.enc');
    const decPath = path.join(tmpDir, 'decrypted.bin');

    await fs.writeFile(inputPath, Buffer.from('sensitive data'));
    await svc.encryptFileWithPassword(inputPath, encPath, 'correct-password');

    await expect(
      svc.decryptFileWithPassword(encPath, decPath, 'wrong-password')
    ).rejects.toThrow();
  });

  test('decryption fails on truncated/corrupted archive', async () => {
    const svc = new BackupService({ home: tmpDir, dest: tmpDir });
    const encPath = path.join(tmpDir, 'corrupt.enc');

    // Write a file that's far too short to contain a valid header
    await fs.writeFile(encPath, Buffer.alloc(10, 0xff));

    await expect(
      svc.decryptFileWithPassword(encPath, path.join(tmpDir, 'out.bin'), 'pw')
    ).rejects.toThrow(/too small|corrupted|invalid/i);
  });

  test('two encryptions of the same plaintext produce different ciphertext (random IV)', async () => {
    const svc = new BackupService({ home: tmpDir, dest: tmpDir });
    const inputPath = path.join(tmpDir, 'plain.bin');
    const enc1 = path.join(tmpDir, 'enc1.bin.enc');
    const enc2 = path.join(tmpDir, 'enc2.bin.enc');

    await fs.writeFile(inputPath, Buffer.from('same plaintext'));
    await svc.encryptFileWithPassword(inputPath, enc1, 'pw');
    await svc.encryptFileWithPassword(inputPath, enc2, 'pw');

    const buf1 = await fs.readFile(enc1);
    const buf2 = await fs.readFile(enc2);
    // They must differ (different random salt / IV)
    expect(buf1.equals(buf2)).toBe(false);
  });
});

describe('Encrypted TAR.GZ backup full lifecycle (create → verify → restore)', () => {
  let rootDir;
  let homeDir;
  let backupDir;

  beforeEach(async () => {
    process.env.RECLAW_SKIP_GATEWAY_STOP = '1';
    process.env.RECLAW_SKIP_GATEWAY_RESTART = '1';
    process.env.RECLAW_DOCTOR_REPAIR = '0';

    rootDir = await fs.mkdtemp(path.join(os.tmpdir(), 'reclaw-enc-lifecycle-'));
    homeDir = path.join(rootDir, 'home');
    backupDir = path.join(rootDir, 'backups');

    await fs.ensureDir(homeDir);
    await fs.ensureDir(backupDir);

    await fs.outputFile(path.join(homeDir, 'openclaw.json'), JSON.stringify({ env: 'prod', token: 'secretABC' }));
    await fs.outputFile(path.join(homeDir, 'credentials', 'api-key.txt'), 'myapikey');
    await fs.outputFile(path.join(homeDir, 'workspace', 'notes.md'), 'important notes');
  });

  afterEach(async () => {
    delete process.env.RECLAW_SKIP_GATEWAY_STOP;
    delete process.env.RECLAW_SKIP_GATEWAY_RESTART;
    delete process.env.RECLAW_DOCTOR_REPAIR;
    await fs.remove(rootDir);
  });

  test('creates an encrypted .tar.gz.enc archive when password is supplied', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir, password: 'supersecret' });
    const result = await svc.createBackup({ format: 'tar.gz' });

    expect(result.archivePath.endsWith('.tar.gz.enc')).toBe(true);
    expect(await fs.pathExists(result.archivePath)).toBe(true);
    expect(result.encrypted).toBe(true);
  });

  test('verifySnapshot succeeds with correct password', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir, password: 'my-pass' });
    const result = await svc.createBackup({ format: 'tar.gz' });

    const verify = await svc.verifySnapshot(result.archivePath, { silent: true });
    expect(verify.ok).toBe(true);
    expect(verify.archiveType).toBe('tar.gz');
  });

  test('verifySnapshot throws without password on encrypted archive', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir, password: 'my-pass' });
    const result = await svc.createBackup({ format: 'tar.gz' });

    const noPassSvc = new BackupService({ home: homeDir, dest: backupDir });
    await expect(
      noPassSvc.verifySnapshot(result.archivePath, { silent: true })
    ).rejects.toThrow(/password/i);
  });

  test('restore from encrypted archive with correct password recreates files', async () => {
    const password = 'restore-pass';
    const svc = new BackupService({ home: homeDir, dest: backupDir, password });
    const createResult = await svc.createBackup({ format: 'tar.gz' });

    // Mutate files
    await fs.writeJson(path.join(homeDir, 'openclaw.json'), { env: 'mutated' });
    await fs.writeFile(path.join(homeDir, 'credentials', 'api-key.txt'), 'changed-key');

    await svc.restore(createResult.archivePath);

    const restoredConfig = await fs.readJson(path.join(homeDir, 'openclaw.json'));
    expect(restoredConfig.env).toBe('prod');
    expect(restoredConfig.token).toBe('secretABC');

    const apiKey = await fs.readFile(path.join(homeDir, 'credentials', 'api-key.txt'), 'utf8');
    expect(apiKey).toBe('myapikey');
  });

  test('listBackups marks encrypted archives with encrypted:true', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir, password: 'p' });
    await svc.createBackup({ format: 'tar.gz' });

    const list = await svc.listBackups();
    expect(list.length).toBeGreaterThan(0);

    const encEntry = list.find((b) => b.encrypted === true);
    expect(encEntry).toBeDefined();
    expect(encEntry.archiveType).toBe('tar.gz');
  });
});
