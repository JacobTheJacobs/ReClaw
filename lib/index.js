const fs = require('fs-extra');
const path = require('path');
const os = require('os');
const crypto = require('crypto');
const { execSync, spawnSync } = require('child_process');
const archiver = require('archiver');
const AdmZip = require('adm-zip');
const tar = require('tar');
const config = require('../config/default');

// Register the encrypted zip format if the package is available
try {
  archiver.registerFormat('zip-encrypted', require('archiver-zip-encrypted'));
} catch (e) {
  // If the optional dependency is missing, we'll gracefully degrade to standard zip
  console.warn('Warning: archiver-zip-encrypted not found. Encrypted backups may not work properly.');
}

const WINDOWS_ABSOLUTE_ARCHIVE_PATH_RE = /^[A-Za-z]:[\\/]/;
const ARCHIVE_ENCRYPTION_MAGIC = Buffer.from('RCLAWENC1');
const ARCHIVE_ENCRYPTION_SALT_BYTES = 16;
const ARCHIVE_ENCRYPTION_IV_BYTES = 12;
const ARCHIVE_ENCRYPTION_TAG_BYTES = 16;
const ARCHIVE_ENCRYPTION_PBKDF2_ROUNDS = 310000;
const BACKUP_SCOPE_TOKEN_ALIASES = {
  credentials: 'creds',
  credential: 'creds'
};
const BACKUP_SCOPE_ALLOWED_TOKENS = new Set(['config', 'creds', 'sessions', 'workspace']);
const BACKUP_SCOPE_EXAMPLE = 'full | config | creds | sessions | config+creds | config+creds+sessions';
const BACKUP_CONFIG_ENTRIES = new Set(['openclaw.json', '.env', 'user.md', 'identity.md']);
const BACKUP_CREDENTIAL_ENTRIES = new Set([
  'credentials',
  'credential',
  'auth',
  'oauth',
  'secrets',
  'devices.json',
  'auth-profiles.json',
  'credentials.json',
  'tokens.json',
]);
const BACKUP_SESSION_ENTRIES = new Set(['sessions', 'memory', 'logs', 'history', 'databases']);
const BACKUP_WORKSPACE_ENTRIES = new Set(['workspace', 'workspaces']);

