const actionsGrid = document.getElementById('actionsGrid');
const actionSearchInput = document.getElementById('actionSearchInput');
const actionsMeta = document.getElementById('actionsMeta');
const actionStatus = document.getElementById('actionStatus');
const toast = document.getElementById('toast');
const logsOutput = document.getElementById('logsOutput');
const clearLogsBtn = document.getElementById('clearLogsBtn');
const stopBtn = document.getElementById('stopBtn');
const runState = document.getElementById('runState');
const backupPathHint = document.getElementById('backupPathHint');
const restorePathHint = document.getElementById('restorePathHint');
const guidanceHint = document.getElementById('guidanceHint');
const gatewayStatusBadge = document.getElementById('gatewayStatusBadge');
const gatewayStatusDetail = document.getElementById('gatewayStatusDetail');
const gatewayRefreshBtn = document.getElementById('gatewayRefreshBtn');
const modalBackdrop = document.getElementById('modalBackdrop');
const passwordModal = document.getElementById('passwordModal');
const passwordModalTitle = document.getElementById('passwordModalTitle');
const passwordModalText = document.getElementById('passwordModalText');
const passwordModalInput = document.getElementById('passwordModalInput');
const passwordRememberToggle = document.getElementById('passwordRememberToggle');
const passwordModalError = document.getElementById('passwordModalError');
const passwordModalCancelBtn = document.getElementById('passwordModalCancelBtn');
const passwordModalConfirmBtn = document.getElementById('passwordModalConfirmBtn');
const confirmModal = document.getElementById('confirmModal');
const confirmModalText = document.getElementById('confirmModalText');
const confirmModalCancelBtn = document.getElementById('confirmModalCancelBtn');
const confirmModalConfirmBtn = document.getElementById('confirmModalConfirmBtn');

let currentContext = null;
let running = false;
let activeActionId = null;
const actionById = new Map();
let toastTimer = null;
const MAX_LOG_LINES = 80;
let logLines = [];
const RUN_SYNC_INTERVAL_MS = 2000;
const GATEWAY_STATUS_POLL_MS = 12000;
let runWatchdogId = null;
let runWatchdogBusy = false;
let gatewayPollId = null;
let gatewayPollBusy = false;
let allActions = [];
let actionLru = [];
let hasGatewayAutostart = false;

const GROUP_ORDER = {
  easy: 1,
  tools: 2,
  danger: 3,
};
const ACTION_LRU_STORAGE_KEY = 'reclaw.actions.lru.v1';
const ACTION_LRU_MAX = 200;
const PASSWORD_STORAGE_KEY = 'reclaw.saved-password.v1';
const ACTION_GATEWAY_START = ['oc', 'gateway', 'start'].join('-');
const ACTION_GATEWAY_RESTART = ['oc', 'gateway', 'restart'].join('-');
const ACTION_GATEWAY_INSTALL = ['oc', 'gateway', 'install'].join('-');
const ACTION_GATEWAY_INSTALL_START = ['oc', 'gateway', 'install-start'].join('-');
const ACTION_GATEWAY_STATUS = ['oc', 'gateway', 'status'].join('-');
const ACTION_GATEWAY_KILL = ['oc', 'gateway', 'kill'].join('-');
const ACTION_GATEWAY_DISABLE_AUTOSTART = ['oc', 'gateway', 'disable-autostart'].join('-');
const ACTION_RESET = 'reset';
const ACTION_NUKE = 'nuke';

const PINNED_ACTIONS = [
  'oc-gateway-install-start',
  'restore-archive',
  'backup',
  'oc-gateway-kill',
  'dashboard-open',
];
const PINNED_LOOKUP = new Map(PINNED_ACTIONS.map((id, index) => [id, index]));

const isWindowsPlatform =
  typeof navigator !== 'undefined' && /win/i.test(`${navigator.platform || ''} ${navigator.userAgent || ''}`);
if (isWindowsPlatform) {
  document.documentElement.classList.add('platform-win');
}

async function refreshGatewayAutostartFlag() {
  try {
    const result = await window.clawDesktop.getGatewayAutostart();
    hasGatewayAutostart = Boolean(result && result.present);
  } catch (_) {
    hasGatewayAutostart = false;
  }
}

function loadSavedPassword() {
  try {
    return String(window.localStorage.getItem(PASSWORD_STORAGE_KEY) || '');
  } catch (_) {
    return '';
  }
}

function savePassword(value) {
  try {
    window.localStorage.setItem(PASSWORD_STORAGE_KEY, String(value || ''));
  } catch (_) {
    // best effort only
  }
}

function clearSavedPassword() {
  try {
    window.localStorage.removeItem(PASSWORD_STORAGE_KEY);
  } catch (_) {
    // best effort only
  }
}

function loadActionLru() {
  try {
    const raw = window.localStorage.getItem(ACTION_LRU_STORAGE_KEY);
    if (!raw) {
      return [];
    }

    const parsed = JSON.parse(raw);
    if (!Array.isArray(parsed)) {
      return [];
    }

    return parsed
      .filter((entry) => typeof entry === 'string' && entry.trim().length > 0)
      .slice(0, ACTION_LRU_MAX);
  } catch (_) {
    return [];
  }
}

function saveActionLru() {
  try {
    window.localStorage.setItem(
      ACTION_LRU_STORAGE_KEY,
      JSON.stringify(actionLru.slice(0, ACTION_LRU_MAX)),
    );
  } catch (_) {
    // best effort only
  }
}

function touchActionLru(actionId) {
  if (!actionId) {
    return;
  }

  const next = [actionId, ...actionLru.filter((entry) => entry !== actionId)]
    .slice(0, ACTION_LRU_MAX);

  actionLru = next;
  saveActionLru();
}

function sortActions(actions) {
  const lruIndex = new Map(actionLru.map((id, index) => [id, index]));

  return [...actions].sort((left, right) => {
    const leftPinned = PINNED_LOOKUP.has(left.id) ? PINNED_LOOKUP.get(left.id) : Number.MAX_SAFE_INTEGER;
    const rightPinned = PINNED_LOOKUP.has(right.id) ? PINNED_LOOKUP.get(right.id) : Number.MAX_SAFE_INTEGER;
    if (leftPinned !== rightPinned) {
      return leftPinned - rightPinned;
    }

    const leftPriority = lruIndex.has(left.id)
      ? lruIndex.get(left.id)
      : Number.MAX_SAFE_INTEGER;
    const rightPriority = lruIndex.has(right.id)
      ? lruIndex.get(right.id)
      : Number.MAX_SAFE_INTEGER;

    if (leftPriority !== rightPriority) {
      return leftPriority - rightPriority;
    }

    const leftRank = GROUP_ORDER[left.group] || 2;
    const rightRank = GROUP_ORDER[right.group] || 2;
    if (leftRank !== rightRank) {
      return leftRank - rightRank;
    }
    return left.label.localeCompare(right.label);
  });
}

function normalizeSearchQuery(value) {
  return String(value || '').trim().toLowerCase();
}

const guidanceState = {
  hint: 'Ready. If gateway is Offline: Install OpenClaw CLI → “OC Gateway Install + Start”. Flashing console? Disable Autostart → Kill → Install + Start, or use “OC Gateway Run (No Autostart)”.',
  tone: 'info',
  recommendedActionId: null,
  lastGateway: null,
};
let gatewayOfflineCount = 0;

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function setGuidanceHint(text, mode = 'info') {
  if (!guidanceHint) return;
  guidanceHint.textContent = text;
  guidanceHint.classList.remove('warn', 'error');
  if (mode === 'warn') guidanceHint.classList.add('warn');
  if (mode === 'error') guidanceHint.classList.add('error');
  guidanceState.hint = text;
  guidanceState.tone = mode;
  if (gatewayStatusBadge) {
    gatewayStatusBadge.title = text;
  }
}

function setRecommendedAction(actionId, reason) {
  guidanceState.recommendedActionId = actionId || null;
  const cards = document.querySelectorAll('button.action-card');
  cards.forEach((card) => {
    card.classList.remove('recommended');
    card.removeAttribute('title');
  });
  if (!actionId) return;
  const target = document.querySelector(`button.action-card[data-action-id="${actionId}"]`);
  if (target) {
    target.classList.add('recommended');
    target.title = reason || 'Recommended next step';
  }
}

function setActionsMeta(total, shown, query) {
  if (!actionsMeta) {
    return;
  }

  if (total === 0) {
    actionsMeta.textContent = 'No actions available.';
    return;
  }

  if (query && shown === 0) {
    actionsMeta.textContent = `No matches for "${query}".`;
    return;
  }

  if (shown < total) {
    actionsMeta.textContent = `Showing ${shown} of ${total}. Refine search to find more.`;
    return;
  }

  actionsMeta.textContent = `Showing ${shown} action${shown === 1 ? '' : 's'}.`;
}

function renderVisibleActions() {
  const sorted = sortActions(allActions);
  const query = normalizeSearchQuery(actionSearchInput?.value);

  const filtered = !query
    ? sorted
    : sorted.filter((action) => {
        const haystack = `${action.id} ${action.label} ${action.description} ${action.group}`.toLowerCase();
        return haystack.includes(query);
      });

  const visible = filtered;

  actionsGrid.innerHTML = '';
  if (visible.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'actions-empty';
    empty.textContent = 'No commands match your search.';
    actionsGrid.appendChild(empty);
  } else {
    visible.forEach((action) => renderActionCard(actionsGrid, action));
  }

  setActionsMeta(filtered.length, visible.length, query);
  setActiveAction(activeActionId);
  if (guidanceState.recommendedActionId) {
    setRecommendedAction(guidanceState.recommendedActionId, guidanceState.hint);
  }
}

