#!/usr/bin/env node
const { execSync } = require('child_process');
const os = require('os');
const path = require('path');
const fs = require('fs');

function safeRun(cmd) {
  try {
    return execSync(cmd, { stdio: 'pipe' }).toString();
  } catch (err) {
    const stdout = err && err.stdout ? err.stdout.toString() : '';
    return stdout || err.message || '';
  }
}

if (os.platform() !== 'win32') {
  console.log('No gateway autostart entries to disable on non-Windows platforms.');
  process.exit(0);
}

const notes = [];
const taskNames = ['OpenClaw Gateway', 'OpenClawGateway', 'OpenClawGatewayTask'];

for (const name of taskNames) {
  let exists = false;
  try {
    execSync(`schtasks /Query /TN "${name}"`, { stdio: 'pipe' });
    exists = true;
  } catch (_) {
    // not present
  }

  if (!exists) {
    continue;
  }

  notes.push(`Found scheduled task "${name}". Ending and deleting...`);
  safeRun(`schtasks /End /TN "${name}" /F`);
  safeRun(`schtasks /Delete /TN "${name}" /F`);
}

// Stop and delete potential service name used by older installs.
safeRun('sc stop OpenClawGateway');
safeRun('sc delete OpenClawGateway');

// Remove startup shortcut if present.
const startupShortcut = path.join(
  process.env.APPDATA || '',
  'Microsoft',
  'Windows',
  'Start Menu',
  'Programs',
  'Startup',
  'OpenClaw Gateway.lnk',
);

if (startupShortcut && fs.existsSync(startupShortcut)) {
  try {
    fs.unlinkSync(startupShortcut);
    notes.push('Removed startup shortcut: OpenClaw Gateway.lnk');
  } catch (err) {
    notes.push(`Could not remove startup shortcut: ${err.message}`);
  }
}

if (notes.length === 0) {
  console.log('No OpenClaw gateway autostart tasks or shortcuts were found.');
} else {
  notes.forEach((line) => console.log(line));
}

console.log('Autostart cleanup complete. Re-run “OC Gateway Install + Start” after this if needed.');
