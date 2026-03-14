#!/usr/bin/env node
const { program } = require('commander');
const chalk = require('chalk');
const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');
const BackupService = require('../lib/index');

function parseNpmOriginalArgv(env = process.env) {
  try {
    if (!env.npm_config_argv) return [];
    const parsed = JSON.parse(env.npm_config_argv);
    return Array.isArray(parsed.original) ? parsed.original : [];
  } catch (e) {
    return [];
  }
}

function parseRestoreArgsFromTokens(tokens) {
  let password;
  let archive;

  const restoreIndex = tokens.lastIndexOf('restore');
  const scanTokens = restoreIndex >= 0 ? tokens.slice(restoreIndex + 1) : tokens;

  for (let i = 0; i < scanTokens.length; i += 1) {
    const token = scanTokens[i];
    if (!token) continue;

    if (token === '--password' || token === '-p') {
      if (scanTokens[i + 1] && !String(scanTokens[i + 1]).startsWith('-')) {
        password = scanTokens[i + 1];
        i += 1;
      }
      continue;
    }

    if (token.startsWith('--password=')) {
      password = token.slice('--password='.length);
      continue;
    }

    if (token.startsWith('-p=')) {
      password = token.slice(3);
      continue;
    }

    if (!token.startsWith('-') && !archive) {
      archive = token;
    }
  }

  return { password, archive };
}

function resolveRestoreInputs(archive, options, command, env = process.env, argv = process.argv) {
  const resolved = {
    archive,
    password: options.password || null
  };

  if (!resolved.password) {
    resolved.password =
      env.npm_config_password ||
      env.npm_config_p ||
      env.RECLAW_PASSWORD ||
      null;
  }

  const commandArgs = command && command.parent && Array.isArray(command.parent.args) ? command.parent.args : [];
  const parsedFromCommandArgs = parseRestoreArgsFromTokens(commandArgs);

  if (!resolved.password && parsedFromCommandArgs.password) {
    resolved.password = parsedFromCommandArgs.password;
  }
  if (!resolved.archive && parsedFromCommandArgs.archive) {
    resolved.archive = parsedFromCommandArgs.archive;
  }

  const parsedFromArgv = parseRestoreArgsFromTokens(Array.isArray(argv) ? argv.slice(2) : []);
  if (!resolved.password && parsedFromArgv.password) {
    resolved.password = parsedFromArgv.password;
  }
  if (!resolved.archive && parsedFromArgv.archive) {
    resolved.archive = parsedFromArgv.archive;
  }

  const parsedFromNpmOriginal = parseRestoreArgsFromTokens(parseNpmOriginalArgv(env));
  if (!resolved.password && parsedFromNpmOriginal.password) {
    resolved.password = parsedFromNpmOriginal.password;
  }
  if (!resolved.archive && parsedFromNpmOriginal.archive) {
    resolved.archive = parsedFromNpmOriginal.archive;
  }

  return resolved;
}

function ensureLauncherExecutable() {
  if (process.platform === 'win32') return;
  try {
    const launcherPath = path.resolve(__dirname, '..', 'reclaw');
    fs.chmodSync(launcherPath, 0o755);
  } catch (e) {
    // Best effort only. Restore can still proceed via node/npm path.
  }
}

