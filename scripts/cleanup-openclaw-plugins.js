const fs = require('fs');
const path = require('path');
const os = require('os');

function expandHome(value) {
  if (!value) return value;
  const str = String(value);
  if (!str.startsWith('~')) return str;
  const tail = str.slice(1).replace(/^[/\\]+/, '');
  return path.join(os.homedir(), tail);
}

function getConfigPath() {
  return process.env.OPENCLAW_CONFIG || path.join(os.homedir(), '.openclaw', 'openclaw.json');
}

function readConfig(configPath) {
  const raw = fs.readFileSync(configPath, 'utf8');
  return JSON.parse(raw);
}

function writeConfig(configPath, data) {
  fs.writeFileSync(configPath, `${JSON.stringify(data, null, 2)}\n`, 'utf8');
}

function main() {
  const configPath = getConfigPath();
  if (!fs.existsSync(configPath)) {
    console.error(`OpenClaw config not found at ${configPath}`);
    process.exit(1);
  }

  let config;
  try {
    config = readConfig(configPath);
  } catch (error) {
    console.error(`Failed to read OpenClaw config: ${error.message}`);
    process.exit(1);
  }

  const pluginPaths = config?.plugins?.load?.paths;
  if (!Array.isArray(pluginPaths) || pluginPaths.length === 0) {
    console.log('No plugin paths to clean.');
    return;
  }

  const kept = [];
  const removed = [];
  pluginPaths.forEach((entry) => {
    const expanded = expandHome(entry);
    if (expanded && fs.existsSync(expanded)) {
      kept.push(entry);
    } else {
      removed.push(entry);
    }
  });

  if (removed.length === 0) {
    console.log('No missing plugin paths detected.');
    return;
  }

  const timestamp = new Date().toISOString().replace(/[:.]/g, '-');
  const backupPath = `${configPath}.bak-${timestamp}`;
  fs.copyFileSync(configPath, backupPath);

  if (!config.plugins) config.plugins = {};
  if (!config.plugins.load) config.plugins.load = {};
  config.plugins.load.paths = kept;

  writeConfig(configPath, config);

  console.log(`Removed ${removed.length} missing plugin path(s).`);
  removed.forEach((entry) => console.log(`- ${entry}`));
  console.log(`Backup saved to: ${backupPath}`);
}

main();
