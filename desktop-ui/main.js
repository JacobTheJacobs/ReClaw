const fs = require('fs');
const path = require('path');
const http = require('http');
const os = require('os');
const { app, BrowserWindow, dialog, ipcMain, shell } = require('electron');
const { spawn } = require('child_process');

// When packaged, bin/lib/scripts are extracted to app.asar.unpacked — spawn can't read inside .asar
const repoRoot = (function () {
  const base = path.resolve(__dirname, '..');
  if (base.includes('app.asar') && !base.includes('app.asar.unpacked')) {
    return base.replace('app.asar', 'app.asar.unpacked');
  }
  return base;
}());
function resolveBackupDir() {
  return (
    process.env.RECLAW_BACKUP_DIR ||
    process.env.BACKUP_DIR ||
    (app.isPackaged ? path.join(os.homedir(), 'claw-backup') : path.join(repoRoot, 'backups'))
  );
}

function resolveResetBackupDir() {
  return (
    process.env.RECLAW_RESET_BACKUP_DIR ||
    process.env.RECLAW_BACKUP_DIR ||
    process.env.BACKUP_DIR ||
    (app.isPackaged ? path.join(os.homedir(), 'claw-backup') : path.join(path.dirname(repoRoot), 'claw-backup'))
  );
}

function buildOpenclawBackupOutputPath(actionId) {
  const createdAt = new Date().toISOString();
  const timestamp = createdAt.replace(/[:.]/g, '-');
  const suffixMap = {
    'oc-backup-create-only-config': '_config',
    'oc-backup-create-no-workspace': '_no-workspace',
    'oc-backup-create-verify': '_verify',
  };
  const suffix = suffixMap[actionId] || '';
  const fileName = `openclaw_backup_${timestamp}${suffix}.tar.gz`;
  return path.join(resolveBackupDir(), fileName);
}
// Resolve a sane Node executable: prefer explicit env override, then process.execPath,
// then fall back to 'node'. Guard against env vars pointing at a directory.
function resolveNodeExecutable() {
  const envCandidate = process.env.RECLAW_NODE_PATH || process.env.NODE || null;
  const candidates = [envCandidate, process.execPath || null, 'node'].filter(Boolean);
  for (const cand of candidates) {
    try {
      const resolved = path.resolve(cand);
      const st = fs.statSync(resolved);
      if (st.isFile()) return resolved;
      if (st.isDirectory()) {
        const alt = path.join(resolved, 'node');
        if (fs.existsSync(alt) && fs.statSync(alt).isFile()) return alt;
      }
    } catch (_) {
      // ignore and try next candidate
    }
  }
  return 'node';
}
const nodeExecutable = resolveNodeExecutable();
const APP_ID = 'com.jacobthejacobs.reclaw';
const MATRIX_ACTION_TIMEOUT_MS = 45000;
const MATRIX_ACTION_TIMEOUT_EXEMPT = new Set(['oc-logs-follow']);

let activeProcess = null;

function killProcessTree(proc, signal = 'SIGTERM') {
  if (!proc || !proc.pid) {
    return false;
  }

  const pid = proc.pid;
  if (process.platform === 'win32') {
    try {
      spawn('taskkill', ['/PID', String(pid), '/T', '/F'], { windowsHide: true });
      return true;
    } catch (_) {
      try {
        proc.kill(signal);
        return true;
      } catch (_) {
        return false;
      }
    }
  }

  try {
    process.kill(-pid, signal);
  } catch (_) {
    try {
      process.kill(pid, signal);
    } catch (_) {
      return false;
    }
  }

  return true;
}
let activeAction = null;

function getIconCandidates() {
  return process.platform === 'win32'
    // Prefer PNG at runtime on Windows for more consistent title/taskbar rendering.
    ? [path.join(__dirname, 'assets', 'reclaw.png'), path.join(__dirname, 'assets', 'reclaw.ico')]
    : [path.join(__dirname, 'assets', 'reclaw.png')];
}

function getWindowIconPath() {
  const candidates = getIconCandidates();
  return candidates.find((item) => fs.existsSync(item));
}

const hasSingleInstanceLock = app.requestSingleInstanceLock();
if (!hasSingleInstanceLock) {
  app.quit();
  process.exit(0);
}

app.setName('ReClaw');
if (process.platform === 'win32') {
  app.setAppUserModelId(APP_ID);
}

function getProfile() {
  return process.platform === 'win32' ? 'windows' : 'unix';
}

function quoteIfNeeded(value) {
  if (!value) {
    return '""';
  }
  if (/\s|"/.test(value)) {
    return `"${value.replace(/"/g, '\\"')}"`;
  }
  return value;
}

function formatCommand(filePath, args) {
  const parts = [quoteIfNeeded(filePath), ...(args || []).map((arg) => quoteIfNeeded(String(arg)))];
  return parts.join(' ');
}

function createStep(label, filePath, args, options = {}) {
  const stepEnv = { ...(options.env || {}) };
  // Electron main process uses Electron binary as execPath; force Node mode for script steps.
  if (filePath === nodeExecutable && process.versions && process.versions.electron) {
    stepEnv.ELECTRON_RUN_AS_NODE = '1';
  }

  // When packaged, bin/lib/scripts are in app.asar.unpacked but node_modules stays in app.asar.
  // Set NODE_PATH so unpacked scripts can resolve their dependencies from inside the asar.
  if (app.isPackaged) {
    const asarNodeModules = path.join(process.resourcesPath, 'app.asar', 'node_modules');
    const sep = process.platform === 'win32' ? ';' : ':';
    stepEnv.NODE_PATH = stepEnv.NODE_PATH
      ? `${asarNodeModules}${sep}${stepEnv.NODE_PATH}`
      : asarNodeModules;
  }

  return {
    label,
    filePath,
    args,
    cwd: repoRoot,
    timeoutMs: Number.isFinite(options.timeoutMs) ? options.timeoutMs : null,
    env: stepEnv,
  };
}

function getPowerShellExecutable() {
  return process.platform === 'win32' ? 'powershell.exe' : null;
}

function getBashExecutable() {
  return process.platform === 'win32' ? 'bash.exe' : 'bash';
}

function resolveOpenclawRepoDir() {
  const envRepo = process.env.OPENCLAW_REPO;
  const candidateDirs = [
    envRepo,
    path.join(os.homedir(), 'openclaw'),
    path.resolve(repoRoot, '..', 'openclaw'),
    path.resolve(process.cwd(), 'openclaw'),
  ].filter(Boolean);

  for (const candidate of candidateDirs) {
    try {
      const normalized = path.resolve(candidate);
      const hasEntrypoint = fs.existsSync(path.join(normalized, 'openclaw.mjs'));
      if (hasEntrypoint) {
        return normalized;
      }
    } catch (_) {
      // best-effort probing only
    }
  }

  return null;
}

function buildMissingOpenclawRepoError() {
  return [
    'OpenClaw repository not found for update.',
    'Clone it first: git clone https://github.com/openclaw/openclaw.git',
    'Then point ReClaw to it: export OPENCLAW_REPO=/absolute/path/to/openclaw',
    'or set OPENCLAW_ENTRY=/absolute/path/to/openclaw/openclaw.mjs for CLI actions.',
  ].join('\n');
}

