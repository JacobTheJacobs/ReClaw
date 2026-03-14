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

describe('openclaw alias command registration', () => {
  test('root help includes key direct alias commands', () => {
    const result = runCli(['--help']);

    expect(result.status).toBe(0);
    expect(result.stdout).toContain('doctor [args...]');
    expect(result.stdout).toContain('reset [args...]');
    expect(result.stdout).toContain('security [args...]');
    expect(result.stdout).toContain('status [args...]');
    expect(result.stdout).toContain('health [args...]');
    expect(result.stdout).toContain('gateway [args...]');
    expect(result.stdout).toContain('channels [args...]');
    expect(result.stdout).toContain('models [args...]');
    expect(result.stdout).toContain('secrets [args...]');
    expect(result.stdout).toContain('logs [args...]');
    expect(result.stdout).toContain('setup [args...]');
  });

  test('doctor alias has local help output', () => {
    const result = runCli(['doctor', '--help']);

    expect(result.status).toBe(0);
    expect(result.stdout).toContain('Run openclaw doctor commands');
    expect(result.stdout).toContain('[args...]');
  });

  test('gateway alias has local help output', () => {
    const result = runCli(['gateway', '--help']);

    expect(result.status).toBe(0);
    expect(result.stdout).toContain('Run openclaw gateway commands');
    expect(result.stdout).toContain('[args...]');
  });
});
