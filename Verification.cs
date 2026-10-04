using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal static class Verification
{
    private static string executable;
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--restore-guard") return Guard.Run(args);
        executable = Path.GetFullPath(args[0]);
        List<IntPtr> previouslyOpen = new List<IntPtr>();
        IntPtr foreground = GetForegroundWindow();
        Native.EnumWindows(delegate(IntPtr h, IntPtr p)
        {
            string name = Native.ClassName(h);
            if (Native.IsWindowVisible(h) && !IsIconic(h) && GetWindowTextLength(h) > 0 &&
                name != "Progman" && name != "WorkerW" && !name.StartsWith("Shell_")) previouslyOpen.Add(h);
            return true;
        }, IntPtr.Zero);
        try
        {
            TestVisibility();
            TestShellWindowExclusion();
            foreach (byte key in new byte[] { 0x20, 0x51, 0x48, 0x44, 0x1B, 0x10 }) TestSession(key, false);
            TestSession(0x51, true);
            TestApplicationRestoration(false);
            TestApplicationRestoration(true);
            TestMouseExit();
            TestRenamedExecutable();
            Console.WriteLine("PASS: keyboard/mouse exit, crash recovery, wallpaper/tray preservation, application placement restoration.");
            return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL: " + error); return 1; }
        finally
        {
            foreach (IntPtr h in previouslyOpen) if (Native.IsWindow(h) && IsIconic(h)) Native.ShowWindow(h, 9);
            SetForegroundWindow(foreground);
        }
    }

    private static void TestVisibility()
    {
        using (Form form = new Form())
        {
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-20000, -20000);
            form.Show();
            WindowState visible = new WindowState(form.Handle);
            visible.Hide();
            Check(!Native.IsWindowVisible(form.Handle), "hide visible window");
            visible.Restore();
            Check(Native.IsWindowVisible(form.Handle), "restore visible window");
            form.Hide();
            WindowState hidden = new WindowState(form.Handle);
            hidden.Restore();
            Check(!Native.IsWindowVisible(form.Handle), "preserve originally hidden window");
        }
        Console.WriteLine("PASS: original visibility preserved.");
    }

    private static void TestSession(byte key, bool crash)
    {
        Dictionary<IntPtr, bool> wallpaperHosts = WallpaperHosts();
        Dictionary<IntPtr, bool> overflow = TrayPopups();
        List<IntPtr> shell = new List<IntPtr>();
        Dictionary<IntPtr, bool> original = new Dictionary<IntPtr, bool>();
        foreach (WindowState window in Native.ShellWindows())
        { shell.Add(window.Handle); original[window.Handle] = Native.IsWindowVisible(window.Handle); }
        Check(shell.Count >= 2, "desktop and taskbar found");
        using (Process app = Launch())
        {
            try
            {
                Wait(delegate { return !app.HasExited && shell.TrueForAll(delegate(IntPtr h) { return !Native.IsWindowVisible(h); }); }, "shell hidden");
                foreach (KeyValuePair<IntPtr, bool> host in wallpaperHosts)
                    Check(Native.IsWindowVisible(host.Key) == host.Value, "wallpaper host remains visible: " + Native.ClassName(host.Key));
                foreach (KeyValuePair<IntPtr, bool> popup in overflow)
                    Check(Native.IsIconic(popup.Key) == popup.Value, "tray overflow was not minimized");
                using (Process duplicate = Launch()) Check(duplicate.WaitForExit(3000) && duplicate.ExitCode == 0, "duplicate launch exits");
                Check(!app.HasExited, "original survives duplicate launch" + (app.HasExited ? " exit=" + app.ExitCode + " " + app.StandardError.ReadToEnd() : ""));
                if (crash) app.Kill();
                else
                {
                    keybd_event(key, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(40);
                    keybd_event(key, 0, 2, UIntPtr.Zero);
                }
                Wait(delegate { return app.HasExited; }, "program exits");
                Wait(delegate { return shell.TrueForAll(delegate(IntPtr h) { return Native.IsWindowVisible(h) == original[h]; }); }, "shell restored");
                foreach (KeyValuePair<IntPtr, bool> popup in overflow)
                    Check(Native.IsIconic(popup.Key) == popup.Value, "tray overflow remains usable after exit");
                Wait(delegate { return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)).Length == 0; }, "no lingering guard");
                Console.WriteLine(crash ? "PASS: forced termination restores shell." : "PASS: key 0x" + key.ToString("X2") + " exits and restores shell.");
            }
            finally
            {
                if (!app.HasExited) { app.Kill(); app.WaitForExit(3000); }
            }
        }
    }

    private static Dictionary<IntPtr, bool> TrayPopups()
    {
        Dictionary<IntPtr, bool> result = new Dictionary<IntPtr, bool>();
        Native.EnumWindows(delegate(IntPtr h, IntPtr p)
        {
            if (Native.IsTrayOverflow(Native.ClassName(h))) result[h] = Native.IsIconic(h);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static Native.WindowPlacement Placement(IntPtr handle)
    {
        Native.WindowPlacement placement = new Native.WindowPlacement();
        placement.Length = Marshal.SizeOf(placement);
        Check(Native.GetWindowPlacement(handle, ref placement), "read application placement");
        return placement;
    }

    private static bool SamePlacement(Native.WindowPlacement before, Native.WindowPlacement after)
    {
        return before.Show == after.Show && before.Normal.Left == after.Normal.Left &&
            before.Normal.Top == after.Normal.Top && before.Normal.Right == after.Normal.Right &&
            before.Normal.Bottom == after.Normal.Bottom;
    }

    private static void TestApplicationRestoration(bool crash)
    {
        using (Form normal = new Form())
        using (Form maximized = new Form())
        using (Form minimized = new Form())
        {
            normal.Text = "Wallpaper verification: normal";
            maximized.Text = "Wallpaper verification: maximized";
            minimized.Text = "Wallpaper verification: originally minimized";
            normal.StartPosition = FormStartPosition.Manual;
            normal.SetBounds(140, 180, 510, 290);
            maximized.StartPosition = FormStartPosition.Manual;
            maximized.SetBounds(200, 230, 560, 330);
            normal.Show(); maximized.Show(); minimized.Show();
            maximized.WindowState = FormWindowState.Maximized;
            minimized.WindowState = FormWindowState.Minimized;
            normal.Activate();
            Application.DoEvents();
            Native.WindowPlacement normalBefore = Placement(normal.Handle);
            Native.WindowPlacement maxBefore = Placement(maximized.Handle);
            IntPtr foregroundBefore = Native.GetForegroundWindow();
            using (Process app = Launch())
            {
                try
                {
                    Wait(delegate { return Native.IsIconic(normal.Handle) && Native.IsIconic(maximized.Handle); }, "test applications minimized");
                    List<WindowState> shell = Native.ShellWindows();
                    Wait(delegate { return shell.TrueForAll(delegate(WindowState state) { return !Native.IsWindowVisible(state.Handle); }); }, "restoration test fully armed");
                    Thread.Sleep(100);
                    Check(Native.IsIconic(minimized.Handle), "originally minimized application stays minimized during mode");
                    if (crash) app.Kill();
                    else
                    {
                        keybd_event(0x51, 0, 0, UIntPtr.Zero);
                        keybd_event(0x51, 0, 2, UIntPtr.Zero);
                    }
                    Wait(delegate { return app.HasExited; }, "program completes restoration");
                    Wait(delegate { return SamePlacement(normalBefore, Placement(normal.Handle)) &&
                        SamePlacement(maxBefore, Placement(maximized.Handle)); }, "original window bounds and maximized state restored");
                    Check(Native.IsIconic(minimized.Handle), "originally minimized application stays minimized after exit");
                    Wait(delegate { return Native.GetForegroundWindow() == foregroundBefore; }, "original foreground restored");
                    Wait(delegate { return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)).Length == 0; }, "restoration guard exits");
                    Console.WriteLine("PASS: " + (crash ? "crash" : "key exit") + " restores normal bounds, maximized state and focus; initially minimized window stays minimized.");
                }
                finally { if (!app.HasExited) { app.Kill(); app.WaitForExit(3000); } }
            }
        }
    }

    private static void TestShellWindowExclusion()
    {
        using (Form form = new Form())
        {
            form.Text = "A titled window";
            form.ShowInTaskbar = true;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-20000, -20000);
            form.Show();
            uint pid; Native.GetWindowThreadProcessId(form.Handle, out pid);
            Check(Native.IsApplicationWindow(form.Handle, new HashSet<uint>(), 0), "normal app is minimizable");
            Check(!Native.IsApplicationWindow(form.Handle, new HashSet<uint> { pid }, 0), "shell infrastructure is excluded");
            Check(Native.IsTrayOverflow("TopLevelWindowForOverflowXamlIsland"), "Windows 11 tray popup is excluded");
            Check(Native.IsTrayOverflow("NotifyIconOverflowWindow"), "legacy tray popup is excluded");
        }
        Console.WriteLine("PASS: normal applications minimized; shell infrastructure excluded.");
    }

    private static void TestRenamedExecutable()
    {
        string original = executable;
        string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "portable app");
        Directory.CreateDirectory(directory);
        string renamed = Path.Combine(directory, "Renamed.exe");
        File.Copy(original, renamed, true);
        try
        {
            executable = renamed;
            TestSession(0x51, false);
            Console.WriteLine("PASS: moved and renamed application restores desktop from a path containing spaces.");
        }
        finally { executable = original; }
    }

    private static void TestMouseExit()
    {
        List<IntPtr> shell = new List<IntPtr>();
        Dictionary<IntPtr, bool> original = new Dictionary<IntPtr, bool>();
        foreach (WindowState state in Native.ShellWindows())
        { shell.Add(state.Handle); original[state.Handle] = Native.IsWindowVisible(state.Handle); }
        using (Process app = Launch())
        {
            try
            {
                Wait(delegate { return !app.HasExited && shell.TrueForAll(delegate(IntPtr h) { return !Native.IsWindowVisible(h); }); }, "mouse mode ready");
                Thread.Sleep(450);
                mouse_event(2, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(40);
                mouse_event(4, 0, 0, 0, UIntPtr.Zero);
                Wait(delegate { return app.HasExited; }, "left click exits");
                Wait(delegate { return shell.TrueForAll(delegate(IntPtr h) { return Native.IsWindowVisible(h) == original[h]; }); }, "left click restores desktop");
                Wait(delegate { return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)).Length == 0; }, "mouse exit guard completes");
                Console.WriteLine("PASS: left click exits and restores desktop.");
            }
            finally { if (!app.HasExited) { app.Kill(); app.WaitForExit(3000); } }
        }
    }

    private static Dictionary<IntPtr, bool> WallpaperHosts()
    {
        Dictionary<IntPtr, bool> result = new Dictionary<IntPtr, bool>();
        Native.EnumWindows(delegate(IntPtr h, IntPtr p)
        {
            string name = Native.ClassName(h);
            if (name == "Progman" || name == "WorkerW")
            {
                result[h] = Native.IsWindowVisible(h);
                Native.EnumChildWindows(h, delegate(IntPtr child, IntPtr parameter)
                {
                    if (Native.ClassName(child) == "SHELLDLL_DefView") result[child] = Native.IsWindowVisible(child);
                    return true;
                }, IntPtr.Zero);
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static Process Launch()
    {
        ProcessStartInfo info = new ProcessStartInfo(executable);
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardError = true;
        info.WindowStyle = ProcessWindowStyle.Hidden;
        return Process.Start(info);
    }
    private static void Wait(Func<bool> predicate, string description)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 5000)
        { Application.DoEvents(); if (predicate()) return; Thread.Sleep(25); }
        throw new Exception(description + " timed out");
    }
    private static void Check(bool success, string description) { if (!success) throw new Exception(description); }
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
}