const OPENCLAW_MATRIX_ACTIONS = [
  {
    id: 'reclaw-backup-list',
    label: 'ReClaw Backup List',
    emoji: '🗂️',
    description: 'Run reclaw backup list.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['backup', 'list'],
  },
  {
    id: 'reclaw-backup-prune-plan',
    label: 'ReClaw Backup Prune Plan',
    emoji: '🧪',
    description: 'Run reclaw backup prune dry-run policy.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['backup', 'prune', '--keep-last', '5', '--older-than', '30d', '--dry-run'],
  },
  {
    id: 'reclaw-backup-export',
    label: 'ReClaw Backup Export',
    emoji: '📤',
    description: 'Run reclaw backup export scoped archive.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['backup', 'export', '--scope', 'config+creds+sessions', '--verify'],
  },
  {
    id: 'reclaw-backup-verify',
    label: 'ReClaw Backup Verify',
    emoji: '🔍',
    description: 'Run reclaw backup verify.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['backup', 'verify'],
  },
  {
    id: 'oc-backup-create',
    label: 'OC Backup Create',
    emoji: '🧰',
    description: 'Run openclaw backup create.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'backup', 'create'],
  },
  {
    id: 'oc-backup-create-verify',
    label: 'OC Backup Verify Create',
    emoji: '✅',
    description: 'Run openclaw backup create --verify.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'backup', 'create', '--verify'],
  },
  {
    id: 'oc-backup-create-plan',
    label: 'OC Backup Plan',
    emoji: '🧪',
    description: 'Run openclaw backup create --dry-run --json.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'backup', 'create', '--dry-run', '--json'],
  },
  {
    id: 'oc-backup-create-only-config',
    label: 'OC Backup Config Only',
    emoji: '🧾',
    description: 'Run openclaw backup create --only-config.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'backup', 'create', '--only-config'],
  },
  {
    id: 'oc-backup-create-no-workspace',
    label: 'OC Backup No Workspace',
    emoji: '📁',
    description: 'Run openclaw backup create --no-include-workspace.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'backup', 'create', '--no-include-workspace'],
  },
  {
    id: 'oc-backup-verify',
    label: 'OC Backup Verify',
    emoji: '🔍',
    description: 'Run openclaw backup verify.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'backup', 'verify'],
  },
  {
    id: 'oc-reset-safe',
    label: 'OC Reset Safe',
    emoji: '🧹',
    description: 'Run openclaw reset safe scope non-interactive.',
    group: 'danger',
    requiresPassword: false,
    destructive: true,
    confirmPhrase: 'RESET',
    commandArgs: ['openclaw', 'reset', '--scope', 'config+creds+sessions', '--yes', '--non-interactive'],
  },
  {
    id: 'oc-reset-dry-run',
    label: 'OC Reset Dry Run',
    emoji: '🧪',
    description: 'Run openclaw reset --dry-run.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'reset', '--dry-run'],
  },
  {
    id: 'oc-doctor',
    label: 'OC Doctor',
    emoji: '🩺',
    description: 'Run openclaw doctor --non-interactive --yes.',
    group: 'easy',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'doctor', '--non-interactive', '--yes'],
  },
  {
    id: 'oc-update-pull',
    label: 'OC Update Pull',
    emoji: '⬇️',
    description: 'Pull latest OpenClaw source with git pull --ff-only.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
  },
  {
    id: 'oc-doctor-repair',
    label: 'OC Doctor Repair',
    emoji: '🛠️',
    description: 'Run openclaw doctor --repair --non-interactive --yes.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'doctor', '--repair', '--non-interactive', '--yes'],
  },
  {
    id: 'oc-doctor-repair-force',
    label: 'OC Doctor Force',
    emoji: '⚙️',
    description: 'Run openclaw doctor --repair --force --non-interactive --yes.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'doctor', '--repair', '--force', '--non-interactive', '--yes'],
  },
  {
    id: 'oc-doctor-non-interactive',
    label: 'OC Doctor NonInteractive',
    emoji: '🤖',
    description: 'Run openclaw doctor --non-interactive --yes.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'doctor', '--non-interactive', '--yes'],
  },
  {
    id: 'oc-doctor-deep',
    label: 'OC Doctor Deep',
    emoji: '🧬',
    description: 'Run openclaw doctor --deep --non-interactive --yes.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'doctor', '--deep', '--non-interactive', '--yes'],
  },
  {
    id: 'oc-doctor-yes',
    label: 'OC Doctor Yes',
    emoji: '👍',
    description: 'Run openclaw doctor --yes --non-interactive.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'doctor', '--yes', '--non-interactive'],
  },
  {
    id: 'oc-doctor-token',
    label: 'OC Doctor Token',
    emoji: '🔐',
    description: 'Run openclaw doctor --generate-gateway-token --non-interactive --yes.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'doctor', '--generate-gateway-token', '--non-interactive', '--yes'],
  },
  {
    id: 'oc-doctor-fix',
    label: 'OC Doctor Fix',
    emoji: '🧰',
    description: 'Run openclaw doctor --fix --non-interactive --yes.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'doctor', '--fix', '--non-interactive', '--yes'],
  },
  {
    id: 'oc-security-audit',
    label: 'OC Security Audit',
    emoji: '🛡️',
    description: 'Run openclaw security audit.',
    group: 'easy',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'security', 'audit'],
  },
  {
    id: 'oc-security-deep',
    label: 'OC Security Deep',
    emoji: '🔒',
    description: 'Run openclaw security audit --deep.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'security', 'audit', '--deep'],
  },
  {
    id: 'oc-security-fix',
    label: 'OC Security Fix',
    emoji: '🧯',
    description: 'Run openclaw security audit --fix.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'security', 'audit', '--fix'],
  },
  {
    id: 'oc-security-json',
    label: 'OC Security JSON',
    emoji: '📋',
    description: 'Run openclaw security audit --json.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'security', 'audit', '--json'],
  },
  {
    id: 'oc-secrets-reload',
    label: 'OC Secrets Reload',
    emoji: '🔄',
    description: 'Run openclaw secrets reload.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'secrets', 'reload'],
  },
  {
    id: 'oc-secrets-audit',
    label: 'OC Secrets Audit',
    emoji: '🔍',
    description: 'Run openclaw secrets audit.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'secrets', 'audit'],
  },
  {
    id: 'oc-status',
    label: 'OC Status',
    emoji: '📊',
    description: 'Run openclaw status.',
    group: 'easy',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'status'],
  },
  {
    id: 'oc-status-deep',
    label: 'OC Status Deep',
    emoji: '📈',
    description: 'Run openclaw status --deep.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'status', '--deep'],
  },
  {
    id: 'oc-status-all',
    label: 'OC Status All',
    emoji: '🧩',
    description: 'Run openclaw status --all.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'status', '--all'],
  },
  {
    id: 'oc-status-usage',
    label: 'OC Status Usage',
    emoji: '📐',
    description: 'Run openclaw status --usage.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'status', '--usage'],
  },
  {
    id: 'oc-health',
    label: 'OC Health',
    emoji: '💓',
    description: 'Run openclaw health.',
    group: 'easy',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'health'],
  },
  {
    id: 'oc-health-json',
    label: 'OC Health JSON',
    emoji: '🧷',
    description: 'Run openclaw health --json.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'health', '--json'],
  },
  {
    id: 'oc-channels-status',
    label: 'OC Channels Status',
    emoji: '📡',
    description: 'Run openclaw channels status.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'channels', 'status'],
  },
  {
    id: 'oc-channels-probe',
    label: 'OC Channels Probe',
    emoji: '📶',
    description: 'Run openclaw channels status --probe.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'channels', 'status', '--probe'],
  },
  {
    id: 'oc-models-status',
    label: 'OC Models Status',
    emoji: '🧠',
    description: 'Run openclaw models status.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'models', 'status'],
  },
  {
    id: 'oc-models-probe',
    label: 'OC Models Probe',
    emoji: '🛰️',
    description: 'Run openclaw models status --probe.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'models', 'status', '--probe'],
  },
  {
    id: 'oc-gateway-start',
    label: 'OC Gateway Start',
    emoji: '▶️',
    description: 'Run openclaw gateway start.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'gateway', 'start'],
  },
  {
    id: 'oc-gateway-stop',
    label: 'OC Gateway Stop',
    emoji: '⏹️',
    description: 'Run openclaw gateway stop.',
    group: 'danger',
    requiresPassword: false,
    destructive: true,
    confirmPhrase: 'STOP',
    commandArgs: ['openclaw', 'gateway', 'stop'],
  },
  {
    id: 'oc-gateway-status',
    label: 'OC Gateway Status',
    emoji: '📍',
    description: 'Run openclaw gateway status.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'gateway', 'status'],
  },
  {
    id: 'oc-gateway-status-deep',
    label: 'OC Gateway Deep',
    emoji: '🧪',
    description: 'Run openclaw gateway status --deep.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'gateway', 'status', '--deep'],
  },
  {
    id: 'oc-gateway-restart',
    label: 'OC Gateway Restart',
    emoji: '🔁',
    description: 'Run openclaw gateway restart.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'gateway', 'restart'],
  },
  {
    id: 'oc-gateway-install',
    label: 'OC Gateway Install',
    emoji: '📥',
    description: 'Run openclaw gateway install.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'gateway', 'install'],
  },
  {
    id: 'oc-gateway-uninstall',
    label: 'OC Gateway Uninstall',
    emoji: '🗑️',
    description: 'Run openclaw gateway uninstall.',
    group: 'danger',
    requiresPassword: false,
    destructive: true,
    confirmPhrase: 'UNINSTALL',
    commandArgs: ['openclaw', 'gateway', 'uninstall'],
  },
  {
    id: 'oc-logs-follow',
    label: 'OC Logs Follow',
    emoji: '📝',
    description: 'Run openclaw logs --follow (Stop to end).',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'logs', '--follow'],
  },
  {
    id: 'oc-setup',
    label: 'OC Setup',
    emoji: '🧱',
    description: 'Run openclaw setup.',
    group: 'tools',
    requiresPassword: false,
    destructive: false,
    commandArgs: ['openclaw', 'setup'],
  },
];

