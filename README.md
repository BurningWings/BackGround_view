# WallpaperOnly

Show only your current desktop wallpaper. Windows 10/11; no installation required.

## Get started
- Shows the current computer’s desktop wallpaper.
- Minimizes windows and hides desktop icons and taskbars.
- Exits when any key is pressed or the left mouse button is clicked.
- Restores windows, desktop icons, and taskbars on exit.

1. Download this repository as a ZIP and extract it.
2. Open PowerShell in the extracted folder and run:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
   ```

3. Run the generated `WallpaperOnly.exe`.

Press any key or left-click to exit and restore your windows, desktop icons, and taskbars.

To change the EXE icon, open `IconSettings.exe`, choose a PNG/ICO image, and click **Apply icon**.

Uses the current computer's wallpaper. No screenshots or personal wallpaper files are included.

MIT License — Copyright (c) 2026 BurningWings.
