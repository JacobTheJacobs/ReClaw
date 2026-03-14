/**
 * cli.aliases.all.test.js
 *
 * Live --help output tests for every passthrough alias command registered
 * in the CLI.  No mocks – the real CLI process is spawned.
 *
 * These tests verify:
 *  1. The command is registered (exit 0, no "unknown command" error).
 *  2. The help text mentions the expected description.
 *  3. The variadic [args...] placeholder is present.
 */

const path = require('path');
const { spawnSync } = require('child_process');

const CLI_PATH = path.join(__dirname, '..', 'bin', 'cli.js');

function runHelp(command) {
  return spawnSync(process.execPath, [CLI_PATH, command, '--help'], {
    encoding: 'utf-8',
    timeout: 30000,
    cwd: path.join(__dirname, '..')
  });
}

const ALIAS_TABLE = [
  // [command, description substring]
  ['doctor',   'openclaw doctor'],
  ['reset',    'openclaw reset'],
  ['security', 'openclaw security'],
  ['secrets',  'openclaw secrets'],
  ['status',   'openclaw status'],
  ['health',   'openclaw health'],
  ['channels', 'openclaw channels'],
  ['models',   'openclaw models'],
  ['gateway',  'openclaw gateway'],
  ['logs',     'openclaw logs'],
  ['setup',    'openclaw setup'],
  ['sessions', 'openclaw sessions'],
  ['skills',   'openclaw skills'],
];

describe('All alias commands register with correct --help output', () => {
  test.each(ALIAS_TABLE)('%s --help exits 0 and shows description', (command, descSubstring) => {
    const result = runHelp(command);

    expect(result.status).toBe(0);
    const combined = (result.stdout + result.stderr).toLowerCase();
    expect(combined).toContain(descSubstring.toLowerCase());
    expect(result.stdout).toContain('[args...]');
  });
});

describe('Root --help lists all alias commands', () => {
  test('root --help shows all alias commands', () => {
    const result = spawnSync(process.execPath, [CLI_PATH, '--help'], {
      encoding: 'utf-8',
      timeout: 30000,
      cwd: path.join(__dirname, '..')
    });

    expect(result.status).toBe(0);

    const commands = ALIAS_TABLE.map(([cmd]) => cmd);
    for (const cmd of commands) {
      expect(result.stdout).toContain(`${cmd} [args...]`);
    }
  });
});

describe('openclaw passthrough command --help', () => {
  test('openclaw --help exits 0 and shows usage', () => {
    const result = spawnSync(process.execPath, [CLI_PATH, 'openclaw', '--help'], {
      encoding: 'utf-8',
      timeout: 30000,
      cwd: path.join(__dirname, '..')
    });

    expect(result.status).toBe(0);
    expect(result.stdout + result.stderr).toMatch(/openclaw/i);
  });
});
