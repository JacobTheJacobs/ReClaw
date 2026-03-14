/**
 * backup.utility.test.js
 *
 * Live unit tests for BackupService pure-utility methods.
 * NO mocks. If jest.fn / jest.spyOn is found in this file the test suite is
 * considered broken by design.
 */

const BackupService = require('../lib/index');

// A minimal service instance is enough for pure helper methods.
const svc = new BackupService({ home: '/tmp/__unused__', dest: '/tmp/__unused__' });

// ─── normalizeBackupScope ────────────────────────────────────────────────────

describe('BackupService.normalizeBackupScope', () => {
  test('returns full scope for the token "full"', () => {
    const result = svc.normalizeBackupScope('full');
    expect(result.raw).toBe('full');
    expect(result.tokens.has('full')).toBe(true);
  });

  test('returns full scope when no argument is provided (default)', () => {
    const result = svc.normalizeBackupScope(undefined);
    expect(result.tokens.has('full')).toBe(true);
  });

  test('parses a single valid token', () => {
    const result = svc.normalizeBackupScope('config');
    expect(result.raw).toBe('config');
    expect(result.tokens.has('config')).toBe(true);
    expect(result.tokens.has('full')).toBe(false);
  });

  test('parses compound scope config+creds', () => {
    const result = svc.normalizeBackupScope('config+creds');
    expect(result.tokens.has('config')).toBe(true);
    expect(result.tokens.has('creds')).toBe(true);
    expect(result.tokens.has('sessions')).toBe(false);
  });

  test('parses config+creds+sessions', () => {
    const result = svc.normalizeBackupScope('config+creds+sessions');
    expect(result.tokens.has('config')).toBe(true);
    expect(result.tokens.has('creds')).toBe(true);
    expect(result.tokens.has('sessions')).toBe(true);
  });

  test('resolves the "credentials" alias to "creds"', () => {
    const result = svc.normalizeBackupScope('credentials');
    expect(result.tokens.has('creds')).toBe(true);
  });

  test('resolves the "credential" alias to "creds"', () => {
    const result = svc.normalizeBackupScope('credential');
    expect(result.tokens.has('creds')).toBe(true);
  });

  test('is case-insensitive', () => {
    const result = svc.normalizeBackupScope('CONFIG+CREDS');
    expect(result.tokens.has('config')).toBe(true);
    expect(result.tokens.has('creds')).toBe(true);
  });

  test('deduplicates repeated tokens', () => {
    const result = svc.normalizeBackupScope('config+config+creds');
    expect(result.raw).toBe('config+creds');
  });

  test('throws for an unknown token', () => {
    expect(() => svc.normalizeBackupScope('bogus')).toThrow(/invalid backup scope/i);
  });

  test('throws for an empty string', () => {
    // empty string falls back to default "full" — it should resolve cleanly
    const result = svc.normalizeBackupScope('');
    expect(result.tokens.has('full')).toBe(true);
  });
});

// ─── categorizeTopLevelEntry ─────────────────────────────────────────────────

describe('BackupService.categorizeTopLevelEntry', () => {
  const cases = [
    ['openclaw.json', 'config'],
    ['.env', 'config'],
    ['user.md', 'config'],
    ['identity.md', 'config'],
    ['credentials', 'creds'],
    ['credential', 'creds'],
    ['auth', 'creds'],
    ['oauth', 'creds'],
    ['secrets', 'creds'],
    ['devices.json', 'creds'],
    ['auth-profiles.json', 'creds'],
    ['credentials.json', 'creds'],
    ['tokens.json', 'creds'],
    ['sessions', 'sessions'],
    ['memory', 'sessions'],
    ['logs', 'sessions'],
    ['history', 'sessions'],
    ['databases', 'sessions'],
    ['workspace', 'workspace'],
    ['workspaces', 'workspace'],
    ['anything-else.txt', 'other'],
    ['', 'other'],
  ];

  test.each(cases)('"%s" → "%s"', (entry, expected) => {
    expect(svc.categorizeTopLevelEntry(entry)).toBe(expected);
  });
});

// ─── scopeIncludesCategory ───────────────────────────────────────────────────

