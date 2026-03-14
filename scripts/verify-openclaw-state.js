#!/usr/bin/env node
const fs = require('fs');
const path = require('path');

function exists(p) {
  try {
    fs.accessSync(p);
    return true;
  } catch (_) {
    return false;
  }
}

function dirCount(p) {
  if (!exists(p)) return 0;
  try {
    return fs.readdirSync(p).length;
  } catch (_) {
    return 0;
  }
}

const mode = process.argv[2];
if (!mode || !['empty', 'restored'].includes(mode)) {
  console.error('Usage: node scripts/verify-openclaw-state.js <empty|restored>');
  process.exit(2);
}

const homeBase = process.env.HOME || process.env.USERPROFILE || '';
const home = process.env.OPENCLAW_HOME || path.join(homeBase, '.openclaw');
const targets = {
  env: path.join(home, '.env'),
  config: path.join(home, 'openclaw.json'),
  logs: path.join(home, 'logs'),
  workspaces: path.join(home, 'workspaces'),
  plugins: path.join(home, 'plugins'),
  credentials: path.join(home, 'credentials')
};

console.log(`verify_mode:${mode}`);
console.log(`openclaw_home:${home}`);

if (mode === 'empty') {
  const rootExists = exists(home);
  console.log(`home_exists:${rootExists ? 'yes' : 'no'}`);

  const present = Object.entries(targets).filter(([, p]) => exists(p));
  for (const [k] of present) {
    console.log(`${k}:present`);
  }

  if (!rootExists || present.length === 0) {
    console.log('result:PASS');
    process.exit(0);
  }

  console.error('result:FAIL (expected empty state, found restored artifacts)');
  process.exit(1);
}

// restored mode
let missing = 0;
for (const [k, p] of Object.entries(targets)) {
  const ok = exists(p);
  console.log(`${k}:${ok ? 'present' : 'missing'}`);
  if (!ok) {
    missing += 1;
    continue;
  }
  try {
    const st = fs.statSync(p);
    if (st.isDirectory()) {
      console.log(`${k}_count:${dirCount(p)}`);
    }
  } catch (_) {
    // ignore
  }
}

if (missing > 0) {
  console.error(`result:FAIL (missing=${missing})`);
  process.exit(1);
}

console.log('result:PASS');
process.exit(0);
