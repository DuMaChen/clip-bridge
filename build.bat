@echo off
set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "bin" mkdir "bin"

echo Compiling ClipBridge for Windows...
"%CSC%" /target:winexe /optimize+ /platform:anycpu /out:bin\clipbridge.exe "%~dp0Program.cs"
if %ERRORLEVEL% equ 0 (
    echo Compilation succeeded: bin\clipbridge.exe
) else (
    echo Compilation failed!
)