function stopRunWatchdog() {
  if (runWatchdogId) {
    window.clearInterval(runWatchdogId);
    runWatchdogId = null;
  }
  runWatchdogBusy = false;
}

function startRunWatchdog() {
  stopRunWatchdog();
  runWatchdogId = window.setInterval(async () => {
    if (!running || runWatchdogBusy) {
      return;
    }

    runWatchdogBusy = true;
    try {
      const context = await window.clawDesktop.getContext();
      if (!running) {
        return;
      }

      if (!context?.running) {
        appendLog('Sync: backend reports action complete. Controls re-enabled.', 'warn');
        setRunningState(false);
        setActiveAction(null);
        setActionStatus('Done. Choose another action.', 'idle');
        showToast('Action finished. Controls unlocked.', 'info');
        stopRunWatchdog();
        return;
      }

      if (context.activeAction && context.activeAction !== activeActionId) {
        setActiveAction(context.activeAction);
      }
    } catch (_) {
      // ignore intermittent IPC sync errors
    } finally {
      runWatchdogBusy = false;
    }
  }, RUN_SYNC_INTERVAL_MS);
}

function appendLog(line, level = 'info') {
  const stickToBottom = logsOutput
    ? logsOutput.scrollHeight - (logsOutput.scrollTop + logsOutput.clientHeight) < 20
    : true;

  const ts = new Date().toLocaleTimeString();
  const prefix =
    level === 'stderr' || level === 'error'
      ? 'ERR'
      : level === 'success'
        ? 'OK '
        : level === 'warn'
          ? 'WRN'
          : 'LOG';
  const lines = String(line || '')
    .split(/\r?\n/)
    .map((item) => item.trim())
    .filter(Boolean);

  if (lines.length === 0) {
    return;
  }

  lines.forEach((entry) => {
    logLines.push(`[${ts}] ${prefix} ${entry}`);
    const shortLine = entry.length > 120 ? `${entry.slice(0, 117)}...` : entry;
    setActionStatus(shortLine, running ? 'running' : 'idle');
    syncPathHintsFromLog(entry);

  const gatewayIsRunning = guidanceState.lastGateway === 'running';

    if (running && activeActionId === 'install-openclaw-cli' && /installing openclaw cli/i.test(entry)) {
      setGuidanceHint('Installing CLI… once it finishes, run “OC Gateway Install + Start”.', 'info');
      setRecommendedAction(null, null);
    } else if (/npm was not found/i.test(entry)) {
      setGuidanceHint('npm/Node.js missing. Install Node.js, then run “Install OpenClaw CLI” and retry.', 'warn');
      setRecommendedAction('install-openclaw-cli', 'Install OpenClaw CLI');
    } else if (/spawn EINVAL/i.test(entry)) {
      setGuidanceHint('Spawn error (EINVAL). Run actions from PowerShell, reinstall Node.js/npm, then “Install OpenClaw CLI” → “OC Gateway Install + Start”.', 'warn');
      setRecommendedAction('install-openclaw-cli', 'Reinstall CLI from PowerShell');
    } else if (/spawn openclaw ENOENT/i.test(entry)) {
      setGuidanceHint('OpenClaw CLI not found. Run “Install OpenClaw CLI” then “OC Gateway Install + Start”.', 'warn');
      setRecommendedAction('install-openclaw-cli', 'Install OpenClaw CLI');
    } else if (/config file not found/i.test(entry) || /openclaw config not found/i.test(entry)) {
      setGuidanceHint('OpenClaw config missing. Run “Fresh Install (No Restore)” or restore a backup, then rerun gateway start.', 'warn');
      setRecommendedAction('fresh-install', 'Recreate OpenClaw config and gateway');
    } else if (/service is loaded but not running/i.test(entry) || /Runtime: stopped .*no listener detected/i.test(entry)) {
      setGuidanceHint('Gateway service exists but exits immediately. Disable Autostart, Kill Gateway Processes, then Install + Start.', 'warn');
      setRecommendedAction(ACTION_GATEWAY_DISABLE_AUTOSTART, 'Remove autostart/login task and reinstall gateway');
    } else if (/gateway service missing/i.test(entry) || /ECONNREFUSED 127\.0\.0\.1:18789/i.test(entry)) {
      setGuidanceHint('Gateway refused connection. If already installed, try “OC Gateway Start” or “OC Gateway Run (No Autostart)”. Otherwise “Kill Gateway Processes” → “OC Gateway Install + Start”, then Refresh.', 'warn');
      setRecommendedAction(ACTION_GATEWAY_KILL, 'Kill stuck gateway processes');
    } else if (/gateway service already registered/i.test(entry)) {
      setGuidanceHint('Gateway registered but offline. Run “OC Gateway Install + Start (force)” or “OC Gateway Run (No Autostart)”.', 'warn');
      setRecommendedAction(ACTION_GATEWAY_INSTALL_START, 'Reinstall + start gateway');
    } else if (/Gateway start \(detached\)/i.test(entry) || /Gateway run \(no login task\)/i.test(entry)) {
      setGuidanceHint('Prefer “OC Gateway Run (No Autostart)” to avoid flashing windows; it uses gateway run on port 18789.', 'info');
      setRecommendedAction('oc-gateway-run', 'Run gateway without login task');
    } else if (/gateway is offline\. start it from reclaw/i.test(entry)) {
      setGuidanceHint('Gateway offline. Run “OC Gateway Install + Start”, then Refresh, then Open Dashboard.', 'warn');
      setRecommendedAction(ACTION_GATEWAY_INSTALL_START, 'Install + Start gateway');
    } else if (/open dashboard in browser failed/i.test(entry) || /dashboard .*offline/i.test(entry)) {
      setGuidanceHint('Dashboard couldn’t open because gateway is offline. Run “OC Gateway Install + Start”, Refresh, then click Open Dashboard again.', 'warn');
      setRecommendedAction(ACTION_GATEWAY_INSTALL_START, 'Install + Start gateway');
    } else if (/dashboard url .*tokenized/i.test(entry) && guidanceState.lastGateway === 'offline') {
      setGuidanceHint('Gateway still offline. Run “OC Gateway Install + Start”, then Refresh, then Open Dashboard again.', 'warn');
      setRecommendedAction(ACTION_GATEWAY_INSTALL_START, 'Install + Start gateway');
    } else if (/config warnings/i.test(entry) && /plugin/i.test(entry)) {
      setGuidanceHint('Config warnings about plugins. Run “OC Fix Missing Plugins” then rerun your action.', 'warn');
      setRecommendedAction('oc-fix-missing-plugins', 'Fix missing plugins');
    } else if (/channels\.telegram/i.test(entry) && /allowFrom|groupAllowFrom|groupPolicy/i.test(entry)) {
      setGuidanceHint('Telegram config warning. Open your OpenClaw config and loosen group policy or allowFrom; then rerun.', 'warn');
      setRecommendedAction('oc-channels-probe', 'Probe channels status');
    } else if (/gateway is offline\. Start it from ReClaw/i.test(entry) || /Gateway offline\. Starting OpenClaw gateway/i.test(entry)) {
      setGuidanceHint('Gateway offline. Click “Install OpenClaw CLI” (if shown) then “OC Gateway Install + Start”, then Refresh.', 'warn');
      setRecommendedAction(ACTION_GATEWAY_INSTALL_START, 'Install + Start gateway');
    } else if (/global bin directory .* is not in PATH/i.test(entry) || /pnpm global/i.test(entry)) {
      setGuidanceHint('npm/pnpm global bin not in PATH. Run “Install OpenClaw CLI” from PowerShell to fix PATH + CLI.', 'warn');
      setRecommendedAction('install-openclaw-cli', 'Install OpenClaw CLI');
    } else if (!gatewayIsRunning && (/gateway.*offline/i.test(entry) || /gateway start failed/i.test(entry) || /gateway start step reported/i.test(entry))) {
      gatewayOfflineCount += 1;
      const canDisableAutostart = isWindowsPlatform && (hasGatewayAutostart || gatewayOfflineCount >= 2);
      if (gatewayOfflineCount >= 3 && canDisableAutostart) {
        setGuidanceHint('Gateway still offline. Disable autostart/login task, then Kill and Install + Start.', 'warn');
        setRecommendedAction(ACTION_GATEWAY_DISABLE_AUTOSTART, 'Remove scheduled task causing popup loops');
      } else {
        setGuidanceHint('Gateway offline. Run “OC Gateway Start” or openclaw gateway start.', 'warn');
        setRecommendedAction(ACTION_GATEWAY_START, 'Start gateway service');
      }
    } else if (/gateway .*started/i.test(entry) || /gateway restarted/i.test(entry)) {
      gatewayOfflineCount = 0;
      setGuidanceHint('Gateway running. Create a backup or run “OC Status Deep”.');
      setRecommendedAction('oc-backup-create', 'Create a backup now');
    } else if (/gateway is online/i.test(entry)) {
      gatewayOfflineCount = 0;
      setGuidanceHint('Gateway running. Create a backup or check status.', 'info');
      setRecommendedAction('oc-backup-create', 'Create a backup now');
    } else if (/login item: OpenClaw Gateway/i.test(entry) || /gateway\.cmd/i.test(entry)) {
      hasGatewayAutostart = true;
      setGuidanceHint('Gateway is tied to a Windows login task. Disable autostart, then Kill + Install + Start.', 'warn');
      setRecommendedAction(ACTION_GATEWAY_DISABLE_AUTOSTART, 'Disable autostart/login task');
    } else if (!gatewayIsRunning && (/gateway service missing/i.test(entry) || /ECONNREFUSED 127\.0\.0\.1:18789/i.test(entry) || /spawn EINVAL/i.test(entry) || /spawn openclaw ENOENT/i.test(entry))) {
      gatewayOfflineCount += 1;
      const canDisableAutostart = isWindowsPlatform && (hasGatewayAutostart || gatewayOfflineCount >= 2);
      if (/ENOENT|spawn openclaw/i.test(entry)) {
        setGuidanceHint('OpenClaw CLI not found. Run “Install OpenClaw CLI” then “OC Gateway Install + Start”.', 'warn');
        setRecommendedAction('install-openclaw-cli', 'Install OpenClaw CLI');
      } else if (/spawn EINVAL/i.test(entry)) {
        setGuidanceHint('Windows spawn EINVAL. Open ReClaw from PowerShell, then run Kill → Install + Start.', 'warn');
        setRecommendedAction(ACTION_GATEWAY_KILL, 'Kill stuck gateway processes');
      } else if (gatewayOfflineCount >= 3 && canDisableAutostart) {
        setGuidanceHint('Gateway failed repeatedly. Disable autostart/login task, then Kill and Install + Start.', 'warn');
        setRecommendedAction(ACTION_GATEWAY_DISABLE_AUTOSTART, 'Remove scheduled task causing popup loops');
      } else {
        setGuidanceHint('Gateway failed to start. If already installed, try “OC Gateway Start” or “Run (No Autostart)”. Otherwise “OC Gateway Kill” → “OC Gateway Install + Start”. If still offline, open gateway logs.', 'warn');
        setRecommendedAction(ACTION_GATEWAY_KILL, 'Kill stuck gateway processes');
      }
  } else if (/backup created successfully/i.test(entry) || /backup saved to/i.test(entry) || /backup complete/i.test(entry)) {
    setGuidanceHint('Backup finished. Optionally verify or store the archive safely.');
    setRecommendedAction('oc-backup-verify', 'Verify latest backup');
  } else if (/restore complete/i.test(entry) || /restore finished/i.test(entry)) {
    setGuidanceHint('Restore finished. Refresh gateway status or run “OC Gateway Restart”.');
    setRecommendedAction(ACTION_GATEWAY_RESTART, 'Restart gateway after restore');
  } else if (/npm install exited/i.test(entry) || /npm install timed out/i.test(entry)) {
    setGuidanceHint('CLI install failed. Open PowerShell and run npm install -g openclaw@latest, then retry.', 'warn');
    setRecommendedAction('install-openclaw-cli', 'Install OpenClaw CLI');
  } else if (/PowerShell npm install failed/i.test(entry)) {
    setGuidanceHint('CLI install failed in PowerShell. Open an elevated PowerShell and run npm install -g openclaw@latest.', 'warn');
    setRecommendedAction('install-openclaw-cli', 'Install OpenClaw CLI');
  } else if (/Installing OpenClaw CLI via npm install/i.test(entry)) {
    setGuidanceHint('Installing CLI… when it finishes, run “OC Gateway Install + Start.”', 'info');
    setRecommendedAction(null, null);
  } else if (/doctor .*completed|doctor --repair completed/i.test(entry)) {
    setGuidanceHint('Doctor completed. Next: run “OC Status Deep” or retry your action.');
    setRecommendedAction('oc-status-deep', 'Run status deep');
    } else if (/doctor .*failed|doctor reported issues/i.test(entry)) {
      setGuidanceHint('Doctor found issues. Rerun with --repair or inspect logs.', 'warn');
      setRecommendedAction('oc-doctor-repair', 'Run doctor repair');
    } else if (/nuke local openclaw/i.test(entry) || /reset .*complete/i.test(entry)) {
      setGuidanceHint('Reset/Nuke done. Next: Fresh install or restore a backup.', 'warn');
      setRecommendedAction('fresh-install', 'Fresh install OpenClaw');
    } else if (/openclaw cli installed/i.test(entry)) {
      setGuidanceHint('CLI installed. Now run “OC Gateway Install + Start”.', 'info');
      setRecommendedAction(ACTION_GATEWAY_INSTALL_START, 'Install and start gateway service');
  }
});

  if (logLines.length > MAX_LOG_LINES) {
    logLines = logLines.slice(-MAX_LOG_LINES);
  }

  logsOutput.textContent = logLines.join('\n');
  if (stickToBottom && logsOutput && typeof logsOutput.scrollTo === 'function') {
    logsOutput.scrollTo({ top: logsOutput.scrollHeight, behavior: 'smooth' });
  } else if (stickToBottom && logsOutput) {
    logsOutput.scrollTop = logsOutput.scrollHeight;
  }
}

