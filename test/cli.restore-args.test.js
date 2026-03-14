const { parseRestoreArgsFromTokens, resolveRestoreInputs } = require('../bin/cli');

describe('restore argument resolution', () => {
  test('parses long and short password flags from token list', () => {
    expect(parseRestoreArgsFromTokens(['restore', '--password', 'abc', 'backup.zip'])).toEqual({
      password: 'abc',
      archive: 'backup.zip'
    });

    expect(parseRestoreArgsFromTokens(['restore', '-p', 'xyz', 'backup.zip'])).toEqual({
      password: 'xyz',
      archive: 'backup.zip'
    });
  });

  test('resolves password from npm env and archive from argv when npm swallows flags', () => {
    const resolved = resolveRestoreInputs(
      undefined,
      { password: undefined },
      { parent: { args: ['restore', 'backup.zip'] } },
      { npm_config_password: 'from-npm-env' },
      ['node', 'bin/cli.js', 'restore', 'backup.zip']
    );

    expect(resolved).toEqual({
      password: 'from-npm-env',
      archive: 'backup.zip'
    });
  });

  test('falls back to npm_config_argv for archive/password when needed', () => {
    const resolved = resolveRestoreInputs(
      undefined,
      { password: undefined },
      { parent: { args: ['restore'] } },
      {
        npm_config_argv: JSON.stringify({
          original: ['run', 'restore', '--password', 'secret', '/tmp/backup.zip']
        })
      },
      ['node', 'bin/cli.js', 'restore']
    );

    expect(resolved).toEqual({
      password: 'secret',
      archive: '/tmp/backup.zip'
    });
  });
});