function getActions(profile) {
  const common = [
    {
      id: 'backup',
      label: 'Save Backup',
      emoji: '💾',
      description: 'Save everything safely now.',
      group: 'easy',
      requiresPassword: false,
      optionalPassword: true,
      destructive: false,
    },
    {
      id: 'restore-latest',
      label: 'Restore Latest',
      emoji: '♻️',
      description: 'Bring back your newest saved backup.',
      group: 'easy',
      requiresPassword: false,
      optionalPassword: true,
      destructive: false,
    },
    {
      id: 'restore-archive',
      label: 'Restore From Archive',
      emoji: '📦',
      description: 'Pick any backup archive and restore it.',
      group: 'tools',
      requiresPassword: false,
      optionalPassword: true,
      destructive: false,
      requiresArchive: true,
    },
    {
      id: 'verify-all',
      label: 'Check Health',
      emoji: '✅',
      description: 'Quickly check files and gateway health.',
      group: 'easy',
      requiresPassword: false,
      destructive: false,
    },
    {
      id: 'gateway-url',
      label: 'Show Dashboard Link',
      emoji: '🔗',
      description: 'Show secure dashboard URL with token.',
      group: 'tools',
      requiresPassword: false,
      destructive: false,
    },
    {
      id: 'dashboard-open',
      label: 'Open Dashboard',
      emoji: '🧭',
      description: 'Open the secure dashboard in browser.',
      group: 'easy',
      requiresPassword: false,
      destructive: false,
    },
  ];

  const operations = [
    ...OPENCLAW_MATRIX_ACTIONS,
    {
      id: 'oc-fix-missing-plugins',
      label: 'OC Fix Missing Plugins',
      emoji: '🧩',
      description: 'Remove missing plugin paths from OpenClaw config.',
      group: 'tools',
      requiresPassword: false,
      destructive: false,
    },
  ];

  if (profile === 'windows') {
    return [
      ...common,
      ...operations,
      {
        id: 'reset',
        label: 'Wipe Local Data',
        emoji: '🧹',
        description: 'Delete local OpenClaw data and clone on this computer.',
        group: 'danger',
        requiresPassword: false,
        destructive: true,
        confirmPhrase: 'yes',
      },
      {
        id: 'nuke',
        label: 'Nuke Local Data',
        emoji: '💥',
        description: 'Remove all local OpenClaw data without creating a backup.',
        group: 'danger',
        requiresPassword: false,
        destructive: true,
        confirmPhrase: 'yes',
      },
      {
        id: 'recover',
        label: 'Fix & Recover',
        emoji: '🛠️',
        description: 'Reclone, rebuild, restore, and restart gateway.',
        group: 'easy',
        requiresPassword: false,
        optionalPassword: true,
        destructive: false,
      },
      {
        id: 'fresh-install',
        label: 'Fresh Install (No Restore)',
        emoji: '🧼',
        description: 'Clone and rebuild OpenClaw without restoring a backup.',
        group: 'tools',
        requiresPassword: false,
        destructive: false,
      },
      {
        id: 'clone-openclaw',
        label: 'Clone OpenClaw Repo',
        emoji: '📥',
        description: 'Clone the OpenClaw source repo to the default location.',
        group: 'tools',
        requiresPassword: false,
        destructive: false,
      },
      {
        id: 'drill',
        label: 'Full Recovery Test',
        emoji: '🧪',
        description: 'Run full backup, wipe, recover, and verify flow.',
        group: 'danger',
        requiresPassword: true,
        destructive: true,
        confirmPhrase: 'DRILL',
      },
    ];
  }

  return [
    ...common,
    ...operations,
    {
      id: 'reset',
      label: 'Wipe Local Data',
      emoji: '🧹',
      description: 'Delete local OpenClaw data and clone on this computer.',
      group: 'danger',
      requiresPassword: false,
      destructive: true,
      confirmPhrase: 'yes',
    },
    {
      id: 'nuke',
      label: 'Nuke Local Data',
      emoji: '💥',
      description: 'Remove all local OpenClaw data without creating a backup.',
      group: 'danger',
      requiresPassword: false,
      destructive: true,
      confirmPhrase: 'yes',
    },
    {
      id: 'recover',
      label: 'Fix & Recover',
      emoji: '🛠️',
      description: 'Reclone, rebuild, restore, and restart gateway.',
      group: 'easy',
      requiresPassword: false,
      optionalPassword: true,
      destructive: false,
    },
    {
      id: 'fresh-install',
      label: 'Fresh Install (No Restore)',
      emoji: '🧼',
      description: 'Clone and rebuild OpenClaw without restoring a backup.',
      group: 'tools',
      requiresPassword: false,
      destructive: false,
    },
    {
      id: 'clone-openclaw',
      label: 'Clone OpenClaw Repo',
      emoji: '📥',
      description: 'Clone the OpenClaw source repo to the default location.',
      group: 'tools',
      requiresPassword: false,
      destructive: false,
    },
  ];
}