function runOpenclawCommand(args) {
  const commandArgs = Array.isArray(args) ? args.filter((entry) => typeof entry === 'string' && entry.length > 0) : [];
  if (commandArgs.length === 0) {
    throw new Error('No OpenClaw arguments were provided. Example: reclaw openclaw doctor --repair');
  }

  const runWith = (command, commandArgsToRun) => spawnSync(command, commandArgsToRun, {
    encoding: 'utf-8',
    stdio: 'inherit',
    shell: process.platform === 'win32'
  });

  const resolveLocalOpenclawEntry = () => {
    const envEntry = process.env.OPENCLAW_ENTRY;
    const candidatePaths = [
      envEntry,
      path.resolve(__dirname, '..', '..', 'openclaw', 'openclaw.mjs'),
      path.resolve(__dirname, '..', 'openclaw', 'openclaw.mjs'),
      path.resolve(process.cwd(), 'openclaw', 'openclaw.mjs')
    ].filter(Boolean);

    for (const candidate of candidatePaths) {
      if (fs.existsSync(candidate)) {
        return candidate;
      }
    }

    return null;
  };

  let result = runWith('openclaw', commandArgs);

  if (result.error && result.error.code === 'ENOENT') {
    const localEntry = resolveLocalOpenclawEntry();
    if (localEntry) {
      result = runWith(process.execPath, [localEntry, ...commandArgs]);
    }
  }

  if (result.error) {
    if (result.error.code === 'ENOENT') {
      throw new Error(
        'openclaw CLI not found in PATH and no local openclaw.mjs fallback was discovered. Clone OpenClaw (git clone https://github.com/openclaw/openclaw.git) and set OPENCLAW_ENTRY=/absolute/path/to/openclaw/openclaw.mjs or OPENCLAW_REPO=/absolute/path/to/openclaw.',
      );
    }
    throw result.error;
  }

  if (typeof result.status === 'number' && result.status !== 0) {
    throw new Error(`openclaw command failed with exit code ${result.status}.`);
  }
}

function normalizeVariadicArgs(args) {
  if (Array.isArray(args)) {
    return args.filter((entry) => typeof entry === 'string' && entry.length > 0);
  }
  if (typeof args === 'string' && args.length > 0) {
    return [args];
  }
  return [];
}

function runOpenclawAliasCommand(baseArgs, args) {
  runOpenclawCommand([...(Array.isArray(baseArgs) ? baseArgs : []), ...normalizeVariadicArgs(args)]);
}

function registerOpenclawAliasCommands() {
  const aliases = [
    {
      signature: 'doctor [args...]',
      baseArgs: ['doctor'],
      description: 'Run openclaw doctor commands (repair, deep, force, non-interactive)'
    },
    {
      signature: 'reset [args...]',
      baseArgs: ['reset'],
      description: 'Run openclaw reset commands (scope, dry-run, non-interactive)'
    },
    {
      signature: 'security [args...]',
      baseArgs: ['security'],
      description: 'Run openclaw security commands (audit, deep, fix, json)'
    },
    {
      signature: 'secrets [args...]',
      baseArgs: ['secrets'],
      description: 'Run openclaw secrets commands (reload, audit, configure, apply)'
    },
    {
      signature: 'status [args...]',
      baseArgs: ['status'],
      description: 'Run openclaw status commands (deep, all, usage, json)'
    },
    {
      signature: 'health [args...]',
      baseArgs: ['health'],
      description: 'Run openclaw health commands (json, verbose)'
    },
    {
      signature: 'channels [args...]',
      baseArgs: ['channels'],
      description: 'Run openclaw channels commands (status, probe, login/logout, logs)'
    },
    {
      signature: 'models [args...]',
      baseArgs: ['models'],
      description: 'Run openclaw models commands (status, probe, list, auth)'
    },
    {
      signature: 'gateway [args...]',
      baseArgs: ['gateway'],
      description: 'Run openclaw gateway commands (start, stop, restart, status, install)'
    },
    {
      signature: 'logs [args...]',
      baseArgs: ['logs'],
      description: 'Run openclaw logs commands (follow, json, limit)'
    },
    {
      signature: 'setup [args...]',
      baseArgs: ['setup'],
      description: 'Run openclaw setup commands'
    },
    {
      signature: 'skills [args...]',
      baseArgs: ['skills'],
      description: 'Run openclaw skills commands (list, info, check)'
    },
    {
      signature: 'sessions [args...]',
      baseArgs: ['sessions'],
      description: 'Run openclaw sessions commands'
    }
  ];

  aliases.forEach((alias) => {
    program
      .command(alias.signature)
      .description(`${alias.description} via OpenClaw passthrough`)
      .allowUnknownOption()
      .action(async (args) => {
        try {
          runOpenclawAliasCommand(alias.baseArgs, args);
        } catch (err) {
          console.error(chalk.red(`❌ OpenClaw ${alias.baseArgs.join(' ')} command failed: ${err.message}`));
          process.exit(1);
        }
      });
  });
}

program
  .version('1.0.0')
  .description('OpenClaw Professional Backup CLI');

