@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

echo ===================================================
echo   ClipBridge - Windows Installer
echo ===================================================

set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    set "CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
if not exist "%CSC%" (
    echo [ERROR] csc.exe compiler not found.
    pause
    exit /b 1
)

set "INSTALL_DIR=%USERPROFILE%\.local\bin"
if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"

echo [1/4] Stopping running service if any...
taskkill /f /im clipbridge.exe >nul 2>&1

echo [2/4] Compiling ClipBridge...
"%CSC%" /target:exe /optimize+ /platform:anycpu /out:"%INSTALL_DIR%\clipbridge.exe" "%~dp0Program.cs"
if %ERRORLEVEL% neq 0 (
    echo [ERROR] Compilation failed.
    pause
    exit /b 1
)

echo [3/4] Adding to User PATH if needed...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$p = [Environment]::GetEnvironmentVariable('PATH', 'User'); $dir = '%INSTALL_DIR%'; if ($p -notlike ('*' + $dir + '*')) { [Environment]::SetEnvironmentVariable('PATH', $dir + ';' + $p, 'User'); Write-Host 'Added %INSTALL_DIR% to User PATH.' }"

echo [4/4] Starting ClipBridge background service...
"%INSTALL_DIR%\clipbridge.exe" start

echo.
echo ===================================================
echo   ClipBridge successfully installed and started!
echo ===================================================
echo Commands available:
echo   clipbridge status
echo   clipbridge stop
echo   clipbridge start
echo   clipbridge --help
echo.
pause