function buildActionPlan(actionId, payload, profile) {
  const password = (payload.password || '').trim();
  const archivePath = (payload.archivePath || '').trim();
  const cliPath = path.join(repoRoot, 'bin', 'cli.js');
  const tokenScriptPath = path.join(repoRoot, 'scripts', 'ensure-gateway-token.js');
  const verifyStatePath = path.join(repoRoot, 'scripts', 'verify-openclaw-state.js');
  const verifyHealthPath = path.join(repoRoot, 'scripts', 'verify-gateway-health.js');
  const cleanupPluginsPath = path.join(repoRoot, 'scripts', 'cleanup-openclaw-plugins.js');
  const resetWindowsPath = path.join(repoRoot, 'scripts', 'full-reset-openclaw.ps1');
  const nukeWindowsPath = path.join(repoRoot, 'scripts', 'full-nuke-openclaw.ps1');
  const recoverWindowsPath = path.join(repoRoot, 'scripts', 'recover-openclaw-local-windows.ps1');
  const freshInstallWindowsPath = path.join(repoRoot, 'scripts', 'fresh-install-openclaw-local-windows.ps1');
  const cloneOpenclawPath = path.join(repoRoot, 'scripts', 'clone-openclaw-repo.js');
  const drillWindowsPath = path.join(repoRoot, 'scripts', 'test-openclaw-recovery-windows.ps1');
  const resetUnixPath = path.join(repoRoot, 'scripts', 'full-reset-openclaw.sh');
  const nukeUnixPath = path.join(repoRoot, 'scripts', 'full-nuke-openclaw.sh');
  const recoverUnixPath = path.join(repoRoot, 'scripts', 'recover-openclaw-local-mac.sh');
  const freshInstallUnixPath = path.join(repoRoot, 'scripts', 'fresh-install-openclaw-local-mac.sh');
  const powershell = getPowerShellExecutable();
  const bash = getBashExecutable();
  const backupDir = resolveBackupDir();
  const resetBackupDir = resolveResetBackupDir();
  const backupEnv = { BACKUP_DIR: backupDir };

  const requirePassword = () => {
    if (!password) {
      throw new Error('Password is required for this action.');
    }
  };

  const matrixAction = OPENCLAW_MATRIX_ACTIONS.find((entry) => entry.id === actionId);
  if (matrixAction) {
    if (matrixAction.id === 'oc-update-pull') {
      const openclawRepoDir = resolveOpenclawRepoDir();
      if (!openclawRepoDir) {
        throw new Error(buildMissingOpenclawRepoError());
      }

      return {
        steps: [
          createStep('Pull latest OpenClaw source', 'git', ['-C', openclawRepoDir, 'pull', '--ff-only']),
        ],
      };
    }

    const timeoutMs = MATRIX_ACTION_TIMEOUT_EXEMPT.has(matrixAction.id)
      ? null
      : MATRIX_ACTION_TIMEOUT_MS;

    const commandArgs = [...(matrixAction.commandArgs || [])];
    const isOpenclawBackupCreate =
      matrixAction.id.startsWith('oc-backup-create') && matrixAction.id !== 'oc-backup-create-plan';

    if (isOpenclawBackupCreate) {
      const outputPath = buildOpenclawBackupOutputPath(matrixAction.id);
      fs.mkdirSync(path.dirname(outputPath), { recursive: true });
      commandArgs.push('--output', outputPath);
    }

    if (matrixAction.id === 'oc-backup-verify') {
      const candidatePath = archivePath || getLatestBackupArchivePath();
      if (!candidatePath) {
        throw new Error('No backup archive found. Run Save Backup or OC Backup Create first.');
      }
      commandArgs.push(candidatePath);
    }

    return {
      steps: [
        createStep(
          `Run ${matrixAction.label}`,
          nodeExecutable,
          [cliPath, ...commandArgs],
          { timeoutMs },
        ),
      ],
    };
  }

  switch (actionId) {
    case 'backup': {
      const backupArgs = [cliPath, 'backup', '--include-browser'];
      if (password) backupArgs.push('--password', password);
      return {
        steps: [createStep('Create backup', nodeExecutable, backupArgs, { env: backupEnv })],
      };
    }

    case 'oc-fix-missing-plugins':
      return {
        steps: [
          createStep('Clean missing plugin paths', nodeExecutable, [cleanupPluginsPath]),
        ],
      };

    case 'restore-latest': {
      const restoreArgs = [cliPath, 'restore'];
      if (password) restoreArgs.push('--password', password);
      return {
        steps: [createStep('Restore latest backup', nodeExecutable, restoreArgs, { env: backupEnv })],
      };
    }

    case 'restore-archive': {
      if (!archivePath) {
        throw new Error('Select a backup archive file first.');
      }
      const restoreArchiveArgs = [cliPath, 'restore'];
      if (password) restoreArchiveArgs.push('--password', password);
      restoreArchiveArgs.push(archivePath);
      return {
        steps: [createStep('Restore selected backup', nodeExecutable, restoreArchiveArgs, { env: backupEnv })],
      };
    }

    case 'verify-all':
      return {
        steps: [
          createStep('Verify gateway health', nodeExecutable, [verifyHealthPath, '--timeout', '8000'], { timeoutMs: 15000 }),
        ],
      };

    case 'gateway-url':
      return {
        steps: [createStep('Print tokenized dashboard URL', nodeExecutable, [tokenScriptPath])],
      };

    case 'dashboard-open':
      return {
        steps: [createStep('Open dashboard in browser', nodeExecutable, [tokenScriptPath, '--open'])],
      };

    case 'reset':
      if (profile === 'windows') {
        if (!powershell) {
          throw new Error('PowerShell is required for this action on Windows.');
        }
        const resetArgs = [
          '-NoProfile',
          '-ExecutionPolicy',
          'Bypass',
          '-File',
          resetWindowsPath,
          '-Yes',
          '-IncludeBrowser',
          '-RemoveOpenClawRepo',
          '-BackupDir',
          resetBackupDir,
        ];
        if (password) {
          resetArgs.push('-Password', password);
        }
        return {
          steps: [
            createStep('Reset / wipe local OpenClaw', powershell, resetArgs),
          ],
        };
      }

      const resetArgs = [
        resetUnixPath,
        '--yes',
        '--remove-openclaw-repo',
        '--backup-dir',
        resetBackupDir,
      ];
      if (password) {
        resetArgs.push('--password', password);
      }
      return {
        steps: [
          createStep('Reset / wipe local OpenClaw', bash, resetArgs),
        ],
      };

    case 'nuke':
      if (profile === 'windows') {
        if (!powershell) {
          throw new Error('PowerShell is required for this action on Windows.');
        }
        return {
          steps: [
            createStep('Nuke local OpenClaw', powershell, [
              '-NoProfile',
              '-ExecutionPolicy',
              'Bypass',
              '-File',
              nukeWindowsPath,
              '-Yes',
              '-RemoveOpenClawRepo',
            ]),
          ],
        };
      }

      return {
        steps: [
          createStep('Nuke local OpenClaw', bash, [
            nukeUnixPath,
            '--yes',
            '--remove-openclaw-repo',
          ]),
        ],
      };

    case 'recover':
      if (profile === 'windows') {
        if (!powershell) {
          throw new Error('PowerShell is required for this action on Windows.');
        }
        const recoverArgs = [
          '-NoProfile',
          '-ExecutionPolicy',
          'Bypass',
          '-File',
          recoverWindowsPath,
        ];
        if (password) {
          recoverArgs.push('-Password', password);
        }
        return {
          steps: [
            createStep('Reclone and recover OpenClaw', powershell, recoverArgs),
          ],
        };
      }

      const recoverArgs = [recoverUnixPath];
      if (password) {
        recoverArgs.push('--password', password);
      }
      return {
        steps: [
          createStep('Reclone and recover OpenClaw', bash, recoverArgs),
        ],
      };

    case 'fresh-install':
      if (profile === 'windows') {
        if (!powershell) {
          throw new Error('PowerShell is required for this action on Windows.');
        }
        return {
          steps: [
            createStep('Fresh install OpenClaw (no restore)', powershell, [
              '-NoProfile',
              '-ExecutionPolicy',
              'Bypass',
              '-File',
              freshInstallWindowsPath,
            ]),
          ],
        };
      }

      return {
        steps: [
          createStep('Fresh install OpenClaw (no restore)', bash, [
            freshInstallUnixPath,
          ]),
        ],
      };

    case 'clone-openclaw':
      return {
        steps: [
          createStep('Clone OpenClaw repo', nodeExecutable, [cloneOpenclawPath]),
        ],
      };

    case 'drill':
      requirePassword();
      if (profile !== 'windows') {
        throw new Error('Full drill action is currently available on Windows only.');
      }

      if (!powershell) {
        throw new Error('PowerShell is required for this action on Windows.');
      }

      return {
        steps: [
          createStep('Run full recovery drill', powershell, [
            '-NoProfile',
            '-ExecutionPolicy',
            'Bypass',
            '-File',
            drillWindowsPath,
            '-Password',
            password,
            '-Yes',
          ]),
        ],
      };

    default:
      throw new Error(`Unknown action: ${actionId}`);
  }
}

function runCommandStep(step) {
  return new Promise((resolve, reject) => {
    // Guard against attempting to spawn a directory (causes ENOTDIR).
    try {
      const resolvedCmd = path.resolve(step.filePath);
      if (fs.existsSync(resolvedCmd)) {
        const st = fs.statSync(resolvedCmd);
        if (st.isDirectory()) {
          return reject(new Error(`${step.label}: command path is a directory: ${resolvedCmd}`));
        }
      }
    } catch (_) {
      // best-effort guard only; fall through to spawn and let child handle errors
    }

    const child = spawn(step.filePath, step.args, {
      cwd: step.cwd || repoRoot,
      env: { ...getAugmentedEnv(), ...(step.env || {}) },
      windowsHide: true,
      detached: process.platform !== 'win32',
    });

    activeProcess = child;
    let settled = false;
    let timeoutId = null;

    const finish = (handler) => {
      if (settled) {
        return;
      }
      settled = true;
      if (timeoutId) {
        clearTimeout(timeoutId);
        timeoutId = null;
      }
      activeProcess = null;
      handler();
    };

    child.stdout.on('data', (chunk) => {
      sendLog('stdout', chunk.toString());
    });

    child.stderr.on('data', (chunk) => {
      sendLog('stderr', chunk.toString());
    });

    child.on('error', (error) => {
      finish(() => reject(error));
    });

    const onProcessEnd = (code, signal) => {
      if (code === 0) {
        finish(() => resolve());
        return;
      }

      const reason = signal ? `signal ${signal}` : `exit code ${code}`;
      finish(() => reject(new Error(`${step.label} failed with ${reason}.`)));
    };

    child.once('exit', onProcessEnd);
    child.once('close', onProcessEnd);

    const timeoutMs = Number(step.timeoutMs);
    if (Number.isFinite(timeoutMs) && timeoutMs > 0) {
      timeoutId = setTimeout(() => {
        sendLog('warn', `${step.label} took too long (${timeoutMs} ms). Stopping it.`);
        killProcessTree(child, 'SIGTERM');
        setTimeout(() => {
          killProcessTree(child, 'SIGKILL');
        }, 1500);
        finish(() => reject(new Error(`${step.label} timed out after ${timeoutMs} ms.`)));
      }, timeoutMs);
    }
  });
}

