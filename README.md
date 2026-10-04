# WallpaperOnly

A portable Windows utility that temporarily shows only the current computer's live desktop wallpaper. It minimizes application windows and hides desktop icons and taskbars without replacing or capturing the wallpaper.

Licensed under the MIT License; see `LICENSE`.

## Use

1. Run `WallpaperOnly.exe`.
2. Press any keyboard key or left-click to exit and restore windows, icons, and taskbars.
3. Run the executable again whenever you want to use it.

Previously minimized windows stay minimized. Window positions, sizes, maximized states, and foreground focus are restored. A separate recovery process also restores the desktop if the main process crashes or is terminated. The launch double-click is briefly ignored so it does not immediately close the app.

The application discovers the current Windows Explorer desktop on each run. It uses that PC's existing wallpaper, including compatible animated wallpaper hosted in the desktop. It does not read a wallpaper file, overlay a saved screenshot, or use a developer's wallpaper. Explorer must be running in an interactive Windows session. Secure-desktop shortcuts and hardware-only keys are controlled by Windows.

## Icon settings and renamed executables

Keep `IconSettings.exe` next to `WallpaperOnly.exe`. Open it, choose a PNG/ICO image, and click **Apply icon**. A renamed main EXE can be discovered by its assembly identity. Use **Choose EXE** if you keep it somewhere else. Close wallpaper mode before editing the executable icon.

Optional local images can be placed in `assets/icon.png` or `assets/icon.ico` beside the helper. The helper saves the generated ICO in its own `assets/icon.ico`. It does not assume a username, Desktop folder location, or absolute installation path. Command-line use:

```powershell
IconSettings.exe --apply "path-to-image.png" "path-to-application.exe"
```

Renaming the main EXE does not affect wallpaper mode or crash recovery. Renaming documentation is also safe.

## Build and verification

Requires Windows 10/11 with the Windows .NET Framework 4.x runtime/compiler. No external packages or installer are required. Build from the repository root:

```powershell
powershell -NoProfile -File .\build.ps1
```

This produces `WallpaperOnly.exe` and `IconSettings.exe`. If `assets/icon.ico` exists it supplies the application icon; otherwise the default Windows application icon is used.

Run the integration checks on an interactive Windows desktop:

```powershell
powershell -NoProfile -File .\verify.ps1
```

The checks briefly minimize and restore windows. They exercise keyboard/left-click exit, crash recovery, wallpaper-host and tray-popup preservation, normal/maximized/minimized window states, foreground restoration, renamed EXEs, and PNG/ICO updates. They do not capture the screen. Generated fixtures stay in the Git-ignored `.qa` folder.

Create a portable release ZIP:

```powershell
powershell -NoProfile -File .\package.ps1
```

## Privacy and GitHub uploads

No network calls, telemetry, persistent keyboard logs, screen captures, user account names, or fixed personal paths are included. Window handles and placements are used temporarily for restoration and are not written to disk. Screenshot-capture and window-title diagnostics have been removed. Local custom icons, compiled executables, test output, and release ZIPs are excluded from Git by `.gitignore`.

Upload the source repository to GitHub, or extract `dist/WallpaperOnly-Source.zip` for a clean source-only copy. Portable executables can be shared separately as release assets from `dist/WallpaperOnly-Windows.zip`. The Windows ZIP contains the two executables, usage instructions, and an empty icon-assets placeholder. Both ZIPs exclude personal screenshots, source-control metadata, custom PNG/ICO files, and test output. If you build with a custom icon, that icon is embedded in the executable.