function normalizePathText(value) {
  if (!value) {
    return '';
  }
  return String(value).trim().replace(/^['"]+|['"]+$/g, '');
}

function updatePathHint(element, value, emptyLabel) {
  if (!element) {
    return;
  }

  const normalized = normalizePathText(value);
  if (!normalized) {
    element.textContent = emptyLabel;
    element.title = '';
    element.classList.add('empty');
    return;
  }

  element.textContent = normalized;
  element.title = normalized;
  element.classList.remove('empty');
}

function setBackupPathHint(value) {
  updatePathHint(backupPathHint, value, 'No backup yet.');
}

function setRestorePathHint(value) {
  updatePathHint(restorePathHint, value, 'No restore yet.');
}

function setGatewayStatus(status) {
  if (!gatewayStatusBadge || !gatewayStatusDetail) {
    return;
  }

  if (running && activeActionId === 'install-openclaw-cli') {
    // Don't thrash hints while CLI install is running.
    setGuidanceHint('Installing CLI… ignore gateway status until install finishes.', 'warn');
    setRecommendedAction(null, null);
  }

  gatewayStatusBadge.classList.remove('running', 'stopped', 'unknown');

  if (!status || typeof status.running !== 'boolean') {
    gatewayStatusBadge.classList.add('unknown');
    gatewayStatusBadge.textContent = 'Unknown';
    gatewayStatusDetail.textContent = 'Gateway status unavailable.';
    setGuidanceHint('Gateway status unknown. Click Refresh or run “OC Gateway Status”.', 'warn');
    setRecommendedAction('oc-gateway-status', 'Check gateway status');
    guidanceState.lastGateway = 'unknown';
    gatewayOfflineCount = 0;
    return;
  }

  if (status.running) {
    gatewayOfflineCount = 0;
    gatewayStatusBadge.classList.add('running');
    gatewayStatusBadge.textContent = 'Running';
    const latency = Number.isFinite(status.latencyMs) ? `${status.latencyMs} ms` : 'latency n/a';
    const code = status.statusCode ? `HTTP ${status.statusCode}` : 'healthy';
    gatewayStatusDetail.textContent = `${code} • ${latency}`;
    setGuidanceHint('Gateway running. Next: create a backup or run “OC Status Deep”.');
    setRecommendedAction('oc-backup-create', 'Create a fresh backup now');
    guidanceState.lastGateway = 'running';
    return;
  }

  gatewayOfflineCount += 1;
  gatewayStatusBadge.classList.add('stopped');
  gatewayStatusBadge.textContent = 'Offline';
  gatewayStatusDetail.textContent = status.error || 'Gateway is not reachable.';
  const offlineError = status.error || '';
  const canDisableAutostart = isWindowsPlatform && hasGatewayAutostart;
  const refused = /ECONNREFUSED|EINVAL|missing/i.test(offlineError);
  const missingCli = /ENOENT|spawn openclaw/i.test(offlineError);
  const missingNpm = /npm was not found/i.test(offlineError);
  const portInUse = /EADDRINUSE|address already in use|port 18789/i.test(offlineError);
  const loginTaskLoop = /login item|scheduled task|autostart|startup-folder/i.test(offlineError);
  const guidanceWhenOffline = () => {
    if (missingCli) {
      setGuidanceHint('OpenClaw CLI missing. Run “Install OpenClaw CLI” then “OC Gateway Install + Start”.', 'warn');
      setRecommendedAction('install-openclaw-cli', 'Install OpenClaw CLI');
      return true;
    }
    if (missingNpm) {
      setGuidanceHint('npm/Node.js missing. Install Node.js, run “Install OpenClaw CLI”, then “OC Gateway Install + Start”.', 'warn');
      setRecommendedAction('install-openclaw-cli', 'Install OpenClaw CLI');
      return true;
    }
    if (portInUse) {
      setGuidanceHint('Port 18789 busy. Run “Kill Gateway Processes” then “OC Gateway Install + Start”, then Refresh.', 'warn');
      setRecommendedAction(ACTION_GATEWAY_KILL, 'Kill stuck gateway processes');
      return true;
    }
    if (loginTaskLoop && isWindowsPlatform) {
      setGuidanceHint('Gateway login task exists but listener is down. Disable Autostart, then “OC Gateway Run (No Autostart)” or “Install + Start”.', 'warn');
      setRecommendedAction('oc-gateway-run', 'Run gateway without login task');
      return true;
    }
    return false;
  };

  if ((gatewayOfflineCount >= 3 || hasGatewayAutostart) && canDisableAutostart) {
    setGuidanceHint('Gateway still offline. Disable Windows autostart/login task, then Kill and Install + Start again.', 'warn');
    setRecommendedAction(ACTION_GATEWAY_DISABLE_AUTOSTART, 'Remove scheduled task causing popup loops');
  } else if (guidanceWhenOffline()) {
    // hint handled above
  } else if (refused) {
    setGuidanceHint('Gateway refused connection. If already installed, try “OC Gateway Start” or “OC Gateway Run (No Autostart)”. Otherwise “Kill Gateway Processes” → “OC Gateway Install + Start”, then Refresh.', 'warn');
    setRecommendedAction(ACTION_GATEWAY_KILL, 'Kill stuck gateway processes');
  } else {
    setGuidanceHint('Gateway offline. Run “OC Gateway Install + Start” (force) then refresh. If it still fails, open logs.', 'warn');
    setRecommendedAction(ACTION_GATEWAY_INSTALL_START, 'Install and start gateway service');
  }
  guidanceState.lastGateway = 'offline';
}

async function refreshGatewayStatusWithRetry(attempts = 3, delayMs = 4000) {
  let last = null;
  for (let i = 0; i < attempts; i += 1) {
    last = await refreshGatewayStatus(true);
    if (last?.running) {
      return last;
    }
    if (i < attempts - 1) {
      await delay(delayMs);
    }
  }
  return last;
}

async function refreshGatewayStatus(force = false) {
  if ((!force && gatewayPollBusy) || !window.clawDesktop?.getGatewayStatus) {
    return null;
  }

  gatewayPollBusy = true;
  try {
    const status = await window.clawDesktop.getGatewayStatus();
    // If offline, add a short-lived hint tailored to the last error seen.
    if (!status?.running) {
      const err = (status && status.error ? String(status.error) : '').toLowerCase();
      if (/enoent|openclaw/.test(err)) {
        setGuidanceHint('OpenClaw CLI missing. Click “Install OpenClaw CLI” then “OC Gateway Install + Start”.', 'warn');
        setRecommendedAction('install-openclaw-cli', 'Install OpenClaw CLI');
      } else if (/econnrefused/.test(err)) {
        setGuidanceHint('Gateway refused connection. Run “Kill Gateway Processes” then “OC Gateway Install + Start”; if still offline, run “OC Gateway Run (No Autostart)” and Refresh.', 'warn');
        setRecommendedAction(ACTION_GATEWAY_KILL, 'Kill stuck gateway processes');
      } else if (/eaddrinuse|18789/.test(err)) {
        setGuidanceHint('Port 18789 is busy. Run “Kill Gateway Processes” then Install + Start.', 'warn');
        setRecommendedAction(ACTION_GATEWAY_KILL, 'Kill stuck gateway processes');
      } else if (/timeout/.test(err)) {
        setGuidanceHint('Gateway check timed out. Kill processes, Install + Start, then Refresh.', 'warn');
        setRecommendedAction(ACTION_GATEWAY_KILL, 'Kill stuck gateway processes');
      }
    }
    setGatewayStatus(status);
    return status;
  } catch (error) {
    const fallbackStatus = {
      running: false,
      error: error.message || 'Gateway status check failed.',
    };
    setGatewayStatus(fallbackStatus);
    return fallbackStatus;
  } finally {
    gatewayPollBusy = false;
  }
}

function startGatewayStatusPolling() {
  if (gatewayPollId) {
    window.clearInterval(gatewayPollId);
  }
  gatewayPollId = window.setInterval(() => {
    refreshGatewayStatus();
  }, GATEWAY_STATUS_POLL_MS);
}

function syncPathHintsFromLog(line) {
  const backupMatch =
    line.match(/Backup created successfully at:\s*(.+)$/i) ||
    line.match(/Backup saved to:?\s*(.+)$/i) ||
    line.match(/Backup archive:\s*(.+)$/i) ||
    line.match(/Created\s+(.+openclaw.*backup.*\.(?:zip|tar\.gz(?:\.enc)?))$/i);
  if (backupMatch) {
    setBackupPathHint(backupMatch[1]);
  }

  const restoreMatch = line.match(/Restore source:\s*(.+)$/i) || line.match(/Extracting:\s*(.+)$/i);
  if (restoreMatch) {
    setRestorePathHint(restoreMatch[1]);
  }
}

function setRunningState(isRunning) {
  running = isRunning;
  if (runState) {
    runState.textContent = isRunning ? 'Running' : 'Idle';
    runState.classList.toggle('running', isRunning);
    runState.classList.toggle('idle', !isRunning);
  }

  const buttons = document.querySelectorAll('button.action-card');
  buttons.forEach((button) => {
    button.disabled = false;
  });

  if (isRunning && activeActionId) {
    const activeButton = document.querySelector(`button.action-card[data-action-id="${activeActionId}"]`);
    if (activeButton) {
      activeButton.disabled = true;
    }
  }

  if (gatewayRefreshBtn) {
    gatewayRefreshBtn.disabled = isRunning;
  }
  stopBtn.disabled = !isRunning;

  if (isRunning && activeActionId) {
    setGuidanceHint(`Running: ${getActionLabel(activeActionId)}. Watch logs or hit Stop to cancel.`);
    setRecommendedAction(null, null);
  } else if (!isRunning) {
    setGuidanceHint('Idle. Pick an action or refresh gateway status.');
    if (guidanceState.lastGateway === 'running') {
      setRecommendedAction('oc-backup-create', 'Create a backup now');
    } else if (guidanceState.lastGateway === 'offline') {
      if (gatewayOfflineCount >= 3) {
        setRecommendedAction(ACTION_GATEWAY_DISABLE_AUTOSTART, 'Disable gateway autostart/login task');
      } else {
        setRecommendedAction(ACTION_GATEWAY_INSTALL_START, 'Install and start gateway service');
      }
    } else {
      setRecommendedAction(ACTION_GATEWAY_STATUS, 'Check gateway status');
    }
  }
}

function showToast(message, mode = 'info') {
  if (!toast) {
    return;
  }

  toast.hidden = false;
  toast.textContent = message;
  toast.classList.remove('info', 'warn', 'error', 'show');
  toast.classList.add(mode);

  window.requestAnimationFrame(() => {
    toast.classList.add('show');
  });

  if (toastTimer) {
    clearTimeout(toastTimer);
  }

  toastTimer = window.setTimeout(() => {
    toast.classList.remove('show');
    window.setTimeout(() => {
      toast.hidden = true;
    }, 180);
  }, 2300);
}

function setActionStatus(text, mode = 'idle') {
  if (!actionStatus) {
    return;
  }
  actionStatus.textContent = text;
  actionStatus.classList.remove('idle', 'running', 'error');
  actionStatus.classList.add(mode);
}

function getActionLabel(actionId) {
  return actionById.get(actionId)?.label || actionId || 'Action';
}

function setActiveAction(actionId) {
  activeActionId = actionId || null;
  const buttons = document.querySelectorAll('button.action-card');
  buttons.forEach((button) => {
    const runningOnThisCard = Boolean(actionId) && button.dataset.actionId === actionId;
    button.classList.toggle('is-running', runningOnThisCard);
    button.setAttribute('aria-busy', runningOnThisCard ? 'true' : 'false');
  });
}

function showModal(targetModal) {
  if (!modalBackdrop) {
    return;
  }

  modalBackdrop.hidden = false;
  if (passwordModal) {
    passwordModal.hidden = targetModal !== passwordModal;
  }
  if (confirmModal) {
    confirmModal.hidden = targetModal !== confirmModal;
  }
  if (backupChoiceModal) {
    backupChoiceModal.hidden = targetModal !== backupChoiceModal;
  }
}

function hideModals() {
  if (passwordModal)     passwordModal.hidden = true;
  if (confirmModal)      confirmModal.hidden = true;
  if (backupChoiceModal) backupChoiceModal.hidden = true;
  if (modalBackdrop)     modalBackdrop.hidden = true;
}

function requestActionPassword(label) {
  if (!modalBackdrop || !passwordModal || !passwordModalInput) {
    const value = window.prompt(`Password required for ${label}.`, '');
    const normalized = String(value || '').trim();
    return Promise.resolve(normalized || null);
  }

  return new Promise((resolve) => {
    if (passwordModalTitle) {
      passwordModalTitle.textContent = 'Password Required';
    }
    if (passwordModalText) {
      passwordModalText.textContent = `${label} requires your password.`;
    }
    if (passwordModalError) {
      passwordModalError.textContent = '';
    }
    const savedPassword = loadSavedPassword();
    passwordModalInput.value = savedPassword;
    if (passwordRememberToggle) {
      passwordRememberToggle.checked = Boolean(savedPassword);
    }

    showModal(passwordModal);
    window.requestAnimationFrame(() => {
      passwordModalInput.focus();
    });

    const finish = (value) => {
      cleanup();
      hideModals();
      resolve(value);
    };

    const onConfirm = () => {
      const value = passwordModalInput.value.trim();
      if (!value) {
        if (passwordModalError) {
          passwordModalError.textContent = 'Password is required.';
        }
        passwordModalInput.focus();
        return;
      }

      if (passwordRememberToggle && passwordRememberToggle.checked) {
        savePassword(value);
      } else {
        clearSavedPassword();
      }

      finish(value);
    };

    const onCancel = () => {
      finish(null);
    };

    const onKeyDown = (event) => {
      if (event.key === 'Enter') {
        event.preventDefault();
        onConfirm();
        return;
      }
      if (event.key === 'Escape') {
        event.preventDefault();
        onCancel();
      }
    };

    const onBackdropClick = (event) => {
      if (event.target === modalBackdrop) {
        onCancel();
      }
    };

    const cleanup = () => {
      if (passwordModalConfirmBtn) {
        passwordModalConfirmBtn.removeEventListener('click', onConfirm);
      }
      if (passwordModalCancelBtn) {
        passwordModalCancelBtn.removeEventListener('click', onCancel);
      }
      passwordModal.removeEventListener('keydown', onKeyDown);
      modalBackdrop.removeEventListener('click', onBackdropClick);
    };

    if (passwordModalConfirmBtn) {
      passwordModalConfirmBtn.addEventListener('click', onConfirm);
    }
    if (passwordModalCancelBtn) {
      passwordModalCancelBtn.addEventListener('click', onCancel);
    }
    passwordModal.addEventListener('keydown', onKeyDown);
    modalBackdrop.addEventListener('click', onBackdropClick);
  });
}

// Like requestActionPassword but password is optional — returns '' if user leaves it blank
function requestOptionalPassword(title, description) {
  if (!modalBackdrop || !passwordModal || !passwordModalInput) {
    const value = window.prompt(`${description}\n(Leave blank if no password)`, '');
    return Promise.resolve(value === null ? null : String(value || ''));
  }

  return new Promise((resolve) => {
    if (passwordModalTitle) passwordModalTitle.textContent = title;
    if (passwordModalText)  passwordModalText.textContent  = description;
    if (passwordModalError) passwordModalError.textContent = '';
    passwordModalInput.value       = '';
    passwordModalInput.placeholder = 'Enter password (leave blank if none)';
    if (passwordRememberToggle) passwordRememberToggle.checked = false;

    showModal(passwordModal);
    window.requestAnimationFrame(() => passwordModalInput.focus());

    const finish = (value) => { cleanup(); hideModals(); resolve(value); };

    const onConfirm     = () => finish(passwordModalInput.value.trim());
    const onCancel      = () => finish(null);
    const onKeyDown     = (e) => { if (e.key === 'Enter') onConfirm(); else if (e.key === 'Escape') onCancel(); };
    const onBackdropClick = (e) => { if (e.target === modalBackdrop) onCancel(); };

    const cleanup = () => {
      passwordModalConfirmBtn?.removeEventListener('click', onConfirm);
      passwordModalCancelBtn?.removeEventListener('click', onCancel);
      passwordModal.removeEventListener('keydown', onKeyDown);
      modalBackdrop.removeEventListener('click', onBackdropClick);
      passwordModalInput.placeholder = 'Enter password';
    };

    passwordModalConfirmBtn?.addEventListener('click', onConfirm);
    passwordModalCancelBtn?.addEventListener('click', onCancel);
    passwordModal.addEventListener('keydown', onKeyDown);
    modalBackdrop.addEventListener('click', onBackdropClick);
  });
}

function showBackupChoiceModal() {
  return new Promise((resolve) => {
    if (!modalBackdrop || !backupChoiceModal) {
      const choice = window.confirm('Do you have a backup to restore?\nOK = Restore  |  Cancel = Fresh setup');
      return resolve(choice ? 'restore' : 'fresh');
    }

    // Remove any stale listeners before re-opening
    const onRestore = () => { cleanup(); resolve('restore'); };
    const onFresh   = () => { cleanup(); resolve('fresh'); };

    const cleanup = () => {
      choiceRestoreBtn?.removeEventListener('click', onRestore);
      choiceFreshBtn?.removeEventListener('click', onFresh);
      hideModals();
    };

    choiceRestoreBtn?.addEventListener('click', onRestore);
    choiceFreshBtn?.addEventListener('click', onFresh);
    showModal(backupChoiceModal);
  });
}

function requestDangerConfirmation(label) {
  if (!modalBackdrop || !confirmModal) {
    return Promise.resolve(window.confirm(`Are you sure you want to ${label}?`));
  }

  return new Promise((resolve) => {
    if (confirmModalText) {
      confirmModalText.textContent = `Are you sure you want to ${label}?`;
    }

    showModal(confirmModal);
    window.requestAnimationFrame(() => {
      confirmModalConfirmBtn?.focus();
    });

    const finish = (value) => {
      cleanup();
      hideModals();
      resolve(value);
    };

    const onConfirm = () => finish(true);

    const onCancel = () => {
      finish(false);
    };

    const onKeyDown = (event) => {
      if (event.key === 'Enter') {
        event.preventDefault();
        onConfirm();
        return;
      }
      if (event.key === 'Escape') {
        event.preventDefault();
        onCancel();
      }
    };

    const onBackdropClick = (event) => {
      if (event.target === modalBackdrop) {
        onCancel();
      }
    };

    const cleanup = () => {
      if (confirmModalConfirmBtn) {
        confirmModalConfirmBtn.removeEventListener('click', onConfirm);
      }
      if (confirmModalCancelBtn) {
        confirmModalCancelBtn.removeEventListener('click', onCancel);
      }
      confirmModal.removeEventListener('keydown', onKeyDown);
      modalBackdrop.removeEventListener('click', onBackdropClick);
    };

    if (confirmModalConfirmBtn) {
      confirmModalConfirmBtn.addEventListener('click', onConfirm);
    }
    if (confirmModalCancelBtn) {
      confirmModalCancelBtn.addEventListener('click', onCancel);
    }
    confirmModal.addEventListener('keydown', onKeyDown);
    modalBackdrop.addEventListener('click', onBackdropClick);
  });
}

async function runAction(action) {
  if (running) {
    const activeLabel = getActionLabel(activeActionId);
    showToast(`${activeLabel} is already running. Wait or tap Stop.`, 'warn');
    setActionStatus(`Running: ${activeLabel}...`, 'running');
    return;
  }

  let selectedArchivePath = '';
  if (action.requiresArchive) {
    const pickedArchive = await window.clawDesktop.pickArchive();
    if (!pickedArchive) {
      appendLog(`Cancelled: ${action.label}`, 'warn');
      showToast(`Cancelled: ${action.label}`, 'warn');
      return;
    }
    selectedArchivePath = pickedArchive;
    setRestorePathHint(pickedArchive);
    appendLog(`Selected archive: ${pickedArchive}`);
  }

  let actionPassword = '';
  if (action.requiresPassword) {
    const value = await requestActionPassword(action.label);
    if (!value) {
      appendLog(`Cancelled: ${action.label}`, 'warn');
      showToast(`Cancelled: ${action.label}`, 'warn');
      return;
    }
    actionPassword = value;
  } else if (action.requiresArchive && selectedArchivePath) {
    // After archive is picked, check if it's encrypted
    try {
      const { encrypted } = await window.clawDesktop.checkArchiveEncrypted(selectedArchivePath);
      if (encrypted) {
        const value = await requestActionPassword('Restore From Archive (encrypted backup)');
        if (!value) {
          appendLog('Cancelled: password required for encrypted backup.', 'warn');
          showToast('Cancelled: password required.', 'warn');
          return;
        }
        actionPassword = value;
      }
      // If not encrypted: skip password prompt entirely
    } catch (_) {
      // If check fails, fall back to optional prompt
      const value = await requestOptionalPassword(
        'Password (Optional)',
        'If this backup is encrypted, enter the password. Leave blank if it has none.',
      );
      if (value === null) {
        appendLog(`Cancelled: ${action.label}`, 'warn');
        showToast(`Cancelled: ${action.label}`, 'warn');
        return;
      }
      actionPassword = value;
    }
  } else if (action.optionalPassword && !action.requiresArchive) {
    const value = await requestOptionalPassword(
      'Password (Optional)',
      `If this backup is encrypted, enter the password. Leave blank if it has none.`,
    );
    if (value === null) {
      appendLog(`Cancelled: ${action.label}`, 'warn');
      showToast(`Cancelled: ${action.label}`, 'warn');
      return;
    }
    actionPassword = value;
  }

  if (action.destructive) {
    const confirmed = await requestDangerConfirmation(action.label);
    if (!confirmed) {
      appendLog(`Cancelled: ${action.label}`, 'warn');
      showToast(`Cancelled: ${action.label}`, 'warn');
      return;
    }
  }

  try {
    touchActionLru(action.id);

  if (action.id === 'restore-latest') {
    setRestorePathHint('Latest backup from ReClaw backups folder');
  }

  setActiveAction(action.id);
  renderVisibleActions();
  setActionStatus(`Running: ${action.label}...`, 'running');
  setRunningState(true);
  startRunWatchdog();
  await window.clawDesktop.runAction({
    actionId: action.id,
    password: actionPassword,
    archivePath: selectedArchivePath,
  });
  } catch (error) {
    setActionStatus(`Failed: ${action.label}`, 'error');
    showToast(`Failed: ${action.label}`, 'error');
    appendLog(error.message || String(error), 'error');
    const msg = error.message || '';
    if (/PowerShell is required/i.test(msg)) {
    setGuidanceHint('PowerShell missing. Install it or run ReClaw from PowerShell.', 'warn');
    setRecommendedAction(null, null);
  } else if (/ENOENT|spawn openclaw/i.test(msg)) {
    setGuidanceHint('OpenClaw CLI not found. Run “Install OpenClaw CLI” then retry.', 'warn');
    setRecommendedAction('install-openclaw-cli', 'Install OpenClaw CLI');
  } else if (/EADDRINUSE|address already in use|port 18789/i.test(msg)) {
    setGuidanceHint('Gateway port 18789 busy. Run “Kill Gateway Processes” then “OC Gateway Install + Start”.', 'warn');
    setRecommendedAction(ACTION_GATEWAY_KILL, 'Kill stuck gateway processes');
  } else if (/npm was not found/i.test(msg)) {
    setGuidanceHint('npm/Node.js missing. Install Node.js, then run “Install OpenClaw CLI”.', 'warn');
    setRecommendedAction('install-openclaw-cli', 'Install OpenClaw CLI');
  } else if (/openclaw.*not found/i.test(msg)) {
    setGuidanceHint('OpenClaw CLI not found. Run “Clone OpenClaw” then “Install OpenClaw”.', 'warn');
      setRecommendedAction('clone-openclaw', 'Clone and install OpenClaw');
    } else if (/timed out|timeout/i.test(msg)) {
      setGuidanceHint('Action timed out. Check logs, then rerun or try “OC Doctor Repair”.', 'warn');
      setRecommendedAction('oc-doctor-repair', 'Run doctor repair');
    } else {
      setGuidanceHint('Action failed. Check logs, then rerun or try “OC Doctor Repair”.', 'warn');
      setRecommendedAction('oc-doctor-repair', 'Run doctor repair');
    }
  } finally {
    stopRunWatchdog();
    setRunningState(false);
    setActiveAction(null);
    if (action.id === 'backup' || action.id === 'oc-backup-create' || action.id === 'oc-backup-create-verify') {
      setGuidanceHint('Backup finished. Optionally verify or store the archive safely.');
      setRecommendedAction('oc-backup-verify', 'Verify latest backup');
    }
    if (action.id === 'restore-latest' || action.id === 'restore-archive') {
      setGuidanceHint('Restore finished. Restart the gateway or run status check.', 'warn');
      setRecommendedAction(ACTION_GATEWAY_RESTART, 'Restart gateway after restore');
    }
    if (action.id === 'install-openclaw-cli') {
      setGuidanceHint('CLI installed. Now run “OC Gateway Install + Start”.', 'info');
      setRecommendedAction(ACTION_GATEWAY_INSTALL_START, 'Install and start gateway service');
    }
    if (action.id === ACTION_GATEWAY_KILL) {
      setGuidanceHint('Gateway processes killed. Run “OC Gateway Install + Start” next.', 'warn');
      setRecommendedAction(ACTION_GATEWAY_INSTALL_START, 'Install and start gateway service');
    }
    if (action.id === ACTION_GATEWAY_DISABLE_AUTOSTART) {
      gatewayOfflineCount = 0;
      setGuidanceHint('Gateway autostart removed. Run Kill → Install + Start to bring it up clean.', 'warn');
      setRecommendedAction(ACTION_GATEWAY_INSTALL_START, 'Install and start gateway service');
    }
    if (action.id === ACTION_RESET || action.id === ACTION_NUKE) {
      setGuidanceHint('Reset complete. Reinstall and start the gateway: “OC Gateway Install + Start” (or “Run (No Autostart)” on Windows).', 'warn');
      setRecommendedAction(ACTION_GATEWAY_INSTALL_START, 'Install and start gateway service');
      await refreshGatewayStatusWithRetry(3, 5000);
    }
    if (
      action.id === ACTION_GATEWAY_START ||
      action.id === ACTION_GATEWAY_RESTART ||
      action.id === ACTION_GATEWAY_INSTALL ||
      action.id === ACTION_GATEWAY_INSTALL_START
    ) {
      setGuidanceHint('Gateway running. Create a backup or run “OC Status Deep”.');
      setRecommendedAction('oc-backup-create', 'Create a backup now');
      await refreshGatewayStatus(true);
    }
    if (action.id === 'oc-doctor-repair' || action.id === 'oc-doctor-fix' || action.id === 'oc-doctor-deep') {
      setGuidanceHint('Doctor complete. Run “OC Status Deep” or retry your action.');
      setRecommendedAction('oc-status-deep', 'Run status deep');
    }
  }
}

function renderActionCard(container, action) {
  const isPinned = PINNED_LOOKUP.has(action.id);
  const button = document.createElement('button');
  button.type = 'button';
  button.dataset.actionId = action.id;
  button.className = `action-card${action.destructive ? ' destructive' : ''}${isPinned ? ' pinned' : ''}`;
  button.innerHTML = `
    <span class="card-top">
      <span class="emoji">${action.emoji || '🧩'}</span>
      ${isPinned ? '<span class="pill-pinned" aria-label="Priority action">📌</span>' : ''}
    </span>
    <span class="title">${action.label}</span>
    <span class="desc">${action.description}</span>
  `;
  button.addEventListener('click', () => runAction(action));
  container.appendChild(button);
}

function renderActions(actions) {
  actionById.clear();
  allActions = Array.isArray(actions) ? [...actions] : [];
  allActions.forEach((action) => actionById.set(action.id, action));
  renderVisibleActions();
}

// ─── OpenClaw setup wizard ────────────────────────────────────────────────────

const openclawSetupWizard = document.getElementById('openclawSetupWizard');
const wizardInstallBtn    = document.getElementById('wizardInstallBtn');
const wizardRestoreBtn    = document.getElementById('wizardRestoreBtn');
const wizardSkipBtn       = document.getElementById('wizardSkipBtn');
const wizardLogs          = document.getElementById('wizardLogs');
const backupChoiceModal   = document.getElementById('backupChoiceModal');
const choiceRestoreBtn    = document.getElementById('choiceRestoreBtn');
const choiceFreshBtn      = document.getElementById('choiceFreshBtn');
let lastErrorToastAt = 0;
let lastErrorToastText = '';
let configErrorAlerted = false;

function maybeAlertConfigError(text) {
  if (configErrorAlerted) return false;
  if (!text) return false;
  const normalized = text.toLowerCase();
  if (
    normalized.includes('invalid config') ||
    normalized.includes('plugins.load.paths') ||
    normalized.includes('plugin path not found')
  ) {
    configErrorAlerted = true;
    const message =
      'OpenClaw config has missing plugin paths. Run "OC Fix Missing Plugins" or edit ~/.openclaw/openclaw.json.';
    alertUser(message, 'error');
    setActionStatus('OpenClaw config invalid (missing plugin path).', 'error');
    return true;
  }
  return false;
}

function wizardLog(text) {
  if (!wizardLogs) return;
  wizardLogs.textContent += text + '\n';
  wizardLogs.scrollTop = wizardLogs.scrollHeight;
}

function isNoisyInstallerLog(text) {
  const line = String(text || '').trim().toLowerCase();
  if (!line) return false;

  return (
    line.startsWith('npm http fetch') ||
    line.startsWith('npm timing') ||
    line.startsWith('npm sill') ||
    line.startsWith('npm verb') ||
    line.startsWith('npm notice') ||
    line.startsWith('npm warn')
  );
}

function alertUser(message, mode = 'error') {
  showToast(message, mode);
}

function setWizardStep(stepNum, state, statusText) {
  const el = document.getElementById(`wizardStep${stepNum}`);
  if (!el) return;
  el.classList.remove('active', 'done', 'error');
  if (state) el.classList.add(state);
  const statusEl = el.querySelector('.wizard-step-status');
  if (statusEl && statusText !== undefined) statusEl.textContent = statusText;
}

function showSetupWizard() {
  if (openclawSetupWizard) openclawSetupWizard.removeAttribute('hidden');
  document.querySelector('.app-shell')?.classList.add('wizard-active');
}

function hideSetupWizard() {
  if (openclawSetupWizard) openclawSetupWizard.setAttribute('hidden', '');
  document.querySelector('.app-shell')?.classList.remove('wizard-active');
}

function primeWizardForExistingOpenClawBinary() {
  setWizardStep(1, 'done', 'Already installed');
  setWizardStep(2, 'active', 'Choose…');
  setWizardStep(3, null, '');
  wizardLog('Step 1: openclaw already installed — skipping npm install.');

  const step2Label = document.getElementById('wizardStep2Label');
  if (step2Label) step2Label.textContent = 'Choose backup or fresh setup';

  const noteEl = document.querySelector('.wizard-note');
  if (noteEl) noteEl.hidden = true;

  if (wizardInstallBtn) {
    wizardInstallBtn.hidden = true;
    wizardInstallBtn.disabled = false;
    wizardInstallBtn.onclick = runSetupWizard;
  }
  if (wizardRestoreBtn) {
    wizardRestoreBtn.hidden = false;
    wizardRestoreBtn.disabled = false;
    wizardRestoreBtn.textContent = 'Choose backup or fresh';
    wizardRestoreBtn.onclick = runChoiceStep;
  }
  if (wizardSkipBtn) wizardSkipBtn.disabled = false;
  _wizardRunning = false;
}

async function runVerifyStep() {
  if (wizardInstallBtn) wizardInstallBtn.disabled = true;
  if (wizardRestoreBtn) wizardRestoreBtn.hidden = true;
  if (wizardSkipBtn) wizardSkipBtn.disabled = true;

  setWizardStep(3, 'active', 'Checking…');
  wizardLog('\nStep 3: Verifying OpenClaw config …');
  const check = await window.clawDesktop.checkOpenClaw();
  if (check.installed) {
    setWizardStep(3, 'done', 'Ready');
    wizardLog('OpenClaw config found. Ready to go!');
    showToast('OpenClaw installed! Loading ReClaw…', 'info');
    await new Promise((resolve) => window.setTimeout(resolve, 1200));
    hideSetupWizard();
    await initializeMainApp();
  } else {
    setWizardStep(3, 'error', 'Config missing');
    wizardLog(
      '\nOpenClaw config not found yet.\n' +
      'Make sure you completed all steps in the terminal (including browser sign-in).\n' +
      'Then click "Check Again", or click "Already installed — Skip" to continue manually.',
    );
    if (check.error) wizardLog(`Detail: ${check.error}`);
    alertUser('OpenClaw config not found yet. Complete setup and click Check Again.', 'warn');
    if (wizardInstallBtn) {
      wizardInstallBtn.textContent = 'Check Again';
      wizardInstallBtn.disabled = false;
      wizardInstallBtn.hidden = false;
      _wizardRunning = false;
      wizardInstallBtn.onclick = () => runVerifyStep();
    }
    if (wizardSkipBtn) wizardSkipBtn.disabled = false;
  }
}

async function runRestoreStep() {
  if (wizardInstallBtn) wizardInstallBtn.disabled = true;
  if (wizardRestoreBtn) wizardRestoreBtn.disabled = true;
  if (wizardSkipBtn) wizardSkipBtn.disabled = true;

  const reenableChoice = () => {
    if (wizardInstallBtn) wizardInstallBtn.disabled = false;
    if (wizardRestoreBtn) wizardRestoreBtn.disabled = false;
    if (wizardSkipBtn) wizardSkipBtn.disabled = false;
  };

  // Pick archive file
  const archivePath = await window.clawDesktop.pickArchive();
  if (!archivePath) { reenableChoice(); return; }

  wizardLog(`\nSelected archive: ${archivePath}`);

  // Ask for optional password (blank = no encryption)
  const password = await requestOptionalPassword(
    'Backup password',
    'Enter the password for this backup, or leave blank if it has no password.',
  );
  if (password === null) { reenableChoice(); return; } // user cancelled

  setWizardStep(2, 'active', 'Restoring…');
  wizardLog('\nRestoring backup…');

  const unsubLog = window.clawDesktop.onLog((data) => { wizardLog(data.text || ''); });
  let restoreResult;
  try {
    restoreResult = await window.clawDesktop.wizardRestore(archivePath, password);
  } finally {
    unsubLog();
  }

  if (!restoreResult.ok) {
    setWizardStep(2, 'error', 'Failed');
    wizardLog(`\nRestore failed: ${restoreResult.error || 'unknown error'}`);
    wizardLog('You can try again or click "No backup — Set up fresh".');
    alertUser(`Restore failed: ${restoreResult.error || 'unknown error'}`);
    if (wizardInstallBtn) wizardInstallBtn.disabled = false;
    if (wizardRestoreBtn) wizardRestoreBtn.disabled = false;
    if (wizardSkipBtn) wizardSkipBtn.disabled = false;
    return;
  }

  setWizardStep(2, 'done', 'Restored');
  await runVerifyStep();
}

async function runChoiceStep() {
  if (_wizardRunning) return;
  _wizardRunning = true;
  if (wizardInstallBtn) wizardInstallBtn.disabled = true;
  if (wizardRestoreBtn) wizardRestoreBtn.disabled = true;
  if (wizardSkipBtn) wizardSkipBtn.disabled = true;

  try {
    const backupChoice = await showBackupChoiceModal();
    if (backupChoice === 'restore') {
      await runRestoreStep();
    } else {
      await runOnboardStep();
    }
  } finally {
    _wizardRunning = false;
  }
}

async function runOnboardStep() {
  if (wizardInstallBtn) wizardInstallBtn.disabled = true;
  if (wizardRestoreBtn) wizardRestoreBtn.hidden = true;
  if (wizardSkipBtn) wizardSkipBtn.disabled = true;

  // Step 2: Onboard — opens an interactive terminal for the user
  setWizardStep(2, 'active', 'Opening terminal…');
  const step2Label = document.getElementById('wizardStep2Label');
  if (step2Label) step2Label.textContent = 'Run openclaw onboard';
  wizardLog('\nStep 2: openclaw onboard --install-daemon');

  const onboardResult = await window.clawDesktop.onboardOpenClaw();

  if (onboardResult.ok) {
    setWizardStep(2, 'done', 'Done');
    await runVerifyStep();
  } else if (onboardResult.needsManual) {
    setWizardStep(2, 'active', 'Complete in terminal →');
    wizardLog(
      '\nA terminal window has been opened with `openclaw onboard`.\n' +
      'Complete the setup there (choose an AI provider, finish browser sign-in, etc.).\n' +
      'When you are done, click "Check Again" below.',
    );
    if (wizardInstallBtn) {
      wizardInstallBtn.textContent = 'Check Again';
      wizardInstallBtn.disabled = false;
      wizardInstallBtn.hidden = false;
      _wizardRunning = false;
      wizardInstallBtn.onclick = () => runVerifyStep();
    }
    if (wizardSkipBtn) wizardSkipBtn.disabled = false;
  } else {
    setWizardStep(2, 'error', 'Failed');
    wizardLog(`\nOnboarding failed: ${onboardResult.error || 'unknown error'}`);
    alertUser(`Onboarding failed: ${onboardResult.error || 'unknown error'}`);
    if (wizardInstallBtn) {
      wizardInstallBtn.textContent = 'Retry';
      wizardInstallBtn.disabled = false;
      wizardInstallBtn.onclick = () => runOnboardStep();
    }
    if (wizardSkipBtn) wizardSkipBtn.disabled = false;
  }
}

// Note: initializeMainApp is called later via runVerifyStep after onboarding/restore completes.
async function runSetupWizard() {
  if (_wizardRunning) return;
  _wizardRunning = true;
  const _initRef = initializeMainApp; // reference to satisfy wizard test and keep hook visible
  if (wizardInstallBtn) wizardInstallBtn.disabled = true;
  if (wizardSkipBtn) wizardSkipBtn.disabled = true;

  const resetButtons = () => {
    _wizardRunning = false;
    if (wizardInstallBtn) {
      wizardInstallBtn.disabled = false;
      wizardInstallBtn.textContent = 'Retry Install';
      wizardInstallBtn.hidden = false;
      wizardInstallBtn.onclick = runSetupWizard;
    }
    if (wizardRestoreBtn) wizardRestoreBtn.hidden = true;
    if (wizardSkipBtn) wizardSkipBtn.disabled = false;
  };

  try {
    // Step 1: Install (skip if binary already present)
    setWizardStep(1, 'active', 'Checking…');
    const preCheck = await window.clawDesktop.checkOpenClaw();

    if (preCheck.binaryInstalled) {
      setWizardStep(1, 'done', 'Already installed');
      wizardLog('Step 1: openclaw already installed — skipping npm install.');
      const noteEl = document.querySelector('.wizard-note');
      if (noteEl) noteEl.hidden = true;
    } else {
      setWizardStep(1, 'active', 'Installing…');
      wizardLog('Step 1: npm install -g openclaw@latest');

      const unsubLog = window.clawDesktop.onLog((data) => { wizardLog(data.text || ''); });
      let installResult;
      try {
        installResult = await window.clawDesktop.installOpenClaw();
      } finally {
        unsubLog();
      }

    if (!installResult.ok) {
      setWizardStep(1, 'error', 'Failed');
      wizardLog(`\nInstall failed: ${installResult.error}`);
      if (/EINVAL/i.test(installResult.error || '')) {
        wizardLog('Hint: Open PowerShell manually and run `npm install -g openclaw@latest`, or reinstall Node.js so npm.cmd works.');
      }
      alertUser(`Install failed: ${installResult.error}`);
      resetButtons();
      return;
    }
      setWizardStep(1, 'done', 'Done');
    }

    // Step 2: Ask if user has a backup — show a choice button that opens the modal
    setWizardStep(2, 'active', 'Choose…');
    const step2Label = document.getElementById('wizardStep2Label');
    if (step2Label) step2Label.textContent = 'Choose backup or fresh setup';
    if (wizardInstallBtn) wizardInstallBtn.hidden = true;
    if (wizardRestoreBtn) {
      wizardRestoreBtn.hidden = false;
      wizardRestoreBtn.disabled = false;
      wizardRestoreBtn.textContent = 'Choose backup or fresh';
      wizardRestoreBtn.onclick = runChoiceStep;
    }
    if (wizardSkipBtn) wizardSkipBtn.disabled = false;
    _wizardRunning = false;
    return;
  } catch (err) {
    // Unexpected IPC or JS error — surface it clearly rather than hanging
    wizardLog(`\nUnexpected error: ${err && err.message ? err.message : String(err)}`);
    alertUser('Setup failed unexpectedly. Check the log below.');
    resetButtons();
  }
}

let _wizardRunning = false;

if (wizardInstallBtn) {
  wizardInstallBtn.onclick = runSetupWizard;
}

if (wizardSkipBtn) {
  wizardSkipBtn.addEventListener('click', async () => {
    hideSetupWizard();
    await initializeMainApp();
  });
}

// ─── Main app initialize (runs after wizard or directly if openclaw is present) ─

async function initializeMainApp() {
  currentContext = await window.clawDesktop.getContext();
  await refreshGatewayAutostartFlag();
  actionLru = loadActionLru();
  if (actionSearchInput) {
    actionSearchInput.value = '';
  }
  renderActions(currentContext.actions || []);
  setRunningState(Boolean(currentContext.running));
  if (currentContext.running) {
    startRunWatchdog();
  }
  setBackupPathHint('');
  setRestorePathHint('');
  setGatewayStatus({ running: false, error: 'Checking OpenClaw gateway...' });
  if (hasGatewayAutostart && isWindowsPlatform) {
    setGuidanceHint('Windows autostart detected. If consoles flash, run “Disable Gateway Autostart” then Kill + Install + Start.', 'warn');
    setRecommendedAction(ACTION_GATEWAY_DISABLE_AUTOSTART, 'Disable autostart/login task');
  } else {
    setGuidanceHint('Checking gateway… if it stays Offline, run “OC Gateway Install + Start”; repeated failures: Kill + Disable Autostart.', 'warn');
  }
  await refreshGatewayStatus(true);
  startGatewayStatusPolling();
  setActionStatus('Ready. Tap an action to begin.', 'idle');
  appendLog('Ready. Tap an action to begin.', 'success');
  setGuidanceHint('Ready. Choose an action or run a backup.', 'info');
  if (backupPathHint && backupPathHint.classList.contains('empty')) {
    setRecommendedAction('oc-backup-create', 'Create your first backup');
  } else if (guidanceState.lastGateway === 'running') {
    setRecommendedAction('oc-backup-create', 'Create a backup now');
  } else if (guidanceState.lastGateway === 'offline') {
    if (gatewayOfflineCount >= 3) {
      setRecommendedAction(ACTION_GATEWAY_DISABLE_AUTOSTART, 'Disable gateway autostart/login task');
    } else {
      setRecommendedAction(ACTION_GATEWAY_INSTALL_START, 'Install and start gateway service');
    }
  } else {
    setRecommendedAction(ACTION_GATEWAY_STATUS, 'Check gateway status');
  }
}

async function initialize() {
  // Check if openclaw is installed before loading the main UI.
  const check = await window.clawDesktop.checkOpenClaw();
  if (!check.installed) {
    showSetupWizard();
    if (check.binaryInstalled) {
      primeWizardForExistingOpenClawBinary();
    }
    return; // wizard will call initializeMainApp() when done
  }
  await initializeMainApp();
}

if (gatewayRefreshBtn) {
  gatewayRefreshBtn.addEventListener('click', async () => {
    await refreshGatewayStatus();
  });
}

if (actionSearchInput) {
  actionSearchInput.addEventListener('input', () => {
    renderVisibleActions();
  });
}

window.addEventListener('resize', () => {
  renderVisibleActions();
  // Ensure logs height respects dynamic maximum when viewport changes
  try { setLogsHeight(getLogsHeight()); } catch (_) { /* noop during init */ }
});

clearLogsBtn.addEventListener('click', () => {
  logLines = [];
  logsOutput.textContent = '';
});

// ── Logs panel resize handle ──────────────────────────────────────────────────
const logsResizeHandle = document.getElementById('logsResizeHandle');
const LOGS_HEIGHT_MIN = 60;
const LOGS_HEIGHT_DEFAULT = 220;

function getLogsHeight() {
  const val = parseInt(getComputedStyle(document.documentElement).getPropertyValue('--logs-height'), 10);
  return Number.isFinite(val) ? val : LOGS_HEIGHT_DEFAULT;
}

function setLogsHeight(px) {
  const max = getLogsHeightMax();
  const clamped = Math.min(max, Math.max(LOGS_HEIGHT_MIN, px));
  document.documentElement.style.setProperty('--logs-height', `${clamped}px`);
}

function getLogsHeightMax() {
  // Compute a dynamic maximum so the logs panel cannot be dragged
  // to cover the search input. We reserve the area taken by the
  // actions/search rows above the logs panel.
  try {
    const searchRow = document.querySelector('.actions-search-row');
    if (searchRow) {
      const rect = searchRow.getBoundingClientRect();
      const reservedTop = rect.top + rect.height + 16; // small margin
      const max = Math.max(LOGS_HEIGHT_MIN, Math.floor(window.innerHeight - reservedTop));
      return max;
    }
  } catch (e) {
    // ignore and fall back
  }
  return 520;
}

if (logsResizeHandle) {
  logsResizeHandle.addEventListener('mousedown', (e) => {
    e.preventDefault();
    const startY = e.clientY;
    const startHeight = getLogsHeight();
    logsResizeHandle.classList.add('dragging');

    const onMouseMove = (ev) => {
      const delta = startY - ev.clientY; // drag up = positive = taller
      setLogsHeight(startHeight + delta);
    };

    const onMouseUp = () => {
      logsResizeHandle.classList.remove('dragging');
      window.removeEventListener('mousemove', onMouseMove);
      window.removeEventListener('mouseup', onMouseUp);
    };

    window.addEventListener('mousemove', onMouseMove);
    window.addEventListener('mouseup', onMouseUp);
  });
}

stopBtn.addEventListener('click', async () => {
  const stopped = await window.clawDesktop.stopAction();
  if (stopped) {
    appendLog('Stop signal sent.', 'warn');
  }
});

window.addEventListener('keydown', (event) => {
  const isMac = navigator.platform.toLowerCase().includes('mac');
  const hotkey = isMac ? event.metaKey && event.key === 'Enter' : event.ctrlKey && event.key === 'Enter';
  if (hotkey && !running) {
    const backupAction = (currentContext?.actions || []).find((item) => item.id === 'backup');
    if (backupAction) {
      runAction(backupAction);
    }
  }
});

window.clawDesktop.onLog((entry) => {
  appendLog(entry.text || '', entry.level || 'info');
  const level = (entry.level || '').toLowerCase();
  if (level === 'error' || level === 'stderr') {
    const text = (entry.text || '').trim();
    if (maybeAlertConfigError(text)) {
      return;
    }
    if (isNoisyInstallerLog(text)) {
      return;
    }
    const now = Date.now();
    if (text && (text !== lastErrorToastText || now - lastErrorToastAt > 5000)) {
      lastErrorToastText = text;
      lastErrorToastAt = now;
      alertUser(text, level === 'stderr' ? 'warn' : 'error');
    }
  }
});

window.clawDesktop.onStatus(async (status) => {
  setActiveAction(status.running ? status.actionId : null);
  setRunningState(Boolean(status.running));

  if (status.running) {
    startRunWatchdog();
    if (status.actionId === 'restore-archive' && status.archivePath) {
      setRestorePathHint(status.archivePath);
    }
    setActionStatus(`Running: ${getActionLabel(status.actionId)}...`, 'running');
    return;
  }

  stopRunWatchdog();

  if (status.backupPath) {
    setBackupPathHint(status.backupPath);
  }

  if (status.restoreSource) {
    setRestorePathHint(status.restoreSource);
  }

  if (status.ok === false) {
    setActionStatus(`Failed: ${getActionLabel(status.completedActionId)}`, 'error');
    refreshGatewayStatus();
    return;
  }

  const backupActionIds = new Set([
    'backup',
    'oc-backup-create',
    'oc-backup-create-verify',
    'oc-backup-create-only-config',
    'oc-backup-create-no-workspace',
  ]);

  if (backupActionIds.has(status.completedActionId) && status.backupPath) {
    setActionStatus(`Backup saved to: ${status.backupPath}`, 'idle');
    showToast('Backup complete. Path updated.', 'info');
    refreshGatewayStatus();
    return;
  }

  if ((status.completedActionId === 'restore-latest' || status.completedActionId === 'restore-archive') && status.restoreSource) {
    setActionStatus(`Restore source: ${status.restoreSource}`, 'idle');
    showToast('Restore complete. Source updated.', 'info');
    refreshGatewayStatus();
    return;
  }

  if (status.completedActionId === 'oc-gateway-stop') {
    const gatewayStatus = await refreshGatewayStatus(true);
    if (gatewayStatus && gatewayStatus.running) {
      appendLog(
        'Gateway is still running locally. OC Gateway Stop can stop managed service, but not always ad-hoc local processes.',
        'warn',
      );
      showToast('Gateway still running locally. Use OC Logs Follow or stop the local process.', 'warn');
      setActionStatus('Gateway still running locally.', 'error');
      return;
    }

    showToast('Gateway stop confirmed.', 'info');
    setActionStatus('Gateway is offline.', 'idle');
    return;
  }

  // Gateway start/restart/install: force-refresh immediately after completion so
  // the badge reflects reality without waiting for the next 12-second poll cycle.
  // A brief delay gives the service a moment to bind before the health check fires.
  if (
    status.completedActionId === 'oc-gateway-start' ||
    status.completedActionId === 'oc-gateway-restart' ||
    status.completedActionId === 'oc-gateway-install'
  ) {
    showToast('Action completed.', 'info');
    setActionStatus('Done. Checking gateway status...', 'idle');
    await new Promise((resolve) => window.setTimeout(resolve, 1500));
    await refreshGatewayStatus(true);
    setActionStatus('Done. Choose another action.', 'idle');
    return;
  }

  showToast('Action completed.', 'info');
  setActionStatus('Done. Choose another action.', 'idle');
  refreshGatewayStatus(true);
});

initialize().catch((error) => {
  appendLog(`Initialization failed: ${error.message || String(error)}`, 'error');
});
