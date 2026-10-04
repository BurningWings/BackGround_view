$ErrorActionPreference = 'Stop'
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskOutput = Join-Path $PSScriptRoot 'WallpaperOnly.exe'
$taskArguments = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/utf8output', '/reference:System.Windows.Forms.dll', "/out:$taskOutput")
$taskSavedIcon = Join-Path $PSScriptRoot 'assets\icon.ico'
if (Test-Path -LiteralPath $taskSavedIcon) { $taskArguments += "/win32icon:$taskSavedIcon" }
& $taskCompiler @taskArguments (Join-Path $PSScriptRoot 'WallpaperOnly.cs')
if ($LASTEXITCODE -ne 0) { throw 'WallpaperOnly build failed.' }
$taskIconSettings = Join-Path $PSScriptRoot 'IconSettings.exe'
& $taskCompiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /out:$taskIconSettings (Join-Path $PSScriptRoot 'IconSettings.cs')
if ($LASTEXITCODE -ne 0) { throw 'IconSettings build failed.' }
Write-Output $taskOutput
