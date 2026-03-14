const fs = require('fs-extra');
const os = require('os');
const path = require('path');
const BackupService = require('../lib/index');

describe('OpenClaw mac integration hooks during restore', () => {
  let rootDir;
  let homeDir;
  let backupDir;

  beforeEach(async () => {
    rootDir = await fs.mkdtemp(path.join(os.tmpdir(), 'reclaw-test-integration-'));
    homeDir = path.join(rootDir, 'home');
    backupDir = path.join(rootDir, 'backups');

    await fs.ensureDir(homeDir);
    await fs.ensureDir(backupDir);

    await fs.outputFile(path.join(homeDir, 'openclaw.json'), JSON.stringify({ restored: false }, null, 2));
    await fs.outputFile(path.join(homeDir, 'workspace', 'check.txt'), 'before-backup');

    // Ensure restore lifecycle hooks are active in this integration test.
    process.env.RECLAW_SKIP_GATEWAY_STOP = '0';
    process.env.RECLAW_SKIP_GATEWAY_RESTART = '0';
  });

  afterEach(async () => {
    delete process.env.RECLAW_SKIP_GATEWAY_STOP;
    delete process.env.RECLAW_SKIP_GATEWAY_RESTART;

    if (rootDir) {
      await fs.remove(rootDir);
    }
  });

  test('restore executes OpenClaw lifecycle commands and restores files', async () => {
    const service = new BackupService({ home: homeDir, dest: backupDir });
    const cliSpy = jest.spyOn(service, 'runCliCommand').mockReturnValue({ status: 0, stdout: '', stderr: '' });
    const archivePath = await service.createSnapshot();

    await fs.outputFile(path.join(homeDir, 'openclaw.json'), JSON.stringify({ restored: true }, null, 2));
    await fs.outputFile(path.join(homeDir, 'workspace', 'check.txt'), 'mutated');

    await service.restore(archivePath);

    const restoredConfig = await fs.readJson(path.join(homeDir, 'openclaw.json'));
    const restoredContent = await fs.readFile(path.join(homeDir, 'workspace', 'check.txt'), 'utf8');
    const lifecycleCalls = cliSpy.mock.calls.map(([cmd, args]) => `${cmd} ${args.join(' ')}`).join('\n');

    expect(restoredConfig).toEqual(expect.objectContaining({ restored: false }));
    expect(restoredConfig.gateway).toEqual(
      expect.objectContaining({
        auth: expect.objectContaining({
          token: expect.any(String)
        })
      })
    );
    expect(restoredContent).toBe('before-backup');

    expect(lifecycleCalls).toMatch(/gateway stop/);
    expect(lifecycleCalls).toMatch(/doctor --repair --non-interactive/);
    expect(lifecycleCalls).toMatch(/gateway restart/);

    cliSpy.mockRestore();
  });
});
