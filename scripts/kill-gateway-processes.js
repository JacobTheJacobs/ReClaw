#!/usr/bin/env node
const { spawnSync } = require('child_process');
const os = require('os');
const fs = require('fs');

function run(command, args) {
  try {
    const res = spawnSync(command, args, {
      stdio: ['ignore', 'ignore', 'ignore'],
      timeout: 8000,
      windowsHide: true,
    });
    if (res.error) {
      // best effort; ignore failures
    }
  } catch (_) {
    // best effort; ignore failures
  }
}

if (os.platform() === 'win32') {
  const ps = fs
    .existsSync('C:\\Program Files\\PowerShell\\7\\pwsh.exe') ? 'C:\\Program Files\\PowerShell\\7\\pwsh.exe'
    : fs.existsSync('C:\\Program Files (x86)\\PowerShell\\7\\pwsh.exe') ? 'C:\\Program Files (x86)\\PowerShell\\7\\pwsh.exe'
    : 'powershell.exe';

  // Primary: PowerShell (fast if available)
  const script = [
    '$pids = @()',
    '$pids += Get-NetTCPConnection -LocalPort 18789 -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess',
    "$pids += Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match 'gateway|openclaw' -or $_.Path -match 'openclaw' -or $_.CommandLine -match 'openclaw.*gateway' } | Select-Object -ExpandProperty Id",
    '$pids | Sort-Object -Unique | ForEach-Object { Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue }',
  ].join('; ');
  run(ps, ['-NoProfile', '-NonInteractive', '-WindowStyle', 'Hidden', '-Command', script]);

  // Fallback: netstat + taskkill to avoid hangs if PowerShell is blocked
  const netstat = spawnSync('cmd.exe', ['/c', 'for /f "tokens=5" %p in (\'netstat -ano ^| find "18789"\') do taskkill /PID %p /F'], {
    stdio: ['ignore', 'ignore', 'ignore'],
    windowsHide: true,
    timeout: 8000,
  });
  if (netstat && netstat.error) {
    // ignore, best effort
  }
  run('taskkill', ['/IM', 'openclaw.exe', '/F']);
  run('taskkill', ['/IM', 'node.exe', '/F']);
} else {
  // Kill by port and process name on Unix-like systems.
  run('bash', ['-lc', "pids=$(lsof -i :18789 -t 2>/dev/null); if [ -n \"$pids\" ]; then kill -9 $pids 2>/dev/null; fi"]);
  run('bash', ['-lc', "pkill -f 'openclaw gateway' || true"]);
  run('bash', ['-lc', "pkill -f 'openclaw.*18789' || true"]);
}

console.log('🔪 Attempted to kill gateway/OpenClaw processes and port 18789 listeners.');