describe('BackupService.scopeIncludesCategory', () => {
  test('full scope includes every category', () => {
    const scopeInfo = svc.normalizeBackupScope('full');
    expect(svc.scopeIncludesCategory(scopeInfo, 'config')).toBe(true);
    expect(svc.scopeIncludesCategory(scopeInfo, 'creds')).toBe(true);
    expect(svc.scopeIncludesCategory(scopeInfo, 'sessions')).toBe(true);
    expect(svc.scopeIncludesCategory(scopeInfo, 'workspace')).toBe(true);
  });

  test('config scope includes config but not creds', () => {
    const scopeInfo = svc.normalizeBackupScope('config');
    expect(svc.scopeIncludesCategory(scopeInfo, 'config')).toBe(true);
    expect(svc.scopeIncludesCategory(scopeInfo, 'creds')).toBe(false);
    expect(svc.scopeIncludesCategory(scopeInfo, 'sessions')).toBe(false);
  });

  test('config+creds includes config and creds but not sessions', () => {
    const scopeInfo = svc.normalizeBackupScope('config+creds');
    expect(svc.scopeIncludesCategory(scopeInfo, 'config')).toBe(true);
    expect(svc.scopeIncludesCategory(scopeInfo, 'creds')).toBe(true);
    expect(svc.scopeIncludesCategory(scopeInfo, 'sessions')).toBe(false);
  });

  test('null scopeInfo returns true (include-all fallback)', () => {
    expect(svc.scopeIncludesCategory(null, 'config')).toBe(true);
  });
});

// ─── shouldIncludeTopLevelEntry ──────────────────────────────────────────────

describe('BackupService.shouldIncludeTopLevelEntry', () => {
  test('includes openclaw.json under config scope', () => {
    const scopeInfo = svc.normalizeBackupScope('config');
    expect(svc.shouldIncludeTopLevelEntry('openclaw.json', scopeInfo)).toBe(true);
  });

  test('excludes workspace dir under config scope', () => {
    const scopeInfo = svc.normalizeBackupScope('config');
    expect(svc.shouldIncludeTopLevelEntry('workspace', scopeInfo)).toBe(false);
  });

  test('includes everything under full scope', () => {
    const scopeInfo = svc.normalizeBackupScope('full');
    expect(svc.shouldIncludeTopLevelEntry('workspace', scopeInfo)).toBe(true);
    expect(svc.shouldIncludeTopLevelEntry('credentials', scopeInfo)).toBe(true);
    expect(svc.shouldIncludeTopLevelEntry('openclaw.json', scopeInfo)).toBe(true);
  });
});

// ─── parseOlderThanToMs ──────────────────────────────────────────────────────

describe('BackupService.parseOlderThanToMs', () => {
  test('null returns null', () => {
    expect(svc.parseOlderThanToMs(null)).toBeNull();
  });

  test('undefined returns null', () => {
    expect(svc.parseOlderThanToMs(undefined)).toBeNull();
  });

  test('empty string returns null', () => {
    expect(svc.parseOlderThanToMs('')).toBeNull();
  });

  const unitCases = [
    ['1ms', 1],
    ['1s', 1000],
    ['1m', 60 * 1000],
    ['1h', 60 * 60 * 1000],
    ['1d', 24 * 60 * 60 * 1000],
    ['1w', 7 * 24 * 60 * 60 * 1000],
    ['30d', 30 * 24 * 60 * 60 * 1000],
    ['12h', 12 * 60 * 60 * 1000],
    ['2w', 2 * 7 * 24 * 60 * 60 * 1000],
  ];

  test.each(unitCases)('"%s" → %d ms', (value, expected) => {
    expect(svc.parseOlderThanToMs(value)).toBe(expected);
  });

  test('throws for invalid format', () => {
    expect(() => svc.parseOlderThanToMs('30days')).toThrow(/invalid.*older-than/i);
  });

  test('throws for missing unit', () => {
    expect(() => svc.parseOlderThanToMs('30')).toThrow(/invalid.*older-than/i);
  });
});

// ─── isEncryptedArchiveName ──────────────────────────────────────────────────

