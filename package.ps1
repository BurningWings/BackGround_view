$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1')
$taskDist = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $taskDist -Force | Out-Null
$taskStage = Join-Path $taskDist ('package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskStage | Out-Null
try {
    foreach ($taskFile in @('WallpaperOnly.exe', 'IconSettings.exe', 'README.txt', 'LICENSE')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskFile) -Destination $taskStage
    }
    $taskAssets = Join-Path $taskStage 'assets'
    New-Item -ItemType Directory -Path $taskAssets | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'assets\README.txt') -Destination $taskAssets
    Compress-Archive -Path (Join-Path $taskStage '*') -DestinationPath (Join-Path $taskDist 'WallpaperOnly-Windows.zip') -Force
    $taskSourceStage = Join-Path $taskStage 'source'
    New-Item -ItemType Directory -Path $taskSourceStage | Out-Null
    foreach ($taskFile in @('WallpaperOnly.cs', 'IconSettings.cs', 'Verification.cs', 'IconVerification.cs', 'build.ps1', 'verify.ps1', 'package.ps1', 'README.md', 'README.txt', 'LICENSE', '.gitignore')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskFile) -Destination $taskSourceStage
    }
    $taskSourceAssets = Join-Path $taskSourceStage 'assets'
    New-Item -ItemType Directory -Path $taskSourceAssets | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'assets\README.txt') -Destination $taskSourceAssets
    # Include .gitignore explicitly: wildcard archive traversal can omit dotfiles.
    $taskSourceFiles = @(Get-ChildItem -LiteralPath $taskSourceStage -Force | ForEach-Object { $_.FullName })
    Compress-Archive -LiteralPath $taskSourceFiles -DestinationPath (Join-Path $taskDist 'WallpaperOnly-Source.zip') -Force
} finally {
    $taskResolved = [System.IO.Path]::GetFullPath($taskStage)
    if ($taskResolved.StartsWith(([System.IO.Path]::GetFullPath($taskDist) + '\'), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $taskResolved -Recurse -Force
    }
}
Write-Output (Join-Path $taskDist 'WallpaperOnly-Windows.zip')
Write-Output (Join-Path $taskDist 'WallpaperOnly-Source.zip')