function sendLog(level, text) {
  const window = BrowserWindow.getAllWindows()[0];
  if (!window) {
    return;
  }
  window.webContents.send('app:log', {
    level,
    text,
    timestamp: new Date().toISOString(),
  });
}

function sendStatus(data) {
  const window = BrowserWindow.getAllWindows()[0];
  if (!window) {
    return;
  }
  window.webContents.send('app:status', data);
}

function getLatestBackupArchivePath() {
  const backupDir = resolveBackupDir();
  if (!fs.existsSync(backupDir)) {
    return null;
  }

  try {
    const candidates = fs
      .readdirSync(backupDir)
      .filter((name) => /^(?:openclaw_backup_|openclaw-backup|reclaw_backup_).*\.(?:zip|tar\.gz(?:\.enc)?)$/i.test(name))
      .map((name) => {
        const archivePath = path.join(backupDir, name);
        const stat = fs.statSync(archivePath);
        return {
          archivePath,
          mtimeMs: stat.mtimeMs,
        };
      })
      .sort((left, right) => right.mtimeMs - left.mtimeMs);

    return candidates[0]?.archivePath || null;
  } catch (error) {
    return null;
  }
}

function checkGatewayStatus(options = {}) {
  const url = options.url || 'http://127.0.0.1:18789/healthz';
  const timeoutMs = Number.isFinite(options.timeoutMs) ? options.timeoutMs : 1800;

  return new Promise((resolve) => {
    const startedAt = Date.now();
    let settled = false;

    const finish = (result) => {
      if (settled) {
        return;
      }
      settled = true;
      resolve(result);
    };

    const req = http.get(url, (res) => {
      const statusCode = Number(res.statusCode || 0);
      let body = '';

      res.setEncoding('utf8');
      res.on('data', (chunk) => {
        if (body.length < 256) {
          body += chunk;
        }
      });

      res.on('end', () => {
        const latencyMs = Date.now() - startedAt;
        const running = statusCode >= 200 && statusCode < 300;
        finish({
          running,
          statusCode,
          latencyMs,
          url,
          body: body.trim().slice(0, 200),
          error: running ? null : `Gateway returned HTTP ${statusCode}`,
        });
      });
    });

    req.setTimeout(timeoutMs, () => {
      req.destroy(new Error(`Timed out after ${timeoutMs} ms`));
    });

    req.on('error', (error) => {
      finish({
        running: false,
        statusCode: null,
        latencyMs: Date.now() - startedAt,
        url,
        error: error.message || 'Gateway is unreachable.',
      });
    });
  });
}

function spawnOpenclawCommand(args, options = {}) {
  const env = { ...getAugmentedEnv(), ...(options.env || {}) };
  const spawnOptions = {
    env,
    shell: false,
    windowsHide: true,
    cwd: options.cwd || repoRoot,
  };

  if (process.platform === 'win32') {
    const comspec = process.env.ComSpec || 'cmd.exe';
    const escapedArgs = (args || []).map((arg) => {
      const value = String(arg);
      if (/\s|"/.test(value)) {
        return `"${value.replace(/"/g, '\\"')}"`;
      }
      return value;
    });
    return spawn(comspec, ['/d', '/s', '/c', `openclaw ${escapedArgs.join(' ')}`], spawnOptions);
  }

  return spawn('openclaw', args || [], spawnOptions);
}

function runOpenclawCommandWithLogs(label, args, options = {}) {
  const timeoutMs = Number.isFinite(options.timeoutMs) ? Number(options.timeoutMs) : 60000;

  return new Promise((resolve, reject) => {
    sendLog('info', `${label}: openclaw ${(args || []).join(' ')}`);

    let child;
    try {
      child = spawnOpenclawCommand(args, options);
    } catch (error) {
      reject(error);
      return;
    }

    let settled = false;
    const finish = (handler) => {
      if (settled) {
        return;
      }
      settled = true;
      if (timeoutId) {
        clearTimeout(timeoutId);
      }
      handler();
    };

    const timeoutId = timeoutMs > 0
      ? setTimeout(() => {
        sendLog('warn', `${label} timed out after ${timeoutMs} ms.`);
        try {
          killProcessTree(child, 'SIGTERM');
        } catch (_) {}
        finish(() => reject(new Error(`${label} timed out after ${timeoutMs} ms.`)));
      }, timeoutMs)
      : null;

    child.stdout.on('data', (chunk) => sendLog('stdout', chunk.toString()));
    child.stderr.on('data', (chunk) => sendLog('stderr', chunk.toString()));

    child.on('error', (error) => {
      finish(() => reject(error));
    });

    child.on('close', (code) => {
      if (code === 0) {
        finish(() => resolve({ ok: true, code: 0 }));
        return;
      }
      finish(() => reject(new Error(`${label} failed with exit code ${code}.`)));
    });
  });
}

async function waitForGatewayReady(timeoutMs = 35000, intervalMs = 1000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    // eslint-disable-next-line no-await-in-loop
    const status = await checkGatewayStatus({ timeoutMs: 1400 });
    if (status.running) {
      return status;
    }
    // eslint-disable-next-line no-await-in-loop
    await new Promise((resolve) => setTimeout(resolve, intervalMs));
  }
  return null;
}

function isPidRunning(pid) {
  if (!Number.isInteger(pid) || pid <= 0) {
    return false;
  }
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return Boolean(error && error.code === 'EPERM');
  }
}

function cleanupStaleGatewayLocks(homeDir) {
  const tmpRoot = path.join(process.env.TEMP || process.env.TMP || os.tmpdir(), 'openclaw');
  if (!fs.existsSync(tmpRoot)) {
    return { scanned: 0, removed: 0 };
  }

  let scanned = 0;
  let removed = 0;
  const targetConfigPath = homeDir ? path.join(homeDir, '.openclaw', 'openclaw.json').toLowerCase() : '';

  for (const name of fs.readdirSync(tmpRoot)) {
    if (!/^gateway.*\.lock$/i.test(name)) {
      continue;
    }
    scanned += 1;
    const lockPath = path.join(tmpRoot, name);

    try {
      const raw = fs.readFileSync(lockPath, 'utf8');
      const parsed = JSON.parse(raw);
      const lockConfig = String(parsed && parsed.configPath ? parsed.configPath : '').toLowerCase();
      if (targetConfigPath && lockConfig && lockConfig !== targetConfigPath) {
        continue;
      }

      const pid = Number(parsed && parsed.pid);
      if (isPidRunning(pid)) {
        continue;
      }

      fs.unlinkSync(lockPath);
      removed += 1;
    } catch (_) {
      // Corrupted lock files can be safely removed.
      try {
        fs.unlinkSync(lockPath);
        removed += 1;
      } catch (_) {
        // ignore
      }
    }
  }

  return { scanned, removed };
}

