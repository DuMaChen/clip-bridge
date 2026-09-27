# ClipBridge - Windows Installation Script (PowerShell)
# Installs ClipBridge to ~/.local/bin and configures background auto-start

$ErrorActionPreference = "Stop"

# Ensure console output encoding is UTF-8
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding = [System.Text.Encoding]::UTF8
} catch {}

Write-Host "🌉 Installing ClipBridge for Windows..." -ForegroundColor Cyan

# 1. Stop existing process if running before updating binary
$existingProcesses = Get-Process -Name "clipbridge" -ErrorAction SilentlyContinue
if ($existingProcesses) {
    Write-Host "⏸️ Stopping running ClipBridge service for update..." -ForegroundColor Yellow
    $existingProcesses | Stop-Process -Force
    Start-Sleep -Milliseconds 400
}

# 2. Locate C# compiler (csc.exe)
$cscPath = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $cscPath)) {
    $cscPath = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path $cscPath)) {
    Write-Error "❌ Microsoft C# Compiler (csc.exe) not found in Windows directory."
    exit 1
}

# 3. Destination directory
$installDir = Join-Path $env:USERPROFILE ".local\bin"
if (-not (Test-Path $installDir)) {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
}

$sourceFile = Join-Path $PSScriptRoot "Program.cs"
$targetExe = Join-Path $installDir "clipbridge.exe"

# 4. Compile Program.cs
Write-Host "🔨 Compiling ClipBridge with native C# compiler..." -ForegroundColor Yellow
$compileArgs = @(
    "/target:winexe",
    "/optimize+",
    "/platform:anycpu",
    "/out:$targetExe",
    "$sourceFile"
)

$process = Start-Process -FilePath $cscPath -ArgumentList $compileArgs -NoNewWindow -Wait -PassThru
if ($process.ExitCode -ne 0) {
    Write-Error "❌ Compilation failed with exit code $($process.ExitCode)."
    exit 1
}

Write-Host "✅ Compiled binary: $targetExe" -ForegroundColor Green

# 5. Add ~/.local/bin to User PATH if not already present
$userPath = [Environment]::GetEnvironmentVariable("PATH", "User")
if ($userPath -notlike "*$installDir*") {
    Write-Host "➕ Adding $installDir to User PATH..." -ForegroundColor Yellow
    $newPath = "$installDir;$userPath"
    [Environment]::SetEnvironmentVariable("PATH", $newPath, "User")
    $env:PATH = "$installDir;$env:PATH"
    Write-Host "✅ Added to PATH. (New terminal windows will have 'clipbridge' available globally)." -ForegroundColor Green
}

# 6. Start background service and configure auto-start
Write-Host "🚀 Configuring auto-start and starting ClipBridge background service..." -ForegroundColor Cyan
& $targetExe start

Write-Host ""
Write-Host "🎉 ClipBridge installed successfully!" -ForegroundColor Green
Write-Host "• Run 'clipbridge status' to inspect running state."
Write-Host "• Run 'clipbridge stop' to stop background service."
Write-Host "• Run 'clipbridge --help' for command reference."