async function runBackupCreate(options = {}) {
  const resolvedOptions =
    options && typeof options.opts === 'function'
      ? options.opts()
      : options;

  const password = resolvedOptions.password || process.env.RECLAW_PASSWORD || null;
  const includeBrowser =
    resolvedOptions.includeBrowser === true ||
    process.env.RECLAW_INCLUDE_BROWSER === '1' ||
    process.env.RECLAW_INCLUDE_BROWSER === 'true';

  const service = new BackupService({
    password,
    includeBrowser
  });

  const defaultFormat =
    resolvedOptions.format ||
    (resolvedOptions.output ? undefined : 'tar.gz');
  const shouldVerify = resolvedOptions.verify === false ? false : true;

  const result = await service.createBackup({
    output: resolvedOptions.output,
    format: defaultFormat,
    dryRun: Boolean(resolvedOptions.dryRun),
    verify: shouldVerify,
    onlyConfig: Boolean(resolvedOptions.onlyConfig),
    includeWorkspace: resolvedOptions.includeWorkspace !== false,
    includeBrowser,
    silent: Boolean(resolvedOptions.json)
  });

  if (resolvedOptions.json) {
    console.log(JSON.stringify(result, null, 2));
    return;
  }

  if (result.dryRun) {
    console.log(chalk.cyan(`🔍 Backup dry run completed for: ${result.archivePath}`));
  } else {
    console.log(chalk.green(`✅ Backup created successfully at: ${result.archivePath}`));
  }

  if (result.verified) {
    console.log(chalk.green('✅ Archive verification passed.'));
  }

  if (password && !result.dryRun) {
    console.log(chalk.yellow('🔒 Encrypted with password.'));
  }

  if (!result.dryRun) {
    console.log(chalk.cyan(`📦 Archive format: ${result.archiveFormat}${result.encrypted ? ' (encrypted)' : ''}`));
  }

  if (includeBrowser) {
    console.log(chalk.yellow('🌐 Included browser directory in backup payload.'));
  }
}

async function runBackupVerify(archive, options = {}) {
  const resolvedOptions =
    options && typeof options.opts === 'function'
      ? options.opts()
      : options;

  const password = resolvedOptions.password || process.env.RECLAW_PASSWORD || null;

  const service = new BackupService({ password });
  const result = await service.verifySnapshot(archive, { silent: true });

  if (resolvedOptions.json) {
    console.log(JSON.stringify(result, null, 2));
    return;
  }

  console.log(chalk.green('✅ Backup verification passed.'));
  console.log(service.formatVerifySummary(result));
}

async function runBackupRestore(archive, options = {}, command = null) {
  const resolvedOptions =
    options && typeof options.opts === 'function'
      ? options.opts()
      : options;

  ensureLauncherExecutable();
  const resolvedInputs = resolveRestoreInputs(archive, resolvedOptions, command || options);
  const password = resolvedInputs.password || process.env.RECLAW_PASSWORD || null;
  const service = new BackupService({
    dryRun: Boolean(resolvedOptions.dryRun),
    password
  });

  if (resolvedOptions.dryRun) {
    console.log(chalk.cyan('🔍 DRY RUN: Simulating restoration...'));
  }
  if (resolvedOptions.safeReset) {
    console.log(chalk.yellow(`🧹 Running safe reset before restore (scope: ${resolvedOptions.resetScope})...`));
  }

  await service.restore(resolvedInputs.archive, {
    safeReset: Boolean(resolvedOptions.safeReset),
    resetScope: resolvedOptions.resetScope,
    verify: Boolean(resolvedOptions.verify),
    scope: resolvedOptions.scope
  });

  if (resolvedOptions.dryRun) {
    console.log(chalk.cyan('✅ Dry run complete. No files were modified.'));
  } else {
    console.log(chalk.green('✅ Restore complete!'));
  }
}