async function launchWindowsGatewayFallback() {
  if (process.platform !== 'win32') {
    return false;
  }

  const env = getAugmentedEnv();
  const homeDir = process.env.HOME || process.env.USERPROFILE || '';

  if (homeDir) {
    const gatewayCmdPath = path.join(homeDir, '.openclaw', 'gateway.cmd');
    if (fs.existsSync(gatewayCmdPath)) {
      try {
        const content = fs.readFileSync(gatewayCmdPath, 'utf8');
        const lines = content
          .split(/\r?\n/)
          .map((line) => String(line || '').trim())
          .filter(Boolean);

        const launchLine = lines
          .slice()
          .reverse()
          .find((line) => !/^@?echo\b/i.test(line) && !/^rem\b/i.test(line) && !/^set\s+/i.test(line));

        if (launchLine) {
          const match = launchLine.match(/^("[^"]+"|\S+)\s+"([^"]+)"(?:\s+(.*))?$/);
          if (match) {
            const exe = String(match[1]).replace(/^"|"$/g, '');
            const scriptPath = String(match[2]);
            const tail = String(match[3] || '').trim();
            const tailArgs = tail ? (tail.match(/"[^"]*"|\S+/g) || []).map((arg) => arg.replace(/^"|"$/g, '')) : [];

            const launcher = spawn(exe, [scriptPath, ...tailArgs], {
              env,
              shell: false,
              windowsHide: true,
              detached: true,
              stdio: 'ignore',
            });
            launcher.unref();
            sendLog('warn', `Gateway fallback launch attempted via parsed ${gatewayCmdPath}.`);
            return true;
          }
        }
      } catch (error) {
        sendLog('warn', `Parsed gateway.cmd launch failed: ${error.message}`);
      }
    }
  }

  // Prefer launching an unsupervised local gateway process for immediate dashboard
  // availability; this is resilient when the service/login item is installed but idle.
  try {
    const tmpRoot = path.join(process.env.TEMP || process.env.TMP || os.tmpdir(), 'openclaw');
    fs.mkdirSync(tmpRoot, { recursive: true });
    const runLogPath = path.join(tmpRoot, 'gateway-detached.log');
    const launchCmd = `openclaw gateway run --port 18789 > "${runLogPath}" 2>&1`;
    const comspec = process.env.ComSpec || 'cmd.exe';
    const launcher = spawn(comspec, ['/d', '/c', launchCmd], {
      env,
      shell: false,
      windowsHide: true,
      detached: true,
      stdio: 'ignore',
    });
    launcher.unref();
    sendLog('warn', 'Gateway fallback launch attempted via `openclaw gateway run --port 18789`.');
    return true;
  } catch (error) {
    sendLog('warn', `Gateway detached run launch failed: ${error.message}`);
  }

  if (!homeDir) {
    return false;
  }

  const gatewayCmdPath = path.join(homeDir, '.openclaw', 'gateway.cmd');
  if (!fs.existsSync(gatewayCmdPath)) {
    return false;
  }

  try {
    // Launch gateway.cmd directly with shell handling to avoid cmd /s quote parsing issues.
    const launcher = spawn(gatewayCmdPath, [], {
      env,
      shell: true,
      windowsHide: true,
      detached: true,
      stdio: 'ignore',
    });
    launcher.unref();
    sendLog('warn', `Gateway fallback launch attempted via ${gatewayCmdPath}`);
    return true;
  } catch (error) {
    sendLog('warn', `Gateway fallback launch failed: ${error.message}`);
    return false;
  }
}

async function ensureGatewayOnline(options = {}) {
  const installIfNeeded = options.installIfNeeded !== false;
  const timeoutMs = Number.isFinite(options.timeoutMs) ? Math.max(5000, Number(options.timeoutMs)) : 45000;
  const homeDir = process.env.HOME || process.env.USERPROFILE || '';
  const gatewayCmdPath = homeDir ? path.join(homeDir, '.openclaw', 'gateway.cmd') : '';
  const hasWindowsGatewayScript = process.platform === 'win32' && gatewayCmdPath && fs.existsSync(gatewayCmdPath);

  const initialStatus = await checkGatewayStatus({ timeoutMs: 1400 });
  if (initialStatus.running) {
    return { ok: true, alreadyRunning: true, status: initialStatus };
  }

  sendLog('info', 'Gateway is offline. Running automatic gateway setup/start.');

  if (process.platform === 'win32') {
    const cleanedBeforeStart = cleanupStaleGatewayLocks(homeDir);
    if (cleanedBeforeStart.removed > 0) {
      sendLog('warn', `Removed ${cleanedBeforeStart.removed} stale gateway lock file(s).`);
    }
  }

  if (installIfNeeded && (!hasWindowsGatewayScript || process.platform !== 'win32')) {
    try {
      await runOpenclawCommandWithLogs('Install gateway service', ['gateway', 'install'], { timeoutMs: 120000 });
    } catch (error) {
      // Non-fatal: install may report warnings when already configured.
      sendLog('warn', `Gateway install step reported an issue: ${error.message}`);
    }
  }

  if (process.platform === 'win32') {
    const fallbackLaunched = await launchWindowsGatewayFallback();
    if (!fallbackLaunched) {
      sendLog('warn', 'Windows gateway fallback launcher was unavailable. Trying openclaw gateway start.');
      try {
        await runOpenclawCommandWithLogs('Start gateway service', ['gateway', 'start'], { timeoutMs: 45000 });
      } catch (error) {
        sendLog('warn', `Gateway start step reported an issue: ${error.message}`);
      }
    }
  } else {
    try {
      await runOpenclawCommandWithLogs('Start gateway service', ['gateway', 'start'], { timeoutMs: 120000 });
    } catch (error) {
      sendLog('warn', `Gateway start step reported an issue: ${error.message}`);
    }
  }

  let readyStatus = await waitForGatewayReady(timeoutMs, 1000);
  if (readyStatus && readyStatus.running) {
    return { ok: true, started: true, status: readyStatus };
  }

  if (process.platform === 'win32') {
    const cleanedBeforeRetry = cleanupStaleGatewayLocks(homeDir);
    if (cleanedBeforeRetry.removed > 0) {
      sendLog('warn', `Removed ${cleanedBeforeRetry.removed} stale gateway lock file(s) before retry.`);
    }

    try {
      await runOpenclawCommandWithLogs('Start gateway service', ['gateway', 'start'], { timeoutMs: 45000 });
    } catch (error) {
      sendLog('warn', `Gateway service start retry reported an issue: ${error.message}`);
    }
    readyStatus = await waitForGatewayReady(20000, 1000);
    if (readyStatus && readyStatus.running) {
      return { ok: true, started: true, retry: 'service-start', status: readyStatus };
    }
  }

  const fallbackLaunched = await launchWindowsGatewayFallback();
  if (fallbackLaunched) {
    readyStatus = await waitForGatewayReady(30000, 1000);
    if (readyStatus && readyStatus.running) {
      return { ok: true, started: true, fallback: true, status: readyStatus };
    }
  }

  const finalStatus = readyStatus || await checkGatewayStatus({ timeoutMs: 1400 });
  return {
    ok: false,
    error: finalStatus?.error || 'Gateway did not become reachable on http://127.0.0.1:18789/healthz',
    status: finalStatus || null,
  };
}

function createWindow() {
  const iconPath = getWindowIconPath();
  const isWindows = process.platform === 'win32';
  const win = new BrowserWindow({
    width: isWindows ? 700 : 560,
    height: isWindows ? 780 : 680,
    minWidth: isWindows ? 600 : 520,
    minHeight: isWindows ? 700 : 620,
    autoHideMenuBar: true,
    backgroundColor: '#12141a',
    show: false,
    ...(iconPath ? { icon: iconPath } : {}),
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });

  win.loadFile(path.join(__dirname, 'renderer', 'index.html'));

  // Open all external links in the default browser, never inside Electron
  win.webContents.setWindowOpenHandler(({ url }) => {
    shell.openExternal(url);
    return { action: 'deny' };
  });
  win.webContents.on('will-navigate', (event, url) => {
    if (!url.startsWith('file://')) {
      event.preventDefault();
      shell.openExternal(url);
    }
  });

  win.once('ready-to-show', () => {
    win.show();
  });

  // Safety net for packaged builds where ready-to-show can occasionally not fire.
  setTimeout(() => {
    if (!win.isDestroyed() && !win.isVisible()) {
      win.show();
    }
  }, 2500);

  win.webContents.on('did-fail-load', () => {
    if (!win.isDestroyed() && !win.isVisible()) {
      win.show();
    }
  });

  return win;
}

app.on('second-instance', () => {
  const existingWindow = BrowserWindow.getAllWindows()[0];
  if (!existingWindow) {
    createWindow();
    return;
  }

  if (existingWindow.isMinimized()) {
    existingWindow.restore();
  }
  if (!existingWindow.isVisible()) {
    existingWindow.show();
  }
  existingWindow.focus();
});

// Kill any active child process when the app quits or the window is forcibly closed
app.on('before-quit', () => {
  if (activeProcess) {
    try { activeProcess.kill(); } catch (_) {}
    activeProcess = null;
  }
});

app.whenReady().then(() => {
  const iconPath = getWindowIconPath();
  if (process.platform === 'darwin' && iconPath && app.dock && typeof app.dock.setIcon === 'function') {
    app.dock.setIcon(iconPath);
  }

  createWindow();

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) {
      createWindow();
    }
  });
});

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') {
    app.quit();
  }
});

