const fs = require('fs');
const path = require('path');
const os = require('os');
const { spawnSync } = require('child_process');

function resolveDefaultRepoDir() {
  const envRepo = process.env.OPENCLAW_REPO;
  if (envRepo) return envRepo;

  const repoRoot = path.resolve(__dirname, '..');
  const isPackaged = repoRoot.includes('.app/Contents/Resources');
  if (isPackaged) {
    return path.join(os.homedir(), 'openclaw');
  }
  return path.resolve(repoRoot, '..', 'openclaw');
}

function main() {
  const repoUrl = process.env.OPENCLAW_REPO_URL || 'https://github.com/openclaw/openclaw.git';
  const targetDir = resolveDefaultRepoDir();

  if (fs.existsSync(path.join(targetDir, '.git'))) {
    console.log(`OpenClaw repo already exists: ${targetDir}`);
    return;
  }

  if (fs.existsSync(targetDir) && fs.readdirSync(targetDir).length > 0) {
    console.error(`Target directory exists and is not empty: ${targetDir}`);
    process.exit(1);
  }

  const result = spawnSync('git', ['clone', repoUrl, targetDir], { stdio: 'inherit' });
  if (result.status !== 0) {
    process.exit(result.status || 1);
  }

  console.log(`Clone complete: ${targetDir}`);
}

main();
