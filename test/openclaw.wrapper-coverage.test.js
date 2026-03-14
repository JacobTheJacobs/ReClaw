const pkg = require('../package.json');

describe('OpenClaw wrapper coverage', () => {
  test('includes required backup and recovery wrappers', () => {
    const requiredScripts = [
      'openclaw:backup:create',
      'openclaw:backup:create:verify',
      'openclaw:backup:create:plan',
      'openclaw:backup:create:only-config',
      'openclaw:backup:create:no-workspace',
      'openclaw:backup:verify',
      'openclaw:reset:safe',
      'openclaw:reset:dry-run',
      'openclaw:doctor',
      'openclaw:doctor:repair',
      'openclaw:doctor:repair:force',
      'openclaw:doctor:non-interactive',
      'openclaw:doctor:deep',
      'openclaw:doctor:yes',
      'openclaw:doctor:token',
      'openclaw:doctor:fix',
      'openclaw:security',
      'openclaw:security:deep',
      'openclaw:security:fix',
      'openclaw:security:json',
      'openclaw:secrets:reload',
      'openclaw:secrets:audit',
      'openclaw:status',
      'openclaw:status:deep',
      'openclaw:status:all',
      'openclaw:status:usage',
      'openclaw:health',
      'openclaw:health:json',
      'openclaw:channels:status',
      'openclaw:channels:status:probe',
      'openclaw:models:status',
      'openclaw:models:status:probe',
      'openclaw:gateway:start',
      'openclaw:gateway:stop',
      'openclaw:gateway:restart',
      'openclaw:gateway:status',
      'openclaw:gateway:status:deep',
      'openclaw:gateway:install',
      'openclaw:gateway:uninstall',
      'openclaw:logs:follow'
    ];

    for (const key of requiredScripts) {
      expect(pkg.scripts[key]).toBeTruthy();
    }
  });
});
