$ErrorActionPreference = 'Stop'
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskQa = Join-Path $PSScriptRoot '.qa'
New-Item -ItemType Directory -Path $taskQa -Force | Out-Null
$taskVerifier = Join-Path $taskQa 'Verification.exe'
& $taskCompiler /nologo /target:exe /main:Verification /utf8output /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /out:$taskVerifier (Join-Path $PSScriptRoot 'WallpaperOnly.cs') (Join-Path $PSScriptRoot 'Verification.cs')
if ($LASTEXITCODE -ne 0) { throw 'Verification build failed.' }
& $taskVerifier (Join-Path $PSScriptRoot 'WallpaperOnly.exe')
if ($LASTEXITCODE -ne 0) { throw 'Desktop verification failed.' }
$taskIconVerifier = Join-Path $taskQa 'IconVerification.exe'
& $taskCompiler /nologo /target:exe /main:IconVerification /utf8output /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /out:$taskIconVerifier (Join-Path $PSScriptRoot 'IconSettings.cs') (Join-Path $PSScriptRoot 'IconVerification.cs')
if ($LASTEXITCODE -ne 0) { throw 'Icon verification build failed.' }
& $taskIconVerifier (Join-Path $PSScriptRoot 'WallpaperOnly.exe')
if ($LASTEXITCODE -ne 0) { throw 'Icon verification failed.' }