describe('BackupService.isEncryptedArchiveName', () => {
  test.each([
    ['backup.tar.gz.enc', true],
    ['backup.tgz.enc', true],
    ['backup.enc', true],
    ['BACKUP.TAR.GZ.ENC', true],
    ['backup.tar.gz', false],
    ['backup.zip', false],
    ['backup.tgz', false],
    ['', false],
  ])('"%s" → %s', (name, expected) => {
    expect(svc.isEncryptedArchiveName(name)).toBe(expected);
  });
});

// ─── inferArchiveTypeFromName ────────────────────────────────────────────────

describe('BackupService.inferArchiveTypeFromName', () => {
  test.each([
    ['backup.zip', 'zip'],
    ['backup.tar.gz', 'tar.gz'],
    ['backup.tgz', 'tar.gz'],
    ['backup.tar.gz.enc', 'tar.gz'],
    ['backup.tgz.enc', 'tar.gz'],
    ['backup.enc', 'tar.gz'],
    ['backup.UNKNOWN', 'unknown'],
    ['', 'unknown'],
  ])('"%s" → "%s"', (name, expected) => {
    expect(svc.inferArchiveTypeFromName(name)).toBe(expected);
  });
});

// ─── inferArchiveFormatFromOutput ────────────────────────────────────────────

describe('BackupService.inferArchiveFormatFromOutput', () => {
  test.each([
    ['/tmp/backup.tar.gz', 'tar.gz'],
    ['/tmp/backup.tgz', 'tar.gz'],
    ['/tmp/backup.tar.gz.enc', 'tar.gz'],
    ['/tmp/backup.enc', 'tar.gz'],
    ['/tmp/backup.zip', 'zip'],
    ['/tmp/backup', null],
    ['', null],
    [null, null],
  ])('"%s" → %s', (output, expected) => {
    expect(svc.inferArchiveFormatFromOutput(output)).toBe(expected);
  });
});

// ─── normalizeArchiveFormat ──────────────────────────────────────────────────

describe('BackupService.normalizeArchiveFormat', () => {
  test.each([
    ['zip', 'zip'],
    ['ZIP', 'zip'],
    ['tar.gz', 'tar.gz'],
    ['TAR.GZ', 'tar.gz'],
    ['tgz', 'tar.gz'],
    ['targz', 'tar.gz'],
  ])('"%s" → "%s"', (input, expected) => {
    expect(svc.normalizeArchiveFormat(input)).toBe(expected);
  });

  test('throws for unsupported format', () => {
    expect(() => svc.normalizeArchiveFormat('rar')).toThrow(/unsupported archive format/i);
  });
});

// ─── getArchiveType ──────────────────────────────────────────────────────────

describe('BackupService.getArchiveType', () => {
  test.each([
    ['/tmp/x.zip', 'zip'],
    ['/tmp/x.tar.gz', 'tar.gz'],
    ['/tmp/x.tgz', 'tar.gz'],
    ['/tmp/x.tar.gz.enc', 'encrypted'],
    ['/tmp/x.enc', 'encrypted'],
  ])('"%s" → "%s"', (archivePath, expected) => {
    expect(svc.getArchiveType(archivePath)).toBe(expected);
  });

  test('throws for unknown type', () => {
    expect(() => svc.getArchiveType('/tmp/x.rar')).toThrow(/unsupported archive type/i);
  });
});

// ─── expandUserPath ──────────────────────────────────────────────────────────

describe('BackupService.expandUserPath', () => {
  const home = require('os').homedir();

  test('expands "~" alone to home directory', () => {
    expect(svc.expandUserPath('~')).toBe(home);
  });

  test('expands "~/path" to home + path', () => {
    expect(svc.expandUserPath('~/backups')).toBe(require('path').join(home, 'backups'));
  });

  test('leaves absolute paths untouched', () => {
    expect(svc.expandUserPath('/absolute/path')).toBe('/absolute/path');
  });

  test('leaves relative paths untouched', () => {
    expect(svc.expandUserPath('relative/path')).toBe('relative/path');
  });

  test('returns non-string input unchanged', () => {
    expect(svc.expandUserPath(null)).toBeNull();
    expect(svc.expandUserPath(42)).toBe(42);
  });
});