async function runBackupList(options = {}) {
  const resolvedOptions =
    options && typeof options.opts === 'function'
      ? options.opts()
      : options;

  const service = new BackupService();
  const backups = await service.listBackups({
    limit: resolvedOptions.limit
  });

  if (resolvedOptions.json) {
    console.log(JSON.stringify({ backups }, null, 2));
    return;
  }

  if (backups.length === 0) {
    console.log(chalk.yellow('No backup archives found.'));
    return;
  }

  console.log(chalk.cyan(`📦 Found ${backups.length} backup archive(s):`));
  backups.forEach((entry, index) => {
    const sizeMb = `${(entry.size / 1024 / 1024).toFixed(2)} MB`;
    const enc = entry.encrypted ? 'encrypted' : 'plain';
    console.log(`${index + 1}. ${entry.name}  (${entry.archiveType}, ${enc}, ${sizeMb}, ${entry.modifiedAt})`);
  });
}

async function runBackupPrune(options = {}) {
  const resolvedOptions =
    options && typeof options.opts === 'function'
      ? options.opts()
      : options;

  const service = new BackupService({
    dryRun: Boolean(resolvedOptions.dryRun)
  });

  const result = await service.pruneBackups({
    keepLast: resolvedOptions.keepLast,
    olderThan: resolvedOptions.olderThan,
    dryRun: Boolean(resolvedOptions.dryRun)
  });

  if (resolvedOptions.json) {
    console.log(JSON.stringify(result, null, 2));
    return;
  }

  if (result.dryRun) {
    console.log(chalk.cyan(`🔍 Dry run: ${result.deletedCount} backup(s) would be deleted.`));
  } else {
    console.log(chalk.green(`✅ Pruned ${result.deletedCount} backup(s).`));
  }
}

async function runBackupExport(options = {}) {
  const resolvedOptions =
    options && typeof options.opts === 'function'
      ? options.opts()
      : options;

  const password = resolvedOptions.password || process.env.RECLAW_PASSWORD || null;
  const includeBrowser =
    resolvedOptions.includeBrowser === true ||
    process.env.RECLAW_INCLUDE_BROWSER === '1' ||
    process.env.RECLAW_INCLUDE_BROWSER === 'true';

  const service = new BackupService({
    password,
    includeBrowser
  });

  const result = await service.exportBackup({
    scope: resolvedOptions.scope,
    output: resolvedOptions.output,
    format: resolvedOptions.format,
    dryRun: Boolean(resolvedOptions.dryRun),
    verify: Boolean(resolvedOptions.verify),
    includeBrowser,
    includeWorkspace: resolvedOptions.includeWorkspace !== false,
    silent: Boolean(resolvedOptions.json)
  });

  if (resolvedOptions.json) {
    console.log(JSON.stringify(result, null, 2));
    return;
  }

  if (result.dryRun) {
    console.log(chalk.cyan(`🔍 Export dry run completed for: ${result.archivePath}`));
  } else {
    console.log(chalk.green(`✅ Export backup created at: ${result.archivePath}`));
  }
}