// ─── OpenClaw setup wizard IPC handlers ──────────────────────────────────────

/**
 * Electron .app bundles on macOS don't inherit the user's shell PATH.
 * Augment with the common locations where Homebrew/nvm/npm install binaries.
 */
function getAugmentedEnv() {
  const sanitizeSpawnEnv = (rawEnv) => {
    const safeEnv = {};
    for (const [key, value] of Object.entries(rawEnv || {})) {
      if (!key || key.includes('\0') || key.includes('=')) {
        continue;
      }
      if (value == null) {
        continue;
      }
      safeEnv[key] = String(value);
    }
    return safeEnv;
  };

  if (process.platform === 'win32') {
    const extraPaths = [
      process.env.APPDATA ? path.join(process.env.APPDATA, 'npm') : null,
      process.env.ProgramFiles ? path.join(process.env.ProgramFiles, 'nodejs') : null,
      process.env['ProgramFiles(x86)'] ? path.join(process.env['ProgramFiles(x86)'], 'nodejs') : null,
    ].filter(Boolean);

    const current = String(process.env.PATH || '');
    const merged = [...extraPaths, ...current.split(';').filter(Boolean)];
    const deduped = [];
    const seen = new Set();
    for (const item of merged) {
      const key = String(item).toLowerCase();
      if (seen.has(key)) {
        continue;
      }
      seen.add(key);
      deduped.push(item);
    }

    return sanitizeSpawnEnv({ ...process.env, PATH: deduped.join(';') });
  }
  const extraPaths = [
    '/usr/local/bin',
    '/opt/homebrew/bin',
    '/opt/homebrew/sbin',
    '/usr/bin',
    '/bin',
  ];
  const current = process.env.PATH || '';
  const merged = [...extraPaths, ...current.split(':')].filter(Boolean).join(':');
  return sanitizeSpawnEnv({ ...process.env, PATH: merged });
}

/**
 * Check whether `openclaw` is available in PATH.
 * Returns { installed: bool, version: string|null, path: string|null }
 */
ipcMain.handle('app:check-openclaw', async () => {
  // Test/demo mode: force wizard to show regardless of installation state
  if (process.env.RECLAW_FORCE_SETUP_WIZARD === '1') {
    return { installed: false, binaryInstalled: false, version: null, path: null, forced: true };
  }

  // Check if the openclaw binary is on PATH.
  // Windows gets a dedicated cmd/where probe because login shells are not relevant there.
  const { execFile, exec } = require('child_process');
  const binaryInstalled = await new Promise((resolve) => {
    const env = getAugmentedEnv();
    let settled = false;
    const done = (value) => {
      if (settled) return;
      settled = true;
      resolve(Boolean(value));
    };

    const runWindowsWhereFallback = () => {
      const comspec = process.env.ComSpec || 'cmd.exe';
      let whereProc;
      try {
        whereProc = spawn(comspec, ['/d', '/s', '/c', 'where openclaw'], {
          env,
          shell: false,
          windowsHide: true,
        });
      } catch (_) {
        done(false);
        return;
      }
      whereProc.on('close', (code) => done(code === 0));
      whereProc.on('error', () => done(false));
    };

    if (process.platform === 'win32') {
      const comspec = process.env.ComSpec || 'cmd.exe';
      let probe;
      try {
        probe = spawn(comspec, ['/d', '/s', '/c', 'openclaw --version'], {
          env,
          shell: false,
          windowsHide: true,
        });
      } catch (_) {
        runWindowsWhereFallback();
        return;
      }

      const timeoutId = setTimeout(() => {
        try {
          probe.kill();
        } catch (_) {}
        runWindowsWhereFallback();
      }, 7000);

      probe.on('close', (code) => {
        if (settled) return;
        clearTimeout(timeoutId);
        if (code === 0) {
          done(true);
          return;
        }
        runWindowsWhereFallback();
      });

      probe.on('error', () => {
        if (settled) return;
        clearTimeout(timeoutId);
        runWindowsWhereFallback();
      });
      return;
    }

    // Unix-like: first try a login shell (catches nvm/volta/custom PATH), then direct execFile.
    exec('openclaw --version', { env, timeout: 5000, shell: process.env.SHELL || '/bin/zsh' }, (err) => {
      if (settled) return;
      if (!err) {
        done(true);
        return;
      }
      let proc;
      try {
        proc = execFile('openclaw', ['--version'], { env, timeout: 5000 });
      } catch (_) {
        done(false);
        return;
      }
      proc.on('close', (code) => done(code === 0));
      proc.on('error', () => done(false));
    });
  });

  // Check for openclaw config file — if it exists, openclaw is configured and ready
  const homeDir = process.env.HOME || process.env.USERPROFILE || '';
  if (!homeDir) {
    return { installed: false, binaryInstalled, version: null, path: null, error: 'Cannot determine home directory.' };
  }
  const openclawConfigPath = path.join(homeDir, '.openclaw', 'openclaw.json');
  try {
    const configured = fs.existsSync(openclawConfigPath);
    return { installed: configured, binaryInstalled, version: null, path: configured ? openclawConfigPath : null };
  } catch (err) {
    return { installed: false, binaryInstalled, version: null, path: null, error: err.message };
  }
});

/**
 * Install OpenClaw globally via npm.
 * Streams stdout/stderr to the renderer via sendLog.
 * Resolves with { ok: bool, error: string|null }.
 */
ipcMain.handle('app:install-openclaw', async () => {
  return new Promise((resolve) => {
    sendLog('info', 'Installing OpenClaw via npm install -g openclaw@latest …');
    const installEnv = { ...getAugmentedEnv(), SHARP_IGNORE_GLOBAL_LIBVIPS: '1' };
    const installArgs = ['install', '-g', 'openclaw@latest', '--no-fund', '--no-audit', '--loglevel=error'];
    const spawnOptions = {
      env: installEnv,
      shell: false,
      windowsHide: true,
    };

    let proc;
    try {
      if (process.platform === 'win32') {
        const comspec = process.env.ComSpec || 'cmd.exe';
        proc = spawn(comspec, ['/d', '/s', '/c', `npm ${installArgs.join(' ')}`], spawnOptions);
      } else {
        proc = spawn('npm', installArgs, spawnOptions);
      }
    } catch (error) {
      sendLog('error', `Failed to launch installer process: ${error.message}`);
      resolve({ ok: false, error: error.message });
      return;
    }

    let settled = false;
    const finish = (result) => {
      if (settled) return;
      settled = true;
      if (timeoutId) clearTimeout(timeoutId);
      resolve(result);
    };

    // Safety timeout — package downloads can be slow on Windows/npm registry mirrors.
    const timeoutId = setTimeout(() => {
      sendLog('error', 'npm install timed out after 8 minutes. Check your internet connection and try again.');
      try { proc.kill(); } catch (_) {}
      finish({ ok: false, error: 'npm install timed out after 8 minutes.' });
    }, 480000);

    proc.stdout.on('data', (chunk) => sendLog('stdout', chunk.toString()));
    proc.stderr.on('data', (chunk) => sendLog('stderr', chunk.toString()));

    proc.on('close', (code) => {
      if (code === 0) {
        sendLog('success', 'OpenClaw installed successfully.');
        finish({ ok: true, error: null });
      } else {
        sendLog('error', `npm install exited with code ${code}.`);
        finish({ ok: false, error: `npm install exited with code ${code}` });
      }
    });

    proc.on('error', (err) => {
      if (err.code === 'ENOENT') {
        const msg = 'npm was not found. Make sure Node.js is installed: https://nodejs.org';
        sendLog('error', msg);
        finish({ ok: false, error: msg });
      } else {
        sendLog('error', `Failed to launch npm: ${err.message}`);
        finish({ ok: false, error: err.message });
      }
    });
  });
});

/**
 * Open an interactive terminal so the user can run `openclaw onboard`.
 * Onboarding requires user interaction (TUI + browser OAuth) so it cannot
 * be driven non-interactively from the app.
 */
