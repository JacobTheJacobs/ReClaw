const path = require('path');
const os = require('os');

function resolveDefaultBackupDir() {
  const cwd = process.cwd();
  const execPath = process.execPath || '';
  const packaged =
    cwd.includes('.app/Contents/Resources') ||
    cwd.includes('app.asar') ||
    execPath.includes('.app/Contents/') ||
    execPath.toLowerCase().endsWith('reclaw.exe');

  return packaged ? path.join(os.homedir(), 'claw-backup') : path.join(cwd, 'backups');
}

module.exports = {
  openclawHome: process.env.OPENCLAW_HOME || path.join(os.homedir(), '.openclaw'),
  backupDir: process.env.RECLAW_BACKUP_DIR || process.env.BACKUP_DIR || resolveDefaultBackupDir(),
  excludeDirs: ['node_modules', '.git', 'backups', '.next', 'coverage', 'temp_repo', 'browser', '_backup_temp'],
  excludeFiles: ['*.tar.gz', '*.zip', '.DS_Store', 'Thumbs.db']
};
