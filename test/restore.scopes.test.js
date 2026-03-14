/**
 * restore.scopes.test.js
 *
 * Live tests for scope-restricted restore operations.
 * Every scope combination has a real backup created and verified after restore.
 * NO mocks – lifecycle hooks are bypassed via env vars the real code supports.
 */

const fs = require('fs-extra');
const os = require('os');
const path = require('path');
const BackupService = require('../lib/index');

describe('Scope-restricted restore', () => {
  let rootDir;
  let homeDir;
  let backupDir;

  beforeEach(async () => {
    // Skip gateway and doctor side-effects so tests can run without OpenClaw installed
    process.env.RECLAW_SKIP_GATEWAY_STOP = '1';
    process.env.RECLAW_SKIP_GATEWAY_RESTART = '1';
    process.env.RECLAW_DOCTOR_REPAIR = '0';

    rootDir = await fs.mkdtemp(path.join(os.tmpdir(), 'reclaw-restore-scopes-'));
    homeDir = path.join(rootDir, 'home');
    backupDir = path.join(rootDir, 'backups');

    await fs.ensureDir(homeDir);
    await fs.ensureDir(backupDir);

    // Seed all categories
    await fs.outputFile(path.join(homeDir, 'openclaw.json'), JSON.stringify({ restored: false, token: 'orig' }));
    await fs.outputFile(path.join(homeDir, '.env'), 'ENV=original');
    await fs.outputFile(path.join(homeDir, 'credentials', 'api.txt'), 'original-cred');
    await fs.outputFile(path.join(homeDir, 'sessions', 'main.json'), '{"session":"orig"}');
    await fs.outputFile(path.join(homeDir, 'memory', 'data.txt'), 'original-memory');
    await fs.outputFile(path.join(homeDir, 'workspace', 'notes.md'), 'original-notes');
  });

  afterEach(async () => {
    delete process.env.RECLAW_SKIP_GATEWAY_STOP;
    delete process.env.RECLAW_SKIP_GATEWAY_RESTART;
    delete process.env.RECLAW_DOCTOR_REPAIR;
    await fs.remove(rootDir);
  });

  // ─── scope=config restores config, leaves other dirs untouched ────────────

  test('scope=config restores openclaw.json and .env, leaves credentials and workspace unchanged', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir });
    const archive = await svc.createSnapshot();

    // Mutate everything
    await fs.writeJson(path.join(homeDir, 'openclaw.json'), { restored: true, token: 'changed' });
    await fs.writeFile(path.join(homeDir, '.env'), 'ENV=mutated');
    await fs.writeFile(path.join(homeDir, 'credentials', 'api.txt'), 'mutated-cred');
    await fs.writeFile(path.join(homeDir, 'workspace', 'notes.md'), 'mutated-notes');

    await svc.restore(archive, { scope: 'config' });

    const cfg = await fs.readJson(path.join(homeDir, 'openclaw.json'));
    expect(cfg.token).toBe('orig');

    const envContent = await fs.readFile(path.join(homeDir, '.env'), 'utf8');
    expect(envContent).toBe('ENV=original');

    // credentials and workspace must remain mutated (scope excluded them)
    const cred = await fs.readFile(path.join(homeDir, 'credentials', 'api.txt'), 'utf8');
    expect(cred).toBe('mutated-cred');

    const notes = await fs.readFile(path.join(homeDir, 'workspace', 'notes.md'), 'utf8');
    expect(notes).toBe('mutated-notes');
  });

  // ─── scope=creds restores credentials, leaves others unchanged ────────────

  test('scope=creds restores credentials dir, leaves config and workspace unchanged', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir });
    const archive = await svc.createSnapshot();

    await fs.writeJson(path.join(homeDir, 'openclaw.json'), { token: 'changed-config' });
    await fs.writeFile(path.join(homeDir, 'credentials', 'api.txt'), 'changed-cred');
    await fs.writeFile(path.join(homeDir, 'workspace', 'notes.md'), 'changed-notes');

    await svc.restore(archive, { scope: 'creds' });

    const cfg = await fs.readJson(path.join(homeDir, 'openclaw.json'));
    expect(cfg.token).toBe('changed-config');  // config was NOT restored

    const cred = await fs.readFile(path.join(homeDir, 'credentials', 'api.txt'), 'utf8');
    expect(cred).toBe('original-cred');

    const notes = await fs.readFile(path.join(homeDir, 'workspace', 'notes.md'), 'utf8');
    expect(notes).toBe('changed-notes');  // workspace NOT restored
  });

  // ─── scope=sessions restores session files ────────────────────────────────

  test('scope=sessions restores sessions/memory dirs, leaves config and credentials unchanged', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir });
    const archive = await svc.createSnapshot();

    await fs.writeJson(path.join(homeDir, 'openclaw.json'), { token: 'changed' });
    await fs.writeFile(path.join(homeDir, 'sessions', 'main.json'), '{"session":"changed"}');
    await fs.writeFile(path.join(homeDir, 'memory', 'data.txt'), 'changed-memory');
    await fs.writeFile(path.join(homeDir, 'credentials', 'api.txt'), 'changed-cred');

    await svc.restore(archive, { scope: 'sessions' });

    const sessionContent = await fs.readFile(path.join(homeDir, 'sessions', 'main.json'), 'utf8');
    expect(sessionContent).toBe('{"session":"orig"}');

    const memContent = await fs.readFile(path.join(homeDir, 'memory', 'data.txt'), 'utf8');
    expect(memContent).toBe('original-memory');

    // config was NOT restored
    const cfg = await fs.readJson(path.join(homeDir, 'openclaw.json'));
    expect(cfg.token).toBe('changed');

    // credentials were NOT restored
    const cred = await fs.readFile(path.join(homeDir, 'credentials', 'api.txt'), 'utf8');
    expect(cred).toBe('changed-cred');
  });

  // ─── scope=workspace restores workspace ───────────────────────────────────

  test('scope=workspace restores workspace dir, leaves config and credentials unchanged', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir });
    const archive = await svc.createSnapshot();

    await fs.writeJson(path.join(homeDir, 'openclaw.json'), { token: 'changed' });
    await fs.writeFile(path.join(homeDir, 'workspace', 'notes.md'), 'changed-notes');
    await fs.writeFile(path.join(homeDir, 'credentials', 'api.txt'), 'changed-cred');

    await svc.restore(archive, { scope: 'workspace' });

    const notes = await fs.readFile(path.join(homeDir, 'workspace', 'notes.md'), 'utf8');
    expect(notes).toBe('original-notes');

    const cfg = await fs.readJson(path.join(homeDir, 'openclaw.json'));
    expect(cfg.token).toBe('changed');  // config NOT restored

    const cred = await fs.readFile(path.join(homeDir, 'credentials', 'api.txt'), 'utf8');
    expect(cred).toBe('changed-cred');  // creds NOT restored
  });

  // ─── scope=config+creds+sessions restores three categories ───────────────

  test('scope=config+creds+sessions restores all three, leaves workspace unchanged', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir });
    const archive = await svc.createSnapshot();

    await fs.writeJson(path.join(homeDir, 'openclaw.json'), { token: 'changed' });
    await fs.writeFile(path.join(homeDir, 'credentials', 'api.txt'), 'changed-cred');
    await fs.writeFile(path.join(homeDir, 'sessions', 'main.json'), '{"session":"changed"}');
    await fs.writeFile(path.join(homeDir, 'workspace', 'notes.md'), 'changed-notes');

    await svc.restore(archive, { scope: 'config+creds+sessions' });

    const cfg = await fs.readJson(path.join(homeDir, 'openclaw.json'));
    expect(cfg.token).toBe('orig');

    const cred = await fs.readFile(path.join(homeDir, 'credentials', 'api.txt'), 'utf8');
    expect(cred).toBe('original-cred');

    const session = await fs.readFile(path.join(homeDir, 'sessions', 'main.json'), 'utf8');
    expect(session).toBe('{"session":"orig"}');

    const notes = await fs.readFile(path.join(homeDir, 'workspace', 'notes.md'), 'utf8');
    expect(notes).toBe('changed-notes');  // workspace NOT restored
  });

  // ─── invalid scope throws before touching any file ────────────────────────

  test('invalid restore scope throws without modifying any file', async () => {
    const svc = new BackupService({ home: homeDir, dest: backupDir });
    const archive = await svc.createSnapshot();

    await fs.writeJson(path.join(homeDir, 'openclaw.json'), { token: 'unmodified' });

    await expect(
      svc.restore(archive, { scope: 'not-a-real-scope' })
    ).rejects.toThrow(/invalid backup scope/i);

    // The file should still reflect the mutated value (restore never ran)
    const cfg = await fs.readJson(path.join(homeDir, 'openclaw.json'));
    expect(cfg.token).toBe('unmodified');
  });
});