ipcMain.handle('app:onboard-openclaw', async () => {
  const { execFile } = require('child_process');
  const cmd = 'openclaw onboard --install-daemon';

  try {
    if (process.platform === 'darwin') {
      // Open Terminal.app with the onboard command; exit when done so the window closes.
      await new Promise((resolve, reject) => {
        execFile('osascript', [
          '-e', `tell application "Terminal" to do script "${cmd}"`,
          '-e', 'tell application "Terminal" to activate',
        ], (err) => (err ? reject(err) : resolve()));
      });
      sendLog('info', 'Opened Terminal.app for interactive onboarding.');
    } else if (process.platform === 'win32') {
      const winProc = spawn('cmd.exe', ['/c', 'start', 'cmd.exe', '/k', cmd], { detached: true, shell: false });
      winProc.on('error', () => {});
      sendLog('info', 'Opened Command Prompt for interactive onboarding.');
    } else {
      // Linux — try common terminal emulators in order
      const terms = ['gnome-terminal', 'xterm', 'konsole', 'xfce4-terminal'];
      let launched = false;
      for (const term of terms) {
        try {
          const termProc = spawn(term, term === 'gnome-terminal' ? ['--', 'bash', '-c', `${cmd}; exec bash`] : ['-e', cmd],
            { detached: true, shell: false });
          termProc.on('error', () => {});
          sendLog('info', `Opened ${term} for interactive onboarding.`);
          launched = true;
          break;
        } catch (_) { /* try next */ }
      }
      if (!launched) {
        sendLog('warn', 'Could not open a terminal automatically. Please run `openclaw onboard` manually.');
      }
    }
  } catch (err) {
    sendLog('warn', `Could not open terminal automatically: ${err.message}. Please run \`openclaw onboard\` manually.`);
  }

  // Always return needsManual — the user must complete onboarding in the terminal.
  return { ok: false, needsManual: true };
});

/**
 * Restore a backup archive during the setup wizard.
 * Streams output to the renderer log.
 */
ipcMain.handle('app:wizard-restore', async (_event, { archivePath, password }) => {
  const cliPath = path.join(repoRoot, 'bin', 'cli.js');
  const args = ['restore'];
  if (password) args.push('--password', password);
  if (archivePath) args.push(archivePath);

  return new Promise((resolve) => {
    sendLog('info', `Restoring backup${archivePath ? `: ${archivePath}` : ' (latest)'}…`);
    let proc = null;
    let settled = false;
    let tid = null;
    const finish = (result) => {
      if (settled) return;
      settled = true;
      if (tid) clearTimeout(tid);
      resolve(result);
    };

    try {
      const wizardEnv = { ...getAugmentedEnv(), ELECTRON_RUN_AS_NODE: '1' };
      if (app.isPackaged) {
        const sep = process.platform === 'win32' ? ';' : ':';
        const asarNodeModules = path.join(process.resourcesPath, 'app.asar', 'node_modules');
        wizardEnv.NODE_PATH = wizardEnv.NODE_PATH ? `${asarNodeModules}${sep}${wizardEnv.NODE_PATH}` : asarNodeModules;
      }
      proc = spawn(nodeExecutable, [cliPath, ...args], {
        env: wizardEnv,
        cwd: repoRoot,
        shell: false,
      });
    } catch (err) {
      sendLog('warn', `Restore failed to spawn: ${err.message}`);
      finish({ ok: false, error: err.message });
      return;
    }

    tid = setTimeout(() => {
      try { if (proc) proc.kill(); } catch (_) {}
      sendLog('warn', 'Restore timed out.');
      finish({ ok: false, error: 'Restore timed out.' });
    }, 120000);

    proc.stdout.on('data', (chunk) => sendLog('stdout', chunk.toString()));
    proc.stderr.on('data', (chunk) => sendLog('stderr', chunk.toString()));
    proc.on('close', (code) => {
      if (code === 0) {
        sendLog('success', 'Restore complete.');
        finish({ ok: true });
      } else {
        sendLog('warn', `Restore exited with code ${code}.`);
        finish({ ok: false, error: `exit ${code}` });
      }
    });
    proc.on('error', (err) => {
      sendLog('warn', `Restore failed: ${err.message}`);
      finish({ ok: false, error: err.message });
    });
  });
});

// ─── Existing context / action handlers ──────────────────────────────────────

ipcMain.handle('app:get-context', async () => {
  const profile = getProfile();
  return {
    profile,
    osLabel: process.platform,
    actions: getActions(profile),
    running: Boolean(activeProcess),
    activeAction,
  };
});

ipcMain.handle('app:get-gateway-status', async () => {
  return checkGatewayStatus();
});

ipcMain.handle('app:ensure-gateway-online', async (_, options = {}) => {
  try {
    return await ensureGatewayOnline(options || {});
  } catch (error) {
    return { ok: false, error: error.message || 'Failed to ensure gateway availability.' };
  }
});

ipcMain.handle('app:pick-archive', async () => {
  const result = await dialog.showOpenDialog({
    properties: ['openFile'],
    filters: [{ name: 'Backup Archives', extensions: ['zip', 'tar', 'gz', 'tgz', 'enc'] }],
  });
  if (result.canceled || result.filePaths.length === 0) {
    return null;
  }
  return result.filePaths[0];
});

ipcMain.handle('app:check-archive-encrypted', async (_event, archivePath) => {
  try {
    const BackupService = require('../lib/index');
    const svc = new BackupService({});
    return { encrypted: svc.isArchiveEncrypted(archivePath) };
  } catch (_) {
    return { encrypted: false };
  }
});

ipcMain.handle('app:stop-action', async () => {
  if (!activeProcess) {
    return false;
  }

  try {
    killProcessTree(activeProcess, 'SIGTERM');
    sendLog('warn', 'Stop requested. Sent termination signal to active process.');
    setTimeout(() => {
      if (activeProcess && activeProcess.pid) {
        killProcessTree(activeProcess, 'SIGKILL');
      }
    }, 1500);
    return true;
  } catch (error) {
    sendLog('error', `Failed to stop process: ${error.message}`);
    return false;
  }
});

ipcMain.handle('app:run-action', async (_, payload) => {
  if (activeProcess) {
    throw new Error('Another action is still running. Stop it or wait for completion.');
  }

  const profile = getProfile();
  const plan = buildActionPlan(payload.actionId, payload, profile);
  const steps = Array.isArray(plan.steps) ? plan.steps : [];
  if (steps.length === 0) {
    throw new Error('No command steps generated for this action.');
  }

  sendLog('info', `Starting action: ${payload.actionId}`);
  sendStatus({
    running: true,
    actionId: payload.actionId,
    archivePath: payload.archivePath || null,
  });

  activeAction = payload.actionId;
  const completionStatus = {
    running: false,
    actionId: null,
    completedActionId: payload.actionId,
    ok: false,
    backupPath: null,
    restoreSource: null,
    error: null,
  };

  try {
    for (let i = 0; i < steps.length; i += 1) {
      const step = steps[i];
      sendLog('info', `[${i + 1}/${steps.length}] ${step.label}`);
      sendLog('info', `Running: ${formatCommand(step.filePath, step.args)}`);
      await runCommandStep(step);
    }

    const latestBackupPath = getLatestBackupArchivePath();
    const ocBackupActions = new Set([
      'oc-backup-create',
      'oc-backup-create-verify',
      'oc-backup-create-only-config',
      'oc-backup-create-no-workspace',
    ]);

    if ((payload.actionId === 'backup' || ocBackupActions.has(payload.actionId)) && latestBackupPath) {
      completionStatus.backupPath = latestBackupPath;
      sendLog('success', `Backup saved to: ${latestBackupPath}`);
    }

    if (payload.actionId === 'restore-archive' && payload.archivePath) {
      completionStatus.restoreSource = payload.archivePath;
      sendLog('info', `Restore source: ${payload.archivePath}`);
    }

    if (payload.actionId === 'restore-latest') {
      completionStatus.restoreSource = latestBackupPath || 'Latest backup in ReClaw backups folder';
      sendLog('info', `Restore source: ${completionStatus.restoreSource}`);
    }

    completionStatus.ok = true;
    sendLog('success', 'Action completed successfully.');
    return {
      ok: true,
      backupPath: completionStatus.backupPath,
      restoreSource: completionStatus.restoreSource,
    };
  } catch (error) {
    completionStatus.error = error.message;
    throw error;
  } finally {
    activeProcess = null;
    activeAction = null;
    sendStatus(completionStatus);
  }
});