program
  .command('backup [mode] [archive]')
  .description('Create, verify, restore, list, prune, and export ReClaw backup archives')
  .option('-o, --output <path>', 'Archive output path or destination directory')
  .option('--format <format>', 'Archive format: zip or tar.gz (default: tar.gz for create/export)')
  .option('--scope <scope>', 'Scope for restore/export: full|config|creds|sessions|config+creds|config+creds+sessions')
  .option('--limit <count>', 'Maximum number of backups to list')
  .option('--keep-last <count>', 'For prune: keep the newest N backups')
  .option('--older-than <age>', 'For prune: delete backups older than age (e.g. 30d, 12h)')
  .option('-p, --password <password>', 'Password for encryption (or use RECLAW_PASSWORD)')
  .option('--include-browser', 'Include ~/.openclaw/browser in backup payload')
  .option('--dry-run', 'Print the backup plan without writing the archive')
  .option('--verify', 'Verify archive integrity (default: on for backup create; opt-in for restore/export)')
  .option('--no-verify', 'Disable create-time verification for backup create')
  .option('--safe-reset', 'Before restore, run: openclaw reset --scope config+creds+sessions --yes --non-interactive')
  .option('--reset-scope <scope>', 'Scope for --safe-reset: config|config+creds+sessions|full', 'config+creds+sessions')
  .option('--only-config', 'Back up only openclaw.json')
  .option('--no-include-workspace', 'Exclude workspace/workspaces paths from backup')
  .option('--json', 'Output JSON')
  .addHelpText(
    'after',
    `\nExamples:\n  reclaw backup\n  reclaw backup create --password "secret123"\n  reclaw backup create --no-verify\n  reclaw backup verify /path/to/archive.zip --json\n  reclaw backup restore /path/to/archive.tar.gz --verify\n  reclaw backup restore --scope config+creds --dry-run\n  reclaw backup list --json --limit 10\n  reclaw backup prune --keep-last 5\n  reclaw backup prune --older-than 30d --dry-run\n  reclaw backup export --scope credentials --output ~/Secure/creds-backup.tar.gz\n`,
  )
  .action(async (mode, archive, command) => {
    const normalizedMode = mode ? String(mode).toLowerCase() : 'create';

    try {
      if (normalizedMode === 'create') {
        await runBackupCreate(command);
        return;
      }

      if (normalizedMode === 'verify') {
        await runBackupVerify(archive, command);
        return;
      }

      if (normalizedMode === 'restore') {
        await runBackupRestore(archive, command, command);
        return;
      }

      if (normalizedMode === 'list') {
        await runBackupList(command);
        return;
      }

      if (normalizedMode === 'prune') {
        await runBackupPrune(command);
        return;
      }

      if (normalizedMode === 'export') {
        await runBackupExport(command);
        return;
      }

      throw new Error(`Unsupported backup subcommand '${mode}'. Use one of: create, verify, restore, list, prune, export.`);
    } catch (err) {
      const modePrefixes = {
        verify: 'Backup verification failed',
        restore: 'Backup restore failed',
        list: 'Backup list failed',
        prune: 'Backup prune failed',
        export: 'Backup export failed',
      };
      const errorPrefix = modePrefixes[normalizedMode] || 'Backup failed';
      console.error(chalk.red(`❌ ${errorPrefix}: ${err.message}`));
      process.exit(1);
    }
  });

program
  .command('restore [archive]')
  .description('Restore an OpenClaw instance (uses latest backup if no file specified)')
  .option('--dry-run', 'Simulate the restoration without changing files')
  .option('--verify', 'Verify archive integrity before restore')
  .option('--scope <scope>', 'Restore scope: full|config|creds|sessions|config+creds|config+creds+sessions', 'full')
  .option('--safe-reset', 'Run openclaw reset --scope config+creds+sessions --yes --non-interactive before restore')
  .option('--reset-scope <scope>', 'Scope for --safe-reset: config|config+creds+sessions|full', 'config+creds+sessions')
  .option('-p, --password <password>', 'Password for decryption (or use .env)')
  .allowUnknownOption() // <--- Allow unknown options (like args swallowed by npm)
  .action(async (archive, options, command) => {
    ensureLauncherExecutable();

    const resolved = resolveRestoreInputs(archive, options, command);

    try {
      const password = resolved.password;
      const service = new BackupService({ 
        dryRun: options.dryRun,
        password: password
      });
      if (options.dryRun) {
        console.log(chalk.cyan('🔍 DRY RUN: Simulating restoration...'));
      }
      if (options.safeReset) {
        console.log(chalk.yellow(`🧹 Running safe reset before restore (scope: ${options.resetScope})...`));
      }
      await service.restore(resolved.archive, {
        safeReset: Boolean(options.safeReset),
        resetScope: options.resetScope,
        verify: Boolean(options.verify),
        scope: options.scope
      });
      if (options.dryRun) {
        console.log(chalk.cyan('✅ Dry run complete. No files were modified.'));
      } else {
        console.log(chalk.green('✅ Restore complete!'));
      }
    } catch (err) {
      console.error(chalk.red(`❌ Restore failed: ${err.message}`));
      process.exit(1);
    }
  });

program
  .command('openclaw [args...]')
  .description('Run official OpenClaw CLI commands through ReClaw')
  .allowUnknownOption()
  .action(async (args) => {
    try {
      runOpenclawCommand(args);
    } catch (err) {
      console.error(chalk.red(`❌ OpenClaw command failed: ${err.message}`));
      process.exit(1);
    }
  });

registerOpenclawAliasCommands();

if (require.main === module) {
  program.parse(process.argv);
}

module.exports = {
  parseNpmOriginalArgv,
  parseRestoreArgsFromTokens,
  resolveRestoreInputs
};
