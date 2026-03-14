# OpenClaw Backup Pro - Windows Installer
$ErrorActionPreference = "Stop"

$banner = @'
██████╗ ███████╗ ██████╗██╗      █████╗ ██╗    ██╗
██╔══██╗██╔════╝██╔════╝██║     ██╔══██╗██║    ██║
██████╔╝█████╗  ██║     ██║     ███████║██║ █╗ ██║
██╔══██╗██╔══╝  ██║     ██║     ██╔══██║██║███╗██║
██║  ██║███████╗╚██████╗███████╗██║  ██║╚███╔███╔╝
╚═╝  ╚═╝╚══════╝ ╚═════╝╚══════╝╚═╝  ╚═╝ ╚══╝╚══╝
'@

Write-Host $banner -ForegroundColor DarkCyan
Write-Host "JacobTheJacobs/ReClaw" -ForegroundColor DarkGray
Write-Host "📦 Installing OpenClaw Backup Pro..." -ForegroundColor Cyan

# Check for Node.js
if (!(Get-Command node -ErrorAction SilentlyContinue)) {
    Write-Error "❌ Node.js not found. Please install it from https://nodejs.org"
    exit 1
}

# Install dependencies
npm install

# Link the CLI globally
npm link

Write-Host "`n✅ Installation complete!" -ForegroundColor Green
Write-Host "🚀 You can now use 'reclaw backup' or 'reclaw restore <file>'" -ForegroundColor Gray