function normalizeRawArchivePath(entryPath) {
  return String(entryPath || '').replace(/^\.\//, '').replace(/\/+$/g, '');
}

function stripTrailingSlashes(value) {
  return String(value || '').replace(/\/+$/g, '');
}

function normalizeArchivePath(entryPath, label) {
  const trimmed = stripTrailingSlashes(String(entryPath || '').trim());
  if (!trimmed) {
    throw new Error(`${label} is empty.`);
  }
  if (trimmed.startsWith('/') || WINDOWS_ABSOLUTE_ARCHIVE_PATH_RE.test(trimmed)) {
    throw new Error(`${label} must be relative: ${entryPath}`);
  }
  if (trimmed.includes('\\')) {
    throw new Error(`${label} must use forward slashes: ${entryPath}`);
  }

  const segments = trimmed.split('/');
  if (segments.some((segment) => segment === '.' || segment === '..')) {
    throw new Error(`${label} contains path traversal segments: ${entryPath}`);
  }

  const normalized = stripTrailingSlashes(path.posix.normalize(trimmed));
  if (!normalized || normalized === '.' || normalized === '..' || normalized.startsWith('../')) {
    throw new Error(`${label} resolves outside the archive root: ${entryPath}`);
  }

  return normalized;
}

function isPathWithinArchive(childPath, parentPath) {
  const relative = path.posix.relative(parentPath, childPath);
  return relative === '' || (!relative.startsWith('../') && relative !== '..');
}

function toPosixRelative(rootDir, absolutePath) {
  const relative = path.relative(rootDir, absolutePath);
  return relative.split(path.sep).join('/');
}

function hashBufferSha256(buffer) {
  return crypto.createHash('sha256').update(buffer).digest('hex');
}

class BackupService {
  constructor(options = {}) {
    this.home = options.home || config.openclawHome;
    this.dest = options.dest || config.backupDir;
    this.dryRun = options.dryRun || false;
    this.password = options.password || null;
    const includeBrowserFromEnv =
      process.env.RECLAW_INCLUDE_BROWSER === '1' ||
      process.env.RECLAW_INCLUDE_BROWSER === 'true';
    this.includeBrowser = options.includeBrowser === true || includeBrowserFromEnv;
  }

  async createSnapshot(options = {}) {
    const result = await this.createBackup(options);
    return result.archivePath;
  }

  expandUserPath(rawPath) {
    if (typeof rawPath !== 'string') {
      return rawPath;
    }

    if (rawPath === '~') {
      return os.homedir();
    }

    if (rawPath.startsWith('~/') || rawPath.startsWith('~\\')) {
      return path.join(os.homedir(), rawPath.slice(2));
    }

    return rawPath;
  }

  inferArchiveFormatFromOutput(outputOption) {
    if (typeof outputOption !== 'string' || !outputOption.trim()) {
      return null;
    }

    const normalized = outputOption.trim().toLowerCase();
    if (normalized.endsWith('.tar.gz') || normalized.endsWith('.tgz')) {
      return 'tar.gz';
    }
    if (
      normalized.endsWith('.tar.gz.enc') ||
      normalized.endsWith('.tgz.enc') ||
      normalized.endsWith('.enc')
    ) {
      return 'tar.gz';
    }
    if (normalized.endsWith('.zip')) {
      return 'zip';
    }

    return null;
  }

  normalizeBackupScope(scope, defaultScope = 'full') {
    const rawScope =
      typeof scope === 'string' && scope.trim() ? scope.trim().toLowerCase() : String(defaultScope).toLowerCase();

    if (rawScope === 'full') {
      return {
        raw: 'full',
        tokens: new Set(['full'])
      };
    }

    const parsedTokens = rawScope
      .split('+')
      .map((entry) => entry.trim())
      .filter(Boolean)
      .map((entry) => BACKUP_SCOPE_TOKEN_ALIASES[entry] || entry);

    if (parsedTokens.length === 0) {
      throw new Error(`Invalid backup scope '${scope}'. Use: ${BACKUP_SCOPE_EXAMPLE}.`);
    }

    for (const token of parsedTokens) {
      if (!BACKUP_SCOPE_ALLOWED_TOKENS.has(token)) {
        throw new Error(`Invalid backup scope token '${token}'. Use: ${BACKUP_SCOPE_EXAMPLE}.`);
      }
    }

    const uniqueTokens = [...new Set(parsedTokens)];
    return {
      raw: uniqueTokens.join('+'),
      tokens: new Set(uniqueTokens)
    };
  }

  scopeIncludesCategory(scopeInfo, category) {
    if (!scopeInfo || !scopeInfo.tokens) {
      return true;
    }
    if (scopeInfo.tokens.has('full')) {
      return true;
    }
    return scopeInfo.tokens.has(category);
  }

  categorizeTopLevelEntry(entryName) {
    const normalized = String(entryName || '').toLowerCase();
    if (!normalized) {
      return 'other';
    }

    if (BACKUP_CONFIG_ENTRIES.has(normalized)) {
      return 'config';
    }
    if (BACKUP_CREDENTIAL_ENTRIES.has(normalized)) {
      return 'creds';
    }
    if (BACKUP_SESSION_ENTRIES.has(normalized)) {
      return 'sessions';
    }
    if (BACKUP_WORKSPACE_ENTRIES.has(normalized)) {
      return 'workspace';
    }

    return 'other';
  }

  shouldIncludeTopLevelEntry(entryName, scopeInfo) {
    if (!scopeInfo || !scopeInfo.tokens || scopeInfo.tokens.has('full')) {
      return true;
    }

    const category = this.categorizeTopLevelEntry(entryName);
    return scopeInfo.tokens.has(category);
  }

  parseOlderThanToMs(value) {
    if (value == null) {
      return null;
    }

    const normalized = String(value).trim().toLowerCase();
    if (!normalized) {
      return null;
    }

    const match = normalized.match(/^(\d+)\s*(ms|s|m|h|d|w)$/i);
    if (!match) {
      throw new Error(`Invalid --older-than value '${value}'. Use examples like 12h, 7d, 30d.`);
    }

    const amount = Number(match[1]);
    const unit = match[2].toLowerCase();
    const unitMap = {
      ms: 1,
      s: 1000,
      m: 60 * 1000,
      h: 60 * 60 * 1000,
      d: 24 * 60 * 60 * 1000,
      w: 7 * 24 * 60 * 60 * 1000,
    };

    return amount * unitMap[unit];
  }

  isEncryptedArchiveName(fileName) {
    const normalized = String(fileName || '').toLowerCase();
    return (
      normalized.endsWith('.tar.gz.enc') ||
      normalized.endsWith('.tgz.enc') ||
      normalized.endsWith('.enc')
    );
  }

  // Returns true if the archive requires a password to extract.
  // For .enc files it's always true. For .zip files we peek at the entry flags.
  isArchiveEncrypted(archivePath) {
    try {
      const normalized = String(archivePath || '').toLowerCase();
      if (this.isEncryptedArchiveName(normalized)) return true;
      if (!normalized.endsWith('.zip')) return false;
      const zip = new AdmZip(archivePath);
      const entries = zip.getEntries();
      return entries.some((e) => e.header && (e.header.flags & 0x1) === 0x1);
    } catch (_) {
      return false;
    }
  }

  // Returns true if the zip uses WinZip AES-256 encryption (method 99).
  // AdmZip and macOS/Windows built-in unzip cannot handle this — only 7z can.
  isZipAes256Encrypted(archivePath) {
    try {
      const zip = new AdmZip(archivePath);
      const entries = zip.getEntries();
      return entries.some((e) => e.header && e.header.method === 99);
    } catch (_) {
      return false;
    }
  }

  // Try 7z/7za to extract a zip or any archive. Returns true on success.
  trySevenZipExtract(archivePath, destDir, password) {
    // Electron runs with a stripped PATH — include common absolute install locations
    const cmds = process.platform === 'win32'
      ? ['7z', 'C:\\Program Files\\7-Zip\\7z.exe', 'C:\\Program Files (x86)\\7-Zip\\7z.exe']
      : [
          '7za', '7z',
          '/opt/homebrew/bin/7za',  // Apple Silicon Homebrew
          '/usr/local/bin/7za',     // Intel Mac Homebrew
          '/opt/homebrew/bin/7z',
          '/usr/local/bin/7z',
          '/usr/bin/7z',
        ];
    let foundSevenZip = false;
    for (const cmd of cmds) {
      try {
        const args = password
          ? ['e', archivePath, `-o${destDir}`, `-p${password}`, '-y', '-aoa']
          : ['e', archivePath, `-o${destDir}`, '-y', '-aoa'];
        const result = spawnSync(cmd, args, { stdio: 'pipe', encoding: 'utf-8' });
        if (result.error && result.error.code === 'ENOENT') continue; // not found, try next
        foundSevenZip = true;
        if (result.status === 0) return { ok: true };
        const output = (result.stdout || '') + (result.stderr || '');
        if (output.includes('Wrong password') || result.status === 2) {
          return { ok: false, wrongPassword: true };
        }
        // non-zero exit for other reason — try next candidate
      } catch (_) {
        // command not found, try next
      }
    }
    return { ok: false, wrongPassword: false, notFound: !foundSevenZip };
  }

  inferArchiveTypeFromName(fileName) {
    const normalized = String(fileName || '').toLowerCase();
    if (normalized.endsWith('.zip')) {
      return 'zip';
    }
    if (normalized.endsWith('.tar.gz') || normalized.endsWith('.tgz')) {
      return 'tar.gz';
    }
    if (this.isEncryptedArchiveName(normalized)) {
      return 'tar.gz';
    }
    return 'unknown';
  }

  async resolveBackupOutputPath(backupName, outputOption) {
    const raw = typeof outputOption === 'string' && outputOption.trim() ? this.expandUserPath(outputOption.trim()) : null;
    if (!raw) {
      return path.join(this.dest, backupName);
    }

    const resolved = path.resolve(raw);
    if (raw.endsWith('/') || raw.endsWith('\\')) {
      return path.join(resolved, backupName);
    }

    try {
      const stat = await fs.stat(resolved);
      if (stat.isDirectory()) {
        return path.join(resolved, backupName);
      }
    } catch (e) {
      // Treat as file path when target does not exist.
    }

    return resolved;
  }

  async assertOutputPathReady(outputPath) {
    if (await fs.pathExists(outputPath)) {
      throw new Error(`Refusing to overwrite existing backup archive: ${outputPath}`);
    }
  }

  async canonicalizePathForContainment(targetPath) {
    const resolved = path.resolve(targetPath);
    const suffix = [];
    let probe = resolved;

    while (true) {
      try {
        const realProbe = await fs.realpath(probe);
        if (suffix.length === 0) {
          return realProbe;
        }
        return path.join(realProbe, ...suffix.slice().reverse());
      } catch (e) {
        const parent = path.dirname(probe);
        if (parent === probe) {
          return resolved;
        }
        suffix.push(path.basename(probe));
        probe = parent;
      }
    }
  }

  async assertOutputPathOutsideSources(outputPath, sourcePaths) {
    const canonicalOutputPath = await this.canonicalizePathForContainment(outputPath);
    for (const sourcePath of sourcePaths) {
      const canonicalSourcePath = await this.canonicalizePathForContainment(sourcePath);
      const withinSource =
        canonicalOutputPath === canonicalSourcePath ||
        canonicalOutputPath.startsWith(`${canonicalSourcePath}${path.sep}`);
      if (withinSource) {
        throw new Error(
          `Backup output must not be written inside a source path: ${outputPath} is inside ${sourcePath}`,
        );
      }
    }
  }

  async buildPayloadIndex(tempDir) {
    const payload = [];

    const scan = async (currentDir) => {
      const entries = await fs.readdir(currentDir);
      for (const entry of entries) {
        const fullPath = path.join(currentDir, entry);
        const stat = await fs.stat(fullPath);
        if (stat.isDirectory()) {
          await scan(fullPath);
          continue;
        }

        const archivePath = toPosixRelative(tempDir, fullPath);
        if (archivePath === 'manifest.json') {
          continue;
        }

        const data = await fs.readFile(fullPath);
        payload.push({
          archivePath,
          size: stat.size,
          sha256: hashBufferSha256(data)
        });
      }
    };

    await scan(tempDir);
    payload.sort((left, right) => left.archivePath.localeCompare(right.archivePath));
    return payload;
  }

  parseManifestPayloadEntries(rawPayloadEntries) {
    if (!Array.isArray(rawPayloadEntries)) {
      return [];
    }

    const payloadEntries = [];
    for (const entry of rawPayloadEntries) {
      if (!entry || typeof entry !== 'object') {
        throw new Error('Backup manifest payload entry must be an object.');
      }

      const normalizedArchivePath = normalizeArchivePath(
        entry.archivePath,
        'Backup manifest payload path',
      );

      if (typeof entry.sha256 !== 'string' || !/^[a-f0-9]{64}$/i.test(entry.sha256)) {
        throw new Error(`Backup manifest payload SHA-256 is invalid for: ${entry.archivePath}`);
      }

      const size = Number(entry.size);
      if (!Number.isFinite(size) || size < 0) {
        throw new Error(`Backup manifest payload size is invalid for: ${entry.archivePath}`);
      }

      payloadEntries.push({
        archivePath: normalizedArchivePath,
        size,
        sha256: String(entry.sha256).toLowerCase()
      });
    }

    return payloadEntries;
  }

  parseBackupManifest(rawManifest) {
    let parsed;
    try {
      parsed = JSON.parse(rawManifest);
    } catch (e) {
      throw new Error(`Backup manifest is not valid JSON: ${e.message}`);
    }

    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
      throw new Error('Backup manifest must be an object.');
    }

    if (Number(parsed.schemaVersion) === 1) {
      if (!Array.isArray(parsed.assets)) {
        throw new Error('Backup manifest schemaVersion=1 requires an assets array.');
      }

      const assets = parsed.assets.map((asset, index) => {
        if (!asset || typeof asset !== 'object') {
          throw new Error(`Backup manifest asset at index ${index} must be an object.`);
        }
        if (typeof asset.kind !== 'string' || !asset.kind.trim()) {
          throw new Error(`Backup manifest asset at index ${index} is missing kind.`);
        }
        if (typeof asset.sourcePath !== 'string' || !asset.sourcePath.trim()) {
          throw new Error(`Backup manifest asset at index ${index} is missing sourcePath.`);
        }
        if (typeof asset.archivePath !== 'string' || !asset.archivePath.trim()) {
          throw new Error(`Backup manifest asset at index ${index} is missing archivePath.`);
        }

        return {
          kind: asset.kind,
          sourcePath: asset.sourcePath,
          archivePath: normalizeArchivePath(asset.archivePath, 'Backup manifest asset path')
        };
      });

      return {
        schemaVersion: 1,
        createdAt: typeof parsed.createdAt === 'string' ? parsed.createdAt : null,
        timestamp: typeof parsed.timestamp === 'string' ? parsed.timestamp : null,
        assets,
        payload: this.parseManifestPayloadEntries(parsed.payload)
      };
    }

    if (Array.isArray(parsed.files)) {
      const legacyAssets = parsed.files
        .filter((entry) => typeof entry === 'string' && entry.trim())
        .map((entry) => {
          const normalized = normalizeArchivePath(entry.replace(/\\/g, '/'), 'Legacy manifest file path');
          return {
            kind: 'state',
            sourcePath: entry,
            archivePath: normalized
          };
        });

      if (legacyAssets.length === 0) {
        throw new Error('Legacy backup manifest has no files to verify.');
      }

      return {
        schemaVersion: 0,
        createdAt: null,
        timestamp: typeof parsed.timestamp === 'string' ? parsed.timestamp : null,
        assets: legacyAssets,
        payload: []
      };
    }

    throw new Error('Unsupported backup manifest format: expected schemaVersion=1 assets or legacy files array.');
  }

  normalizeArchiveFormat(formatValue) {
    const normalized = String(formatValue || 'zip').toLowerCase();
    if (normalized === 'zip') {
      return 'zip';
    }
    if (normalized === 'tar.gz' || normalized === 'targz' || normalized === 'tgz') {
      return 'tar.gz';
    }
    throw new Error(`Unsupported archive format '${formatValue}'. Use 'zip' or 'tar.gz'.`);
  }

  getArchiveType(archivePath) {
    const lowerPath = String(archivePath || '').toLowerCase();
    if (lowerPath.endsWith('.zip')) {
      return 'zip';
    }
    if (lowerPath.endsWith('.tar.gz') || lowerPath.endsWith('.tgz')) {
      return 'tar.gz';
    }
    if (lowerPath.endsWith('.tar.gz.enc') || lowerPath.endsWith('.tgz.enc') || lowerPath.endsWith('.enc')) {
      return 'encrypted';
    }
    throw new Error(`Unsupported archive type for file: ${archivePath}`);
  }

  normalizeArchiveEntry(rawEntryPath, label) {
    const candidate = normalizeRawArchivePath(rawEntryPath);
    if (!candidate) {
      return null;
    }
    return normalizeArchivePath(candidate, label);
  }

  deriveArchiveEncryptionKey(password, salt) {
    return crypto.pbkdf2Sync(
      String(password),
      salt,
      ARCHIVE_ENCRYPTION_PBKDF2_ROUNDS,
      32,
      'sha256',
    );
  }

  async appendFileToFile(sourcePath, targetPath) {
    await new Promise((resolve, reject) => {
      const source = fs.createReadStream(sourcePath);
      const target = fs.createWriteStream(targetPath, { flags: 'a' });

      source.on('error', reject);
      target.on('error', reject);
      target.on('close', resolve);

      source.pipe(target);
    });
  }

  async encryptFileWithPassword(inputPath, outputPath, password) {
    const salt = crypto.randomBytes(ARCHIVE_ENCRYPTION_SALT_BYTES);
    const iv = crypto.randomBytes(ARCHIVE_ENCRYPTION_IV_BYTES);
    const key = this.deriveArchiveEncryptionKey(password, salt);
    const cipher = crypto.createCipheriv('aes-256-gcm', key, iv);
    const tempCipherPath = `${outputPath}.cipher.tmp`;

    try {
      await new Promise((resolve, reject) => {
        const input = fs.createReadStream(inputPath);
        const encrypted = fs.createWriteStream(tempCipherPath);

        input.on('error', reject);
        encrypted.on('error', reject);
        encrypted.on('close', resolve);
        cipher.on('error', reject);

        input.pipe(cipher).pipe(encrypted);
      });

      const authTag = cipher.getAuthTag();
      const header = Buffer.concat([ARCHIVE_ENCRYPTION_MAGIC, salt, iv]);
      await fs.writeFile(outputPath, header);
      await this.appendFileToFile(tempCipherPath, outputPath);
      await fs.appendFile(outputPath, authTag);
    } finally {
      await fs.remove(tempCipherPath).catch(() => undefined);
    }
  }

  async decryptFileWithPassword(inputPath, outputPath, password) {
    const stats = await fs.stat(inputPath);
    const headerBytes =
      ARCHIVE_ENCRYPTION_MAGIC.length + ARCHIVE_ENCRYPTION_SALT_BYTES + ARCHIVE_ENCRYPTION_IV_BYTES;
    const minimumSize = headerBytes + ARCHIVE_ENCRYPTION_TAG_BYTES + 1;
    if (stats.size < minimumSize) {
      throw new Error('Encrypted archive is too small or corrupted.');
    }

    const descriptor = await fs.open(inputPath, 'r');
    try {
      const header = Buffer.alloc(headerBytes);
      await fs.read(descriptor, header, 0, header.length, 0);

      const magic = header.subarray(0, ARCHIVE_ENCRYPTION_MAGIC.length);
      if (!magic.equals(ARCHIVE_ENCRYPTION_MAGIC)) {
        throw new Error('Encrypted archive header is invalid.');
      }

      const saltStart = ARCHIVE_ENCRYPTION_MAGIC.length;
      const saltEnd = saltStart + ARCHIVE_ENCRYPTION_SALT_BYTES;
      const ivEnd = saltEnd + ARCHIVE_ENCRYPTION_IV_BYTES;
      const salt = header.subarray(saltStart, saltEnd);
      const iv = header.subarray(saltEnd, ivEnd);

      const authTag = Buffer.alloc(ARCHIVE_ENCRYPTION_TAG_BYTES);
      await fs.read(descriptor, authTag, 0, authTag.length, stats.size - ARCHIVE_ENCRYPTION_TAG_BYTES);

      const key = this.deriveArchiveEncryptionKey(password, salt);
      const decipher = crypto.createDecipheriv('aes-256-gcm', key, iv);
      decipher.setAuthTag(authTag);

      const encryptedStart = headerBytes;
      const encryptedEnd = stats.size - ARCHIVE_ENCRYPTION_TAG_BYTES - 1;

      await new Promise((resolve, reject) => {
        const encryptedInput = fs.createReadStream(inputPath, {
          start: encryptedStart,
          end: encryptedEnd
        });
        const decryptedOutput = fs.createWriteStream(outputPath);

        encryptedInput.on('error', reject);
        decryptedOutput.on('error', reject);
        decipher.on('error', reject);
        decryptedOutput.on('close', resolve);

        encryptedInput.pipe(decipher).pipe(decryptedOutput);
      });
    } catch (error) {
      throw new Error(`Could not decrypt archive. Check password and archive integrity. (${error.message})`);
    } finally {
      await fs.close(descriptor).catch(() => undefined);
    }
  }

  async prepareArchiveForRead(absArchivePath) {
    const archiveType = this.getArchiveType(absArchivePath);
    if (archiveType !== 'encrypted') {
      return {
        archivePath: absArchivePath,
        encrypted: false,
        cleanup: async () => undefined
      };
    }

    if (!this.password) {
      throw new Error('Archive is encrypted. Supply --password (or RECLAW_PASSWORD) to continue.');
    }

    const plainArchiveName = path.basename(absArchivePath).replace(/\.enc$/i, '');
    const tempPlainArchivePath = path.join(
      os.tmpdir(),
      `reclaw_decrypted_${Date.now()}_${Math.random().toString(36).slice(2)}_${plainArchiveName}`,
    );

    await this.decryptFileWithPassword(absArchivePath, tempPlainArchivePath, this.password);

    return {
      archivePath: tempPlainArchivePath,
      encrypted: true,
      cleanup: async () => {
        await fs.remove(tempPlainArchivePath).catch(() => undefined);
      }
    };
  }

  async inspectZipArchive(absArchivePath) {
    const zip = new AdmZip(absArchivePath);
    const rawEntries = zip.getEntries().map((entry) => entry.entryName);
    if (rawEntries.length === 0) {
      throw new Error('Backup archive is empty.');
    }

    const normalizedEntries = [];
    const fileMetadata = new Map();
    let manifestRaw = null;

    for (const zipEntry of zip.getEntries()) {
      const normalizedPath = this.normalizeArchiveEntry(zipEntry.entryName, 'Archive entry');
      if (!normalizedPath) {
        continue;
      }
      if (fileMetadata.has(normalizedPath) || normalizedEntries.includes(normalizedPath)) {
        throw new Error(`Backup archive contains duplicate entry path: ${normalizedPath}`);
      }

      normalizedEntries.push(normalizedPath);

      if (zipEntry.isDirectory) {
        fileMetadata.set(normalizedPath, {
          isDirectory: true,
          size: 0,
          sha256: null
        });
        continue;
      }

      const encryptedEntry = zipEntry.header && (zipEntry.header.flags & 0x1) === 0x1;
      if (encryptedEntry && !this.password) {
        throw new Error(`Archive entry is encrypted but no password was provided: ${normalizedPath}`);
      }

      const data = this.password ? zipEntry.getData(this.password) : zipEntry.getData();
      const metadata = {
        isDirectory: false,
        size: data.length,
        sha256: hashBufferSha256(data)
      };
      fileMetadata.set(normalizedPath, metadata);

      if (normalizedPath === 'manifest.json') {
        manifestRaw = data.toString('utf8');
      }
    }

    return {
      normalizedEntries,
      fileMetadata,
      manifestRaw
    };
  }

  async inspectTarArchive(absArchivePath) {
    const normalizedEntries = [];
    const fileMetadata = new Map();
    let manifestRaw = null;
    let pendingError = null;

    await tar.t({
      file: absArchivePath,
      gzip: true,
      onentry: (entry) => {
        if (pendingError) {
          entry.resume();
          return;
        }

        let normalizedPath;
        try {
          normalizedPath = this.normalizeArchiveEntry(entry.path, 'Archive entry');
        } catch (error) {
          pendingError = error;
          entry.resume();
          return;
        }

        if (!normalizedPath) {
          entry.resume();
          return;
        }

        if (fileMetadata.has(normalizedPath) || normalizedEntries.includes(normalizedPath)) {
          pendingError = new Error(`Backup archive contains duplicate entry path: ${normalizedPath}`);
          entry.resume();
          return;
        }

        normalizedEntries.push(normalizedPath);

        const isDirectory = entry.type === 'Directory';
        if (isDirectory) {
          fileMetadata.set(normalizedPath, {
            isDirectory: true,
            size: 0,
            sha256: null
          });
          entry.resume();
          return;
        }

        const hash = crypto.createHash('sha256');
        let size = 0;
        const manifestChunks = normalizedPath === 'manifest.json' ? [] : null;

        entry.on('data', (chunk) => {
          size += chunk.length;
          hash.update(chunk);
          if (manifestChunks) {
            manifestChunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk));
          }
        });

        entry.on('end', () => {
          fileMetadata.set(normalizedPath, {
            isDirectory: false,
            size,
            sha256: hash.digest('hex')
          });

          if (manifestChunks) {
            manifestRaw = Buffer.concat(manifestChunks).toString('utf8');
          }
        });
      }
    });

    if (pendingError) {
      throw pendingError;
    }

    if (normalizedEntries.length === 0) {
      throw new Error('Backup archive is empty.');
    }

    return {
      normalizedEntries,
      fileMetadata,
      manifestRaw
    };
  }

  async createTarGzDirectory(sourceDir, outPath) {
    await tar.c(
      {
        file: outPath,
        cwd: sourceDir,
        gzip: true,
        portable: true
      },
      ['.'],
    );
  }

  formatVerifySummary(result) {
    return [
      `Backup archive OK: ${result.archivePath}`,
      `Manifest schema: ${result.schemaVersion}`,
      `Created at: ${result.createdAt || 'unknown'}`,
      `Assets verified: ${result.assetCount}`,
      `Payload entries verified: ${result.payloadEntryCount}`,
      `Archive entries scanned: ${result.entryCount}`
    ].join('\n');
  }

  async verifySnapshot(archivePath, options = {}) {
    let resolvedArchivePath = archivePath;
    if (!resolvedArchivePath) {
      const backups = await this.getBackups();
      if (backups.length === 0) {
        throw new Error('No backups found in destination directory.');
      }
      resolvedArchivePath = path.join(this.dest, backups[0]);
    }

    const absArchivePath = path.resolve(this.expandUserPath(resolvedArchivePath));
    if (!(await fs.pathExists(absArchivePath))) {
      throw new Error(`Backup archive not found: ${absArchivePath}`);
    }

    const prepared = await this.prepareArchiveForRead(absArchivePath);
    try {
      const archiveType = this.getArchiveType(prepared.archivePath);
      const inspection = archiveType === 'zip'
        ? await this.inspectZipArchive(prepared.archivePath)
        : await this.inspectTarArchive(prepared.archivePath);

      const manifestEntryMatches = inspection.normalizedEntries.filter((entryPath) => entryPath === 'manifest.json');
      if (manifestEntryMatches.length !== 1) {
        throw new Error(`Expected exactly one root manifest.json entry, found ${manifestEntryMatches.length}.`);
      }

      if (!inspection.manifestRaw) {
        throw new Error('Backup archive manifest entry could not be resolved.');
      }

      const manifest = this.parseBackupManifest(inspection.manifestRaw);

      for (const asset of manifest.assets) {
        const exact = inspection.fileMetadata.has(asset.archivePath);
        const nested = inspection.normalizedEntries.some(
          (entryPath) => entryPath !== asset.archivePath && isPathWithinArchive(entryPath, asset.archivePath),
        );
        if (!exact && !nested) {
          throw new Error(`Backup archive is missing payload for manifest asset: ${asset.archivePath}`);
        }
      }

      for (const payloadEntry of manifest.payload) {
        const metadata = inspection.fileMetadata.get(payloadEntry.archivePath);
        if (!metadata) {
          throw new Error(`Backup archive is missing manifest payload entry: ${payloadEntry.archivePath}`);
        }
        if (metadata.isDirectory) {
          throw new Error(`Backup manifest payload points to a directory: ${payloadEntry.archivePath}`);
        }

        if (metadata.size !== payloadEntry.size) {
          throw new Error(
            `Backup manifest payload size mismatch for ${payloadEntry.archivePath}: expected ${payloadEntry.size}, got ${metadata.size}`,
          );
        }

        if (String(metadata.sha256 || '').toLowerCase() !== payloadEntry.sha256) {
          throw new Error(
            `Backup manifest payload hash mismatch for ${payloadEntry.archivePath}: expected ${payloadEntry.sha256}, got ${metadata.sha256}`,
          );
        }
      }

      const result = {
        ok: true,
        archivePath: absArchivePath,
        archiveType,
        schemaVersion: manifest.schemaVersion,
        createdAt: manifest.createdAt || manifest.timestamp || null,
        assetCount: manifest.assets.length,
        payloadEntryCount: manifest.payload.length,
        entryCount: inspection.normalizedEntries.length
      };

      if (!options.silent) {
        console.log(this.formatVerifySummary(result));
      }

      return result;
    } finally {
      await prepared.cleanup();
    }
  }

  async createBackup(options = {}) {
    const nowDate = options.nowMs ? new Date(options.nowMs) : new Date();
    const createdAt = nowDate.toISOString();
    const timestamp = createdAt.replace(/[:.]/g, '-');
    const archiveRoot = `openclaw_backup_${timestamp}`;
    const inferredFormat = this.inferArchiveFormatFromOutput(options.output);
    const archiveFormat = this.normalizeArchiveFormat(options.format || inferredFormat || 'zip');
    const encryptedTar = archiveFormat === 'tar.gz' && Boolean(this.password);
    const backupName = `${archiveRoot}.${archiveFormat === 'zip' ? 'zip' : encryptedTar ? 'tar.gz.enc' : 'tar.gz'}`;
    const tempDir = path.join(os.tmpdir(), `claw_backup_${timestamp}`);
    const finalPath = await this.resolveBackupOutputPath(backupName, options.output);

    const dryRun = options.dryRun === true || this.dryRun === true;
    const verify = options.verify === true;
    const silent = options.silent === true;
    const onlyConfig = options.onlyConfig === true;
    const requestedScopeInfo = this.normalizeBackupScope(options.scope, onlyConfig ? 'config' : 'full');
    const effectiveScopeInfo = onlyConfig ? this.normalizeBackupScope('config', 'config') : requestedScopeInfo;
    const includeWorkspace =
      onlyConfig
        ? false
        : this.scopeIncludesCategory(effectiveScopeInfo, 'workspace') && options.includeWorkspace !== false;
    const includeSessions = onlyConfig ? false : this.scopeIncludesCategory(effectiveScopeInfo, 'sessions');
    const includeExternalPlugins =
      onlyConfig
        ? false
        : this.scopeIncludesCategory(effectiveScopeInfo, 'config') || this.scopeIncludesCategory(effectiveScopeInfo, 'creds');
    const includeBrowser = options.includeBrowser === true || this.includeBrowser;
    const log = (...args) => {
      if (!silent) {
        console.log(...args);
      }
    };

    const manifest = {
      schemaVersion: 1,
      timestamp,
      createdAt,
      archiveRoot,
      version: '2.0.0',
      options: {
        archiveFormat,
        scope: effectiveScopeInfo.raw,
        includeWorkspace,
        onlyConfig,
        includeBrowser,
        encrypted: Boolean(this.password)
      },
      files: [],
      assets: [],
      skipped: [],
      payload: [],
      mapping: {}
    };

    const sourcePathsForSafety = [];

    await fs.ensureDir(tempDir);

    try {
      log(`[1/4] Scanning for data in: ${this.home}`);

      if (onlyConfig) {
        const configPath = path.join(this.home, 'openclaw.json');
        if (!(await fs.pathExists(configPath))) {
          throw new Error('No OpenClaw config file was found to back up.');
        }

        if (!dryRun) {
          await fs.copy(configPath, path.join(tempDir, 'openclaw.json'));
        }

        manifest.files.push('openclaw.json');
        manifest.assets.push({
          kind: 'config',
          sourcePath: configPath,
          archivePath: 'openclaw.json'
        });
        sourcePathsForSafety.push(configPath);
      } else {
        if (includeWorkspace) {
          const configPath = path.join(this.home, 'openclaw.json');
          if (await fs.pathExists(configPath)) {
            try {
              await fs.readJson(configPath);
            } catch (error) {
              throw new Error(
                `Config invalid at ${configPath}. ReClaw cannot reliably discover custom workspaces for backup. Fix the config or rerun with --no-include-workspace for a partial backup.`,
              );
            }
          }
        }

        if (includeSessions) {
          const snappedDatabases = await this.snapshotDatabases(tempDir, { dryRun, silent });
          for (const dbAsset of snappedDatabases) {
            manifest.assets.push({
              kind: 'database',
              sourcePath: dbAsset.sourcePath,
              archivePath: dbAsset.archivePath
            });
            sourcePathsForSafety.push(dbAsset.sourcePath);
          }
        }

        const defaultExclusions = ['node_modules', '.git', 'temp_repo', 'dist', 'coverage', 'backups'];
        const configuredExclusions =
          Array.isArray(config.excludeDirs) && config.excludeDirs.length > 0
            ? [...config.excludeDirs]
            : defaultExclusions;

        const exclusions = new Set(configuredExclusions);
        if (includeBrowser) {
          exclusions.delete('browser');
          log('   🌐 Including browser directory in backup payload.');
        } else {
          exclusions.add('browser');
        }

        if (!includeWorkspace) {
          exclusions.add('workspace');
          exclusions.add('workspaces');
        }

        const items = await fs.readdir(this.home);
        for (const item of items) {
          if (item === 'databases') {
            continue;
          }

          const src = path.join(this.home, item);
          const srcStat = await fs.stat(src).catch(() => null);
          if (!srcStat) {
            continue;
          }

          if (!this.shouldIncludeTopLevelEntry(item, effectiveScopeInfo)) {
            manifest.skipped.push({
              kind: 'state',
              sourcePath: src,
              archivePath: item,
              reason: 'scope-filtered'
            });
            continue;
          }

          if (exclusions.has(item)) {
            manifest.skipped.push({
              kind: 'state',
              sourcePath: src,
              archivePath: item,
              reason: 'excluded'
            });
            continue;
          }

          const dest = path.join(tempDir, item);
          log(`   📦 Backing up: ${item}...`);
          if (!dryRun) {
            await fs.copy(src, dest, {
              filter: (srcPath) => {
                const basename = path.basename(srcPath);
                return !exclusions.has(basename);
              }
            });
          }

          manifest.files.push(item);
          manifest.assets.push({
            kind: srcStat.isDirectory() ? 'state-directory' : 'state-file',
            sourcePath: src,
            archivePath: item
          });
          sourcePathsForSafety.push(src);
        }

        if (includeExternalPlugins) {
          const externalPlugins = await this.bundleExternalPlugins(tempDir, manifest, { dryRun, silent });
          for (const pluginAsset of externalPlugins) {
            manifest.assets.push({
              kind: 'external-plugin',
              sourcePath: pluginAsset.sourcePath,
              archivePath: pluginAsset.archivePath
            });
            sourcePathsForSafety.push(pluginAsset.sourcePath);
          }
        }
      }

      await this.assertOutputPathOutsideSources(finalPath, sourcePathsForSafety);

      const result = {
        ok: true,
        archivePath: finalPath,
        createdAt,
        archiveRoot,
        archiveFormat,
        encrypted: Boolean(this.password),
        dryRun,
        verified: false,
        includeWorkspace,
        onlyConfig,
        includeBrowser,
        scope: effectiveScopeInfo.raw,
        assets: manifest.assets,
        skipped: manifest.skipped
      };

      if (dryRun) {
        log('[3/4] Dry run complete. Archive not written.');
        return result;
      }

      await this.assertOutputPathReady(finalPath);
      await fs.ensureDir(path.dirname(finalPath));

      manifest.payload = await this.buildPayloadIndex(tempDir);
      await fs.writeJson(path.join(tempDir, 'manifest.json'), manifest, { spaces: 2 });

      log(`[3/4] Compressing to ${path.basename(finalPath)}...`);
      if (this.password) {
        if (archiveFormat === 'zip') {
          log('   🔒 Encryption enabled (ZipCrypto)');
        } else {
          log('   🔒 Encryption enabled (AES-256-GCM)');
        }
      }

      if (archiveFormat === 'zip') {
        await this.zipDirectory(tempDir, finalPath);
      } else {
        const tempTarPath = encryptedTar ? `${finalPath}.tmp.tar.gz` : finalPath;
        await this.createTarGzDirectory(tempDir, tempTarPath);
        if (encryptedTar) {
          await this.encryptFileWithPassword(tempTarPath, finalPath, this.password);
          await fs.remove(tempTarPath).catch(() => undefined);
        }
      }

      const stats = await fs.stat(finalPath);
      log(`[4/4] Backup complete! Size: ${(stats.size / 1024 / 1024).toFixed(2)} MB`);

      if (verify) {
        await this.verifySnapshot(finalPath, { silent: true });
        result.verified = true;
      }

      return result;
    } finally {
      await fs.remove(tempDir);
    }
  }

  async snapshotDatabases(tempDir, options = {}) {
    const dryRun = options.dryRun === true;
    const silent = options.silent === true;
    const snapshotAssets = [];
    const dbDir = path.join(tempDir, 'databases');
    if (!dryRun) {
      await fs.ensureDir(dbDir);
    }
    
    // Simple recursive search for .db/.sqlite files, ignoring node_modules
    const findDbs = async (dir) => {
      let results = [];
      try {
        const list = await fs.readdir(dir);
        for (let file of list) {
          if (['node_modules', '.git', 'temp_repo'].includes(file)) continue;
          
          let full = path.join(dir, file);
          let stat = await fs.stat(full);
          
          if (stat.isDirectory()) {
            results = results.concat(await findDbs(full));
          } else if (/\.(db|sqlite|sqlite3)$/.test(file)) {
            results.push(full);
          }
        }
      } catch (e) {
        // Ignore permission errors etc.
      }
      return results;
    };

    const dbs = await findDbs(this.home);
    if (dbs.length > 0) {
      if (!silent) {
        console.log(`   💾 Found ${dbs.length} databases. Snapshotting...`);
      }
      for (let dbPath of dbs) {
        const rel = path.relative(this.home, dbPath);
        const archivePath = path.posix.join('databases', rel.split(path.sep).join('/'));
        snapshotAssets.push({
          sourcePath: dbPath,
          archivePath
        });

        if (dryRun) {
          continue;
        }

        const dest = path.join(dbDir, rel);
        await fs.ensureDir(path.dirname(dest));
        try {
          // Attempt sqlite3 .backup command for consistency
          execSync(`sqlite3 "${dbPath}" ".backup '${dest}'"`, { stdio: 'ignore' });
        } catch (e) {
          // Fallback to file copy if sqlite3 CLI fails or isn't installed
          await fs.copy(dbPath, dest);
        }
      }
    }

    return snapshotAssets;
  }

  getSafeName(fullPath) {
    // Generate a unique safe name for external plugin folders
    const normalized = fullPath.replace(/\\/g, '/').toLowerCase();
    const hash = crypto.createHash('md5').update(normalized).digest('hex').substring(0, 6);
    const base = path.basename(normalized);
    if (base.endsWith(`_${hash}`)) {
      return base;
    }
    return `${base}_${hash}`;
  }

  async bundleExternalPlugins(tempDir, manifest, options = {}) {
    const dryRun = options.dryRun === true;
    const silent = options.silent === true;
    // Logic to find plugins referenced in openclaw.json that live outside the home dir
    const configPath = path.join(this.home, 'openclaw.json');
    if (!(await fs.pathExists(configPath))) return [];

    try {
      const configData = await fs.readJson(configPath);
      const pluginDir = path.join(tempDir, 'external_plugins');
      const bundledAssets = [];
      
      const findPaths = (obj) => {
        let paths = [];
        if (!obj) return paths;
        
        const traverse = (item) => {
          if (typeof item === 'string') {
            // Heuristic to detect absolute paths or paths starting with ~ or / or drive letters
            if ((/^[A-Z]:\\/i.test(item) || item.startsWith('/') || item.startsWith('~')) && !item.includes('node_modules')) {
               paths.push(item);
            }
          } else if (typeof item === 'object') {
            Object.values(item).forEach(traverse);
          }
        };
        traverse(obj);
        return paths;
      };

      const externalPaths = [...new Set(findPaths(configData.plugins || {}))];
      
      for (let p of externalPaths) {
        // Resolve ~ to home dir
        let resolvedP = p.replace(/^~/, os.homedir());
        const resolvedAbs = path.resolve(resolvedP);
        const homeAbs = path.resolve(this.home);

        // Skip plugin paths that are already inside OpenClaw home; they are not external.
        if (resolvedAbs === homeAbs || resolvedAbs.startsWith(`${homeAbs}${path.sep}`)) {
          continue;
        }

        if (await fs.pathExists(resolvedP)) {
          const safeName = this.getSafeName(p);
          if (!silent) {
            console.log(`   🔌 Bundling external plugin: ${path.basename(resolvedP)}`);
          }
          if (!dryRun) {
            await fs.copy(resolvedP, path.join(pluginDir, safeName), { 
              filter: (src) => !src.includes('node_modules') 
            });
          }
          manifest.mapping[p] = safeName;
          bundledAssets.push({
            sourcePath: resolvedAbs,
            archivePath: path.posix.join('external_plugins', safeName)
          });
        }
      }

      return bundledAssets;
    } catch (e) {
      if (!silent) {
        console.warn('Warning: Could not bundle external plugins:', e.message);
      }
      return [];
    }
  }

  async restore(archivePath, options = {}) {
    const safeReset = options.safeReset === true;
    const resetScope = this.normalizeResetScope(options.resetScope);
    const verifyBeforeRestore = options.verify === true;
    const restoreScopeInfo = this.normalizeBackupScope(options.scope, 'full');

    if (!archivePath) {
      const backups = await this.getBackups();
      if (backups.length === 0) throw new Error('No backups found in destination directory.');
      archivePath = path.join(this.dest, backups[0]);
    }

    // Resolve absolute path to avoid cwd issues with system tools
    const absArchivePath = path.resolve(this.expandUserPath(archivePath));

    // Check if archive is encrypted and require password only if so
    const archiveIsEncrypted = this.isArchiveEncrypted(absArchivePath);
    if (archiveIsEncrypted && !this.password) {
      throw new Error('This backup is encrypted. Supply --password to restore it.');
    }
    if (!archiveIsEncrypted && this.password) {
      // Password provided but archive isn't encrypted — ignore it silently
      this.password = null;
    }

    if (verifyBeforeRestore) {
      await this.verifySnapshot(absArchivePath, { silent: true });
      console.log('   ✅ Archive verification passed before restore.');
    }

    await this.prepareRestoreRuntime();
    if (safeReset) {
      await this.runSafeResetBeforeRestore(resetScope);
    }
    await this.resolveGitLfsPointerIfNeeded(absArchivePath);

    console.log(`[1/5] Extracting: ${path.basename(absArchivePath)}`);
    const tempDir = path.join(os.tmpdir(), 'claw_restore_temp');
    await fs.emptyDir(tempDir);

    const manifestPath = path.join(tempDir, 'manifest.json');

    const preparedArchive = await this.prepareArchiveForRead(absArchivePath);
    try {
      const effectiveArchivePath = preparedArchive.archivePath;
      const archiveType = this.getArchiveType(effectiveArchivePath);

      if (archiveType === 'zip') {
        // Try AdmZip first (handles ZipCrypto, cross-platform, no dependencies)
        let admZipOk = false;
        try {
          if (this.password) {
            console.log('   🔒 Using ZipCrypto decryption...');
            const zip = new AdmZip(effectiveArchivePath);
            zip.extractAllTo(tempDir, true, false, this.password);
          } else {
            const zip = new AdmZip(effectiveArchivePath);
            zip.extractAllTo(tempDir, true);
          }
          if (!(await fs.pathExists(manifestPath))) {
            throw new Error('Extraction completed but manifest.json was not found.');
          }
          console.log('   ✅ Extraction successful (ZipCrypto)!');
          admZipOk = true;
        } catch (_) {
          // AdmZip failed — may be AES-256 or a large/corrupt archive
        }

        if (!admZipOk) {
          // If zip is encrypted and AdmZip failed, it's likely AES-256 (not ZipCrypto).
          // System unzip also can't handle AES-256, so go straight to 7z for encrypted archives.
          // For unencrypted archives that AdmZip failed, try system unzip first (Unix only).
          const zipIsEncrypted = Boolean(this.password) && this.isArchiveEncrypted(effectiveArchivePath);

          let sysUnzipOk = false;
          if (!zipIsEncrypted && process.platform !== 'win32') {
            console.log('   ⚠️  JS extraction failed, falling back to system unzip...');
            try {
              const stdioLevel = process.env.DEBUG ? 'inherit' : 'ignore';
              execSync(`unzip -o "${effectiveArchivePath}" -d "${tempDir}"`, { stdio: stdioLevel });
              if (await fs.pathExists(manifestPath)) sysUnzipOk = true;
            } catch (_) { /* fall through to 7z */ }
          }

          if (!sysUnzipOk) {
            if (zipIsEncrypted) {
              console.log('   🔒 AdmZip failed on encrypted zip (likely AES-256). Trying 7z...');
            } else {
              console.log('   ⚠️  system unzip failed, trying 7z...');
            }
            const r = this.trySevenZipExtract(effectiveArchivePath, tempDir, this.password);
            if (r.wrongPassword) {
              throw new Error('Restoration failed. Incorrect password for this backup archive.');
            }
            if (!r.ok || !(await fs.pathExists(manifestPath))) {
              if (r.notFound) {
                const hint = process.platform === 'win32'
                  ? 'Install 7-Zip from https://7-zip.org and ensure it is in your PATH.'
                  : 'Install 7-Zip: brew install p7zip';
                throw new Error(`Restoration failed. This backup uses AES-256 zip encryption. ${hint}`);
              }
              throw new Error('Restoration failed. Could not extract zip archive. Check your password and ensure the file is not a Git LFS pointer.');
            }
            console.log('   ✅ Extraction successful (7z)!');
          }
        }
      } else if (archiveType === 'tar.gz') {
        try {
          await tar.x({
            file: effectiveArchivePath,
            cwd: tempDir,
            gzip: true,
            strict: true
          });
          if (!(await fs.pathExists(manifestPath))) {
            throw new Error('Extraction completed but manifest.json was not found.');
          }
          console.log('   ✅ Extraction successful (tar.gz)!');
        } catch (error) {
          throw new Error(`Restoration failed. Could not extract tar.gz archive: ${error.message}`);
        }
      } else {
        throw new Error(`Unsupported archive type for restore: ${path.basename(effectiveArchivePath)}`);
      }
    } finally {
      await preparedArchive.cleanup();
    }

    const manifest = await fs.readJson(manifestPath);

    const topLevelEntries = (await fs.readdir(tempDir)).filter(
      (entry) => !['manifest.json', 'external_plugins'].includes(entry),
    );
    const selectedTopLevelEntries = topLevelEntries.filter((entry) => this.shouldIncludeTopLevelEntry(entry, restoreScopeInfo));
    const payloadEntries = Array.isArray(manifest.payload) ? manifest.payload : [];
    const selectedPayloadEntries = payloadEntries.filter((entry) => {
      if (!entry || typeof entry.archivePath !== 'string') return false;
      const root = entry.archivePath.split('/')[0];
      return this.shouldIncludeTopLevelEntry(root, restoreScopeInfo);
    });

    if (this.dryRun) {
      const plannedCount = selectedPayloadEntries.length > 0 ? selectedPayloadEntries.length : selectedTopLevelEntries.length;
      console.log(
        `[Dry Run] Would restore ${plannedCount} entries from backup ${manifest.timestamp || 'unknown'} (scope: ${restoreScopeInfo.raw})`,
      );
      await fs.remove(tempDir);
      return;
    }

    // Actual Restoration Logic
    const shouldRestoreWorkspace =
      this.scopeIncludesCategory(restoreScopeInfo, 'workspace') &&
      ((await fs.pathExists(path.join(tempDir, 'workspace'))) ||
        (await fs.pathExists(path.join(tempDir, 'workspaces'))));

    if (shouldRestoreWorkspace) {
      if (!process.env.DEBUG && !this._printedPrepareMsg) {
          console.log('[2/5] Preparing workspace... (details hidden for privacy)');
          this._printedPrepareMsg = true;
      } else if (process.env.DEBUG) {
          console.log('[2/5] Preparing workspace...');
      }
      
      // If node_modules is missing in the target, try to run install (if package.json exists!)
      if (!(await fs.pathExists(path.join(this.home, 'node_modules')))) {
          if (await fs.pathExists(path.join(this.home, 'package.json'))) {
              if (process.env.DEBUG) console.log('   📦 node_modules missing. Running npm install...');
              try {
                  const stdioLevel = process.env.DEBUG ? 'inherit' : 'ignore';
                  execSync('npm install', { cwd: this.home, stdio: stdioLevel });
              } catch (e) {
                  if (process.env.DEBUG) console.warn('   ⚠️ npm install failed. You may need to run it manually.');
              }
          }
      }
    }

    console.log('[3/5] Restoring files...');
    // Copy selected top-level entries back (scope-aware)
    for (const entry of selectedTopLevelEntries) {
      if (entry === 'databases') {
        continue;
      }
      const sourcePath = path.join(tempDir, entry);
      const targetPath = path.join(this.home, entry);
      await fs.copy(sourcePath, targetPath, { overwrite: true });
    }
    
    // Restore databases specifically
    if (
      this.scopeIncludesCategory(restoreScopeInfo, 'sessions') &&
      (await fs.pathExists(path.join(tempDir, 'databases')))
    ) {
      await fs.copy(path.join(tempDir, 'databases'), this.home, { overwrite: true });
    }

    // Restore plugins
    const extPluginSrc = path.join(tempDir, 'external_plugins');
    const shouldRestorePlugins =
      this.scopeIncludesCategory(restoreScopeInfo, 'config') || this.scopeIncludesCategory(restoreScopeInfo, 'creds');
    if (shouldRestorePlugins && (await fs.pathExists(extPluginSrc))) {
      console.log('   🔌 Restoring bundled plugins...');
      const target = path.join(this.home, 'plugins');
      await fs.ensureDir(target);
      await fs.copy(extPluginSrc, target, { overwrite: true });
    }

    // Fix paths in config files
    if (shouldRestorePlugins) {
      await this.deepLocalizePaths(this.home, manifest.mapping);
      await this.normalizePluginPathsForRuntime();
    }

    // Platform specific fixes
    if (process.platform !== 'win32') {
      try { 
        execSync(`chmod -R 755 "${this.home}"`, { stdio: 'ignore' }); 
      } catch(e) {}
    }

    await this.postRestoreRuntimeCheck();
    await this.ensureDashboardTokenAndUrl();

    console.log('[5/5] Cleanup complete.');
    await fs.remove(tempDir);
  }

  async ensureDashboardTokenAndUrl() {
    const helperPath = path.resolve(__dirname, '..', 'scripts', 'ensure-gateway-token.js');
    if (!(await fs.pathExists(helperPath))) return;

    const result = spawnSync(process.execPath, [helperPath, '--home', this.home], {
      encoding: 'utf-8',
      timeout: 15000,
      stdio: process.env.DEBUG ? 'inherit' : 'pipe'
    });

    if (process.env.DEBUG && result.status !== 0) {
      const output = `${result.stdout || ''}\n${result.stderr || ''}`.trim();
      if (output) {
        console.warn('   ⚠️ Could not auto-print dashboard token URL:');
        console.warn(output);
      }
    }
  }

  getDockerHomePath() {
    const dockerHome = process.env.OPENCLAW_DOCKER_HOME;
    if (!dockerHome) return null;
    return dockerHome.replace(/\\/g, '/').replace(/\/+$/, '');
  }

  toRuntimePluginPath(pluginDirName) {
    const dockerHome = this.getDockerHomePath();
    if (dockerHome) {
      return `${dockerHome}/plugins/${pluginDirName}`;
    }
    return path.join(this.home, 'plugins', pluginDirName).replace(/\\/g, '/');
  }

  async normalizePluginPathsForRuntime() {
    const configPath = path.join(this.home, 'openclaw.json');
    if (!(await fs.pathExists(configPath))) return;

    let configData;
    try {
      configData = await fs.readJson(configPath);
    } catch (e) {
      return;
    }

    const load = configData?.plugins?.load;
    if (!load || !Array.isArray(load.paths) || load.paths.length === 0) return;

    const pluginRoot = path.join(this.home, 'plugins');
    const normalizedPaths = [];
    let modified = false;

    for (const originalPath of load.paths) {
      if (typeof originalPath !== 'string' || !originalPath.trim()) {
        normalizedPaths.push(originalPath);
        continue;
      }

      const basename = path.basename(originalPath.replace(/\\/g, '/'));
      const localPluginPath = path.join(pluginRoot, basename);
      if (!(await fs.pathExists(localPluginPath))) {
        normalizedPaths.push(originalPath);
        continue;
      }

      const runtimePath = this.toRuntimePluginPath(basename);
      normalizedPaths.push(runtimePath);
      if (runtimePath !== originalPath) {
        modified = true;
      }
    }

    if (!modified) return;

    configData.plugins.load.paths = normalizedPaths;
    await fs.writeJson(configPath, configData, { spaces: 2 });

    if (process.env.OPENCLAW_DOCKER_HOME) {
      console.log('   🐳 Normalized plugin paths for Docker runtime.');
    }
  }

  runCliCommand(command, args, timeoutMs = 60000) {
    const runWith = (commandName, commandArgs) => spawnSync(commandName, commandArgs, {
      encoding: 'utf-8',
      timeout: timeoutMs,
      stdio: process.env.DEBUG ? 'inherit' : 'pipe'
    });

    let result = runWith(command, args);

    if (command === 'openclaw' && result.error && result.error.code === 'ENOENT') {
      const envEntry = process.env.OPENCLAW_ENTRY;
      const candidatePaths = [
        envEntry,
        path.resolve(__dirname, '..', '..', 'openclaw', 'openclaw.mjs'),
        path.resolve(__dirname, '..', 'openclaw', 'openclaw.mjs'),
        path.resolve(process.cwd(), 'openclaw', 'openclaw.mjs')
      ].filter(Boolean);

      for (const candidate of candidatePaths) {
        if (fs.existsSync(candidate)) {
          result = runWith(process.execPath, [candidate, ...(Array.isArray(args) ? args : [])]);
          break;
        }
      }
    }

    return result;
  }

  normalizeResetScope(scope) {
    const resolvedScope = typeof scope === 'string' && scope.trim() ? scope.trim() : 'config+creds+sessions';
    const allowedScopes = new Set(['config', 'config+creds+sessions', 'full']);
    if (!allowedScopes.has(resolvedScope)) {
      throw new Error(
        `Invalid reset scope '${resolvedScope}'. Use one of: config, config+creds+sessions, full.`,
      );
    }
    return resolvedScope;
  }

  async runSafeResetBeforeRestore(scope) {
    const resolvedScope = this.normalizeResetScope(scope);
    const resetArgs = ['reset', '--scope', resolvedScope, '--yes', '--non-interactive'];
    if (this.dryRun) {
      resetArgs.push('--dry-run');
    }

    const result = this.runCliCommand('openclaw', resetArgs, 90000);
    if (result.error && result.error.code === 'ENOENT') {
      throw new Error('openclaw CLI not found. Cannot run safe reset before restore.');
    }

    if (result.status !== 0) {
      const output = `${result.stdout || ''}\n${result.stderr || ''}`.trim();
      throw new Error(
        `openclaw reset failed before restore (scope: ${resolvedScope}).${output ? `\n${output}` : ''}`,
      );
    }

    console.log(`   🧹 OpenClaw reset completed before restore (scope: ${resolvedScope}).`);
  }

  async prepareRestoreRuntime() {
    if (process.env.RECLAW_SKIP_GATEWAY_STOP === '1') return;

    const result = this.runCliCommand('openclaw', ['gateway', 'stop']);

    if (result.error && result.error.code === 'ENOENT') {
      if (process.env.DEBUG) {
        console.log('   ℹ️ openclaw CLI not found. Skipping gateway stop step.');
      }
      return;
    }

    if (result.status === 0) {
      console.log('   🛑 OpenClaw gateway stopped before restore.');
      return;
    }

    const output = `${result.stdout || ''}\n${result.stderr || ''}`;
    const benign = /not loaded|not installed|service not installed|service unit not found/i.test(output);
    if (benign) {
      if (process.env.DEBUG) {
        console.log('   ℹ️ OpenClaw gateway was not running as a managed service.');
      }
      return;
    }

    console.warn('   ⚠️ Could not stop OpenClaw gateway automatically. Continuing restore.');
    if (process.env.DEBUG && output.trim()) {
      console.warn(output.trim());
    }
  }

  async postRestoreRuntimeCheck() {
    if (process.env.RECLAW_SKIP_GATEWAY_RESTART === '1') return;

    const useDoctorRepair = process.env.RECLAW_DOCTOR_REPAIR !== '0';
    const doctorArgs = useDoctorRepair
      ? ['doctor', '--fix', '--repair', '--non-interactive']
      : ['doctor', '--fix', '--non-interactive'];

    const doctor = this.runCliCommand('openclaw', doctorArgs);
    if (doctor.error && doctor.error.code === 'ENOENT') {
      if (process.env.DEBUG) {
        console.log('   ℹ️ openclaw CLI not found. Skipping post-restore doctor/restart.');
      }
      return;
    }

    if (doctor.status === 0) {
      if (useDoctorRepair) {
        console.log('   🩺 OpenClaw doctor --repair completed after restore.');
      }
    }

    if (doctor.status !== 0 && process.env.DEBUG) {
      const doctorOutput = `${doctor.stdout || ''}\n${doctor.stderr || ''}`.trim();
      if (doctorOutput) {
        console.warn('   ⚠️ openclaw doctor reported issues after restore:');
        console.warn(doctorOutput);
      }
    }

    const restart = this.runCliCommand('openclaw', ['gateway', 'restart']);
    if (restart.status === 0) {
      console.log('   🚀 OpenClaw gateway restarted after restore.');
      return;
    }

    // Restart failed — could be "not installed" or some other error.
    // Either way, try install + start as recovery before giving up.
    console.log('   📥 Gateway restart failed. Trying gateway install + start...');
    const install = this.runCliCommand('openclaw', ['gateway', 'install']);
    if (install.status === 0) {
      console.log('   ✅ Gateway installed.');
    } else if (process.env.DEBUG) {
      const installOut = `${install.stdout || ''}\n${install.stderr || ''}`.trim();
      if (installOut) console.warn('   ⚠️ Gateway install output:', installOut);
    }

    const start = this.runCliCommand('openclaw', ['gateway', 'start']);
    if (start.status === 0) {
      console.log('   🚀 OpenClaw gateway started after restore.');
      return;
    }

    console.warn('   ⚠️ Gateway start failed after install. Run "OC Gateway Start" manually.');
    if (process.env.DEBUG) {
      const startOut = `${start.stdout || ''}\n${start.stderr || ''}`.trim();
      if (startOut) console.warn(startOut);
    }
  }

  async resolveGitLfsPointerIfNeeded(absArchivePath) {
    if (!(await fs.pathExists(absArchivePath))) {
      throw new Error(`Archive not found: ${absArchivePath}`);
    }

    const stat = await fs.stat(absArchivePath);
    // Git LFS pointer files are tiny text files (usually ~120-200 bytes).
    if (stat.size > 1024 * 1024) return;

    const raw = await fs.readFile(absArchivePath, 'utf8').catch(() => null);
    if (!raw || !raw.startsWith('version https://git-lfs.github.com/spec/v1')) return;

    const archiveDir = path.dirname(absArchivePath);
    const archiveName = path.basename(absArchivePath);

    try {
      execSync('git lfs version', { stdio: 'ignore' });
    } catch (e) {
      throw new Error(`Archive '${archiveName}' is a Git LFS pointer, not the real backup file. Install Git LFS and run 'git lfs pull' in '${archiveDir}'.`);
    }

    try {
      console.log('   📦 Detected Git LFS pointer. Fetching actual backup content...');
      execSync(`git lfs pull --include="${archiveName}"`, { cwd: archiveDir, stdio: 'ignore' });
    } catch (e) {
      throw new Error(`Archive '${archiveName}' is stored via Git LFS but could not be fetched automatically. Run 'git lfs pull' in '${archiveDir}' and retry.`);
    }

    const post = await fs.stat(absArchivePath);
    if (post.size <= 1024 * 1024) {
      throw new Error(`Archive '${archiveName}' still looks like a Git LFS pointer after fetch. Run 'git lfs pull' in '${archiveDir}' and verify the file size.`);
    }
  }

  async deepLocalizePaths(dir, mapping) {
    if (!mapping || Object.keys(mapping).length === 0) return;
    
    const systemPluginDir = path.join(this.home, 'plugins').replace(/\\/g, '/');
    
    // Recursive search for config files to update
    const processDir = async (currentDir) => {
        const list = await fs.readdir(currentDir);
        for (let file of list) {
            const full = path.join(currentDir, file);
            // Skip node_modules etc
            if (['node_modules', '.git'].includes(file)) continue;
            
            const stat = await fs.stat(full);
            if (stat.isDirectory()) {
                await processDir(full);
            } else if (/\.(json|env|js)$/.test(file)) {
                // Read and check for remapped paths
                let content = await fs.readFile(full, 'utf8');
                let modified = false;
                
                // 1. Handle explicit bundled mappings
                for (const [original, safeName] of Object.entries(mapping)) {
                    const originalNormalized = original.replace(/\\/g, '/');
                    const originalEscaped = original.replace(/\\/g, '\\\\');
                    
                    const newPath = path.join(systemPluginDir, safeName).replace(/\\/g, '/');

                    if (content.includes(original)) {
                        content = content.split(original).join(newPath);
                        modified = true;
                    }
                    if (content.includes(originalNormalized)) {
                        content = content.split(originalNormalized).join(newPath);
                        modified = true;
                    }
                    if (content.includes(originalEscaped)) {
                        content = content.split(originalEscaped).join(newPath);
                        modified = true;
                    }
                }

                // 2. Cross-platform path sanitization (Windows -> Mac/Linux)
                // If we are on Mac/Linux, find any Windows-style paths (e.g., C:\Users\...) and fix them
                if (process.platform !== 'win32') {
                    // Match Windows paths like C:\Path or C:\\Path. 
                    // We look for a drive letter followed by backslashes or forward slashes.
                    const windowsPathRegex = /([a-zA-Z]:[\\\/]+(?:[^"<>|\r\n]+[\\\/]+)*[^"<>|\r\n]*)/g;
                    const matches = content.match(windowsPathRegex);
                    if (matches) {
                        for (const winPath of matches) {
                            // Convert all backslashes (single or double) to forward slashes and remove drive letter
                            const sanitized = winPath.replace(/^[a-zA-Z]:/, '').replace(/\\\\+/g, '/');
                            
                            // Heuristic: If it looks like it was pointing to a Windows User profile, 
                            // remap it to the current user's home directory on Mac/Linux.
                            let finalSanitized = sanitized;
                            if (sanitized.toLowerCase().startsWith('/users/')) {
                                const parts = sanitized.split('/');
                                // parts[0] is empty, parts[1] is 'users', parts[2] is the old username
                                if (parts.length > 3) {
                                    const subPath = parts.slice(3).join('/');
                                    finalSanitized = path.join(os.homedir(), subPath).replace(/\\/g, '/');
                                }
                            }

                            // Privacy mode: mask file names and paths in output if not debugging
                            // Just print a generic counter or masked version to keep the video clean
                            if (process.env.DEBUG) {
                                console.log(`     🔄 Sanitizing path: ${winPath} -> ${finalSanitized}`);
                            } else {
                                // Only print the first time to avoid wall of text in video
                                if (!this._printedSanitizeMsg) {
                                    console.log(`     🔄 Sanitizing cross-platform paths... (details hidden for privacy)`);
                                    this._printedSanitizeMsg = true;
                                }
                            }
                            content = content.split(winPath).join(finalSanitized);
                            modified = true;
                        }
                    }

                    // 3. Fix mixed separators (e.g., /Users/jacob/.openclaw/workspaces\agent-name)
                    // This happens if some parts were fixed but others remained, or if the config has mixed slashes.
                    // We look for strings that start with / (Unix absolute) but contain \ (Windows separator).
                    const mixedPathRegex = /(\/[^\s"<>|\r\n]*\\[^\s"<>|\r\n]*)/g;
                    const mixedMatches = content.match(mixedPathRegex);
                    if (mixedMatches) {
                        for (const mixedPath of mixedMatches) {
                            const fixed = mixedPath.replace(/\\+/g, '/');
                            if (process.env.DEBUG) {
                                console.log(`     🔄 Fixing mixed separators: ${mixedPath} -> ${fixed}`);
                            }
                            content = content.split(mixedPath).join(fixed);
                            modified = true;
                        }
                    }
                }
                
                if (modified) {
                    if (process.env.DEBUG) {
                        console.log(`     ✏️ Updated paths in: ${file}`);
                    }
                    await fs.writeFile(full, content);
                }
            }
        }
    };
    
    await processDir(dir);
  }

  async getBackups() {
    if (!(await fs.pathExists(this.dest))) return [];
    const files = await fs.readdir(this.dest);
    return files
      .filter((fileName) => {
        const normalized = String(fileName || '').toLowerCase();
        const supportedArchive =
          normalized.endsWith('.zip') ||
          normalized.endsWith('.tar.gz') ||
          normalized.endsWith('.tgz') ||
          normalized.endsWith('.tar.gz.enc') ||
          normalized.endsWith('.tgz.enc') ||
          normalized.endsWith('.enc');
        const looksLikeBackup =
          normalized.startsWith('openclaw_backup_') || normalized.includes('openclaw') || normalized.includes('reclaw');
        return supportedArchive && looksLikeBackup;
      })
      .sort()
      .reverse();
  }

  async getBackupMetadata(fileName) {
    const archivePath = path.join(this.dest, fileName);
    const stats = await fs.stat(archivePath).catch(() => null);
    if (!stats || !stats.isFile()) {
      return null;
    }

    return {
      name: fileName,
      archivePath,
      archiveType: this.inferArchiveTypeFromName(fileName),
      encrypted: this.isEncryptedArchiveName(fileName),
      size: stats.size,
      modifiedAt: stats.mtime.toISOString(),
      mtimeMs: stats.mtimeMs
    };
  }

  async listBackups(options = {}) {
    const limitRaw = options.limit;
    const limit = limitRaw == null ? null : Number(limitRaw);
    if (limit != null && (!Number.isInteger(limit) || limit <= 0)) {
      throw new Error(`Invalid --limit value '${limitRaw}'. It must be a positive integer.`);
    }

    const backupNames = await this.getBackups();
    const metadata = [];
    for (const fileName of backupNames) {
      const info = await this.getBackupMetadata(fileName);
      if (info) {
        metadata.push(info);
      }
    }

    metadata.sort((left, right) => right.mtimeMs - left.mtimeMs || left.name.localeCompare(right.name));
    const trimmed = limit == null ? metadata : metadata.slice(0, limit);
    return trimmed.map(({ mtimeMs, ...entry }) => entry);
  }

  async pruneBackups(options = {}) {
    const keepLastRaw = options.keepLast;
    const keepLast = keepLastRaw == null ? null : Number(keepLastRaw);
    if (keepLast != null && (!Number.isInteger(keepLast) || keepLast < 0)) {
      throw new Error(`Invalid --keep-last value '${keepLastRaw}'. It must be a non-negative integer.`);
    }

    const olderThanMs = this.parseOlderThanToMs(options.olderThan);
    if (keepLast == null && olderThanMs == null) {
      throw new Error('No prune policy specified. Use --keep-last and/or --older-than.');
    }

    const dryRun = options.dryRun === true || this.dryRun === true;
    const backupNames = await this.getBackups();
    const metadata = [];
    for (const fileName of backupNames) {
      const info = await this.getBackupMetadata(fileName);
      if (info) {
        metadata.push(info);
      }
    }

    metadata.sort((left, right) => right.mtimeMs - left.mtimeMs || left.name.localeCompare(right.name));

    const deleteSet = new Set();
    if (keepLast != null) {
      for (const backup of metadata.slice(keepLast)) {
        deleteSet.add(backup.name);
      }
    }

    if (olderThanMs != null) {
      const cutoff = Date.now() - olderThanMs;
      for (const backup of metadata) {
        if (backup.mtimeMs < cutoff) {
          deleteSet.add(backup.name);
        }
      }
    }

    const deleted = [];
    const retained = [];
    for (const backup of metadata) {
      const { mtimeMs, ...entry } = backup;
      if (!deleteSet.has(backup.name)) {
        retained.push(entry);
        continue;
      }

      if (!dryRun) {
        await fs.remove(backup.archivePath);
      }
      deleted.push(entry);
    }

    return {
      ok: true,
      dryRun,
      keepLast,
      olderThan: typeof options.olderThan === 'string' ? options.olderThan : null,
      deletedCount: deleted.length,
      retainedCount: retained.length,
      deleted,
      retained
    };
  }

  async exportBackup(options = {}) {
    const scopeInfo = this.normalizeBackupScope(options.scope, 'config');
    const exportFormat = options.format || this.inferArchiveFormatFromOutput(options.output) || 'tar.gz';
    return this.createBackup({
      ...options,
      format: exportFormat,
      scope: scopeInfo.raw,
      includeWorkspace: this.scopeIncludesCategory(scopeInfo, 'workspace') && options.includeWorkspace !== false,
      verify: options.verify === true
    });
  }

  zipDirectory(sourceDir, outPath) {
    // Create archive with encryption if password set
    let archive;
    if (this.password) {
        // Use standard ZipCrypto (legacy) instead of AES-256
        // This ensures compatibility with macOS built-in unzip and Windows Explorer
        archive = archiver('zip-encrypted', { 
            zlib: { level: 9 }, 
            encryptionMethod: 'zip20', // Standard ZipCrypto (compatible)
            password: this.password 
        });
    } else {
        archive = archiver('zip', { zlib: { level: 9 } });
    }
    
    const stream = fs.createWriteStream(outPath);
    return new Promise((resolve, reject) => {
      archive.directory(sourceDir, false)
             .on('error', err => reject(err))
             .pipe(stream);
             
      stream.on('close', () => resolve());
      archive.finalize();
    });
  }
}

module.exports = BackupService;