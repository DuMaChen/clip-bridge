$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskCsc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $taskCsc)) { $taskCsc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$taskBin = Join-Path $taskRoot 'bin'
New-Item -ItemType Directory -Path $taskBin -Force | Out-Null
& $taskCsc /nologo /target:winexe /optimize+ /platform:anycpu "/out:$taskBin\clipbridge.exe" "$taskRoot\Program.cs"
if ($LASTEXITCODE -ne 0) { throw 'Application build failed' }
& $taskCsc /nologo /target:exe "/out:$taskBin\ClipboardRegression.exe" "$PSScriptRoot\ClipboardRegression.cs"
if ($LASTEXITCODE -ne 0) { throw 'Regression test build failed' }
& "$taskBin\ClipboardRegression.exe" "$taskBin\clipbridge.exe"
if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed' }
