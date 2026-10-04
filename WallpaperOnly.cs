using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--restore-guard")
            return Guard.Run(args);

        bool owns;
        using (Mutex instance = new Mutex(true, "Local\\WallpaperOnly-" + Process.GetCurrentProcess().SessionId, out owns))
        {
            if (!owns) return 0;
            DesktopMode mode = null;
            try
            {
                // A previous crashed instance may still be restoring Explorer.
                // Wait for that restoration before taking our own snapshot.
                using (Mutex recovery = new Mutex(false, Guard.RecoveryMutex))
                {
                    bool acquired;
                    try { acquired = recovery.WaitOne(5000); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new InvalidOperationException("The previous desktop restoration is still in progress.");
                    recovery.ReleaseMutex();
                    Application.EnableVisualStyles();
                    mode = new DesktopMode();
                    mode.Start();
                    Application.Run(mode);
                }
                return 0;
            }
            catch (Exception error)
            {
                if (mode != null) mode.Dispose();
                MessageBox.Show("Unable to start wallpaper mode.\r\n" + error.Message,
                    "WallpaperOnly", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            finally
            {
                if (mode != null) mode.Dispose();
                instance.ReleaseMutex();
            }
        }
    }
}

internal sealed class DesktopMode : ApplicationContext
{
    private readonly List<WindowState> windows = Native.ShellWindows();
    private readonly List<WindowState> session = new List<WindowState>();
    private readonly HashSet<int> heldAtLaunch = new HashSet<int>();
    private readonly Native.HookProc hookProc;
    private readonly Native.HookProc mouseProc;
    private readonly System.Windows.Forms.Timer timer;
    private readonly Dispatcher dispatcher = new Dispatcher();
    private IntPtr hook;
    private IntPtr mouseHook;
    private bool leftHeldAtLaunch;
    private bool leftExitPending;
    private readonly Stopwatch mouseDelay = new Stopwatch();
    private Process guard;
    private EventWaitHandle guardDone;
    private bool finished;
    private bool armed;

    internal DesktopMode()
    {
        hookProc = OnKey;
        mouseProc = OnMouse;
        timer = new System.Windows.Forms.Timer();
        timer.Interval = 80;
        timer.Tick += OnTick;
        dispatcher.ExitRequested += delegate { Finish(); };
    }

    internal void Start()
    {
        if (windows.Count == 0)
            throw new InvalidOperationException("The current Windows desktop could not be found.");

        List<WindowState> applications = Native.ApplicationWindows();
        session.AddRange(windows);
        session.AddRange(applications);
        IntPtr foreground = Native.GetForegroundWindow();
        if (foreground != IntPtr.Zero) session.Add(new WindowState(foreground, 'F'));
        // Snapshot application placements and start recovery before any mutation.
        guard = Guard.Start(session, out guardDone);
        Native.AllowSetForegroundWindow((uint)guard.Id);
        for (int key = 8; key <= 255; key++)
            if ((Native.GetAsyncKeyState(key) & 0x8000) != 0) heldAtLaunch.Add(key);
        leftHeldAtLaunch = (Native.GetAsyncKeyState(1) & 0x8000) != 0;

        hook = Native.SetWindowsHookEx(13, hookProc, Native.GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        mouseHook = Native.SetWindowsHookEx(14, mouseProc, Native.GetModuleHandle(null), 0);
        if (mouseHook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();

        // Minimize application windows only. Shell flyouts (including the hidden
        // tray icons popup) must never be minimized or their next opening breaks.
        foreach (WindowState application in applications) application.Minimize();

        foreach (WindowState window in windows) window.Hide();
        armed = true;
        mouseDelay.Start();
        timer.Start();
    }

    private IntPtr OnKey(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && armed && !finished)
        {
            int kind = message.ToInt32();
            int key = Marshal.ReadInt32(data);
            if (kind == 0x101 || kind == 0x105) heldAtLaunch.Remove(key);
            if ((kind == 0x100 || kind == 0x104) && !heldAtLaunch.Contains(key))
            {
                // Post rather than doing process cleanup inside a keyboard hook.
                RequestExit();
                return new IntPtr(1);
            }
        }
        return Native.CallNextHookEx(hook, code, message, data);
    }

    private void RequestExit()
    {
        armed = false;
        Native.PostMessage(dispatcher.Handle, Dispatcher.ExitMessage, IntPtr.Zero, IntPtr.Zero);
    }

    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && !finished)
        {
            int kind = message.ToInt32();
            if (kind == 0x202)
            {
                leftHeldAtLaunch = false;
                if (leftExitPending)
                {
                    leftExitPending = false;
                    RequestExit();
                    return new IntPtr(1);
                }
            }
            // Ignore the launch double-click. Consume both halves of the exit
            // click so it cannot accidentally operate a restored application.
            if (kind == 0x201 && armed && !leftHeldAtLaunch && mouseDelay.ElapsedMilliseconds >= 350)
            {
                leftExitPending = true;
                return new IntPtr(1);
            }
        }
        return Native.CallNextHookEx(mouseHook, code, message, data);
    }

    private void OnTick(object sender, EventArgs args)
    {
        heldAtLaunch.RemoveWhere(delegate(int key) { return (Native.GetAsyncKeyState(key) & 0x8000) == 0; });
        if ((Native.GetAsyncKeyState(1) & 0x8000) == 0) leftHeldAtLaunch = false;
        if (guard.HasExited) { Finish(); return; }
        // Explorer can show the taskbar again when the mouse reaches its edge.
        foreach (WindowState window in windows) window.Hide();
    }

    private void Finish()
    {
        if (finished) return;
        finished = true;
        armed = false;
        timer.Stop();
        if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        if (mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
        foreach (WindowState window in windows) window.Restore();
        if (guardDone != null)
        {
            if (guard != null && !guard.HasExited) Native.AllowSetForegroundWindow((uint)guard.Id);
            guardDone.Set();
            // Keep the instance mutex until the old guard has finished restoring.
            if (guard == null || !guard.WaitForExit(2000) || guard.ExitCode != 0)
                WindowState.RestoreSession(session);
        }
        else WindowState.RestoreSession(session);
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        Finish();
        if (disposing)
        {
            timer.Dispose();
            dispatcher.Dispose();
            if (guard != null) guard.Dispose();
            if (guardDone != null) guardDone.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class Dispatcher : NativeWindow, IDisposable
{
    internal const int ExitMessage = 0x8001;
    internal event EventHandler ExitRequested;
    internal Dispatcher()
    {
        CreateParams parameters = new CreateParams();
        parameters.Parent = new IntPtr(-3); // HWND_MESSAGE: no visible program window.
        CreateHandle(parameters);
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == ExitMessage)
        {
            if (ExitRequested != null) ExitRequested(this, EventArgs.Empty);
        }
        base.WndProc(ref message);
    }
    public void Dispose() { if (Handle != IntPtr.Zero) DestroyHandle(); }
}

internal sealed class WindowState
{
    internal readonly IntPtr Handle;
    private readonly uint owner;
    private readonly string className;
    private readonly bool visible;
    internal readonly char Kind;
    private Native.WindowPlacement placement;

    internal WindowState(IntPtr handle) : this(handle, 'S') { }

    internal WindowState(IntPtr handle, char kind)
    {
        Handle = handle;
        Native.GetWindowThreadProcessId(handle, out owner);
        className = Native.ClassName(handle);
        visible = Native.IsWindowVisible(handle);
        Kind = kind;
        if (kind == 'A')
        {
            placement = new Native.WindowPlacement();
            placement.Length = Marshal.SizeOf(placement);
            if (!Native.GetWindowPlacement(handle, ref placement))
                throw new System.ComponentModel.Win32Exception();
        }
    }

    private WindowState(IntPtr handle, uint process, string name, bool wasVisible, char kind, Native.WindowPlacement saved)
    { Handle = handle; owner = process; className = name; visible = wasVisible; Kind = kind; placement = saved; }

    private bool Exists()
    {
        uint process;
        Native.GetWindowThreadProcessId(Handle, out process);
        return Native.IsWindow(Handle) && process == owner && Native.ClassName(Handle) == className;
    }
    internal void Hide() { if (Exists() && Native.IsWindowVisible(Handle)) Native.ShowWindow(Handle, 0); }
    internal void Minimize() { if (Kind == 'A' && Exists()) Native.ShowWindowAsync(Handle, 6); }
    internal void Restore()
    {
        if (!Exists()) return;
        if (Kind == 'F') { Native.SetForegroundWindow(Handle); return; }
        if (Kind == 'A')
        {
            Native.WindowPlacement saved = placement;
            saved.Flags |= 4; // WPF_ASYNCWINDOWPLACEMENT: do not wait on hung apps.
            if (saved.Show == 1) saved.Show = 4; // Normal size without focus changes.
            Native.SetWindowPlacement(Handle, ref saved);
        }
        else Native.ShowWindow(Handle, visible ? 8 : 0);
    }

    internal static void RestoreSession(List<WindowState> states)
    {
        // Taskbars first, then applications in reverse Z order, then prior focus.
        foreach (WindowState state in states) if (state.Kind == 'S') state.Restore();
        for (int i = states.Count - 1; i >= 0; i--) if (states[i].Kind == 'A') states[i].Restore();
        // Placements are posted across processes. A maximized window can take
        // focus when that post executes, so restore original focus afterward.
        Stopwatch wait = Stopwatch.StartNew();
        while (wait.ElapsedMilliseconds < 500)
        {
            bool ready = true;
            foreach (WindowState state in states)
                if (state.Kind == 'A' && !state.PlacementRestored()) { ready = false; break; }
            if (ready) break;
            Thread.Sleep(10);
        }
        foreach (WindowState state in states) if (state.Kind == 'F') state.Restore();
    }

    private bool PlacementRestored()
    {
        if (!Exists()) return true;
        Native.WindowPlacement current = new Native.WindowPlacement();
        current.Length = Marshal.SizeOf(current);
        return !Native.GetWindowPlacement(Handle, ref current) || current.Show == placement.Show;
    }

    internal string Encode()
    {
        string saved = string.Join(";", new string[] {
            placement.Flags.ToString(CultureInfo.InvariantCulture), placement.Show.ToString(CultureInfo.InvariantCulture),
            placement.Min.X.ToString(CultureInfo.InvariantCulture), placement.Min.Y.ToString(CultureInfo.InvariantCulture),
            placement.Max.X.ToString(CultureInfo.InvariantCulture), placement.Max.Y.ToString(CultureInfo.InvariantCulture),
            placement.Normal.Left.ToString(CultureInfo.InvariantCulture), placement.Normal.Top.ToString(CultureInfo.InvariantCulture),
            placement.Normal.Right.ToString(CultureInfo.InvariantCulture), placement.Normal.Bottom.ToString(CultureInfo.InvariantCulture) });
        return Handle.ToInt64().ToString(CultureInfo.InvariantCulture) + ":" + owner + ":" +
            (visible ? "1" : "0") + ":" + Convert.ToBase64String(Encoding.UTF8.GetBytes(className)) + ":" + Kind + ":" + saved;
    }
    internal static WindowState Decode(string value)
    {
        string[] parts = value.Split(':');
        string[] saved = parts[5].Split(';');
        Native.WindowPlacement placement = new Native.WindowPlacement();
        placement.Length = Marshal.SizeOf(placement);
        placement.Flags = int.Parse(saved[0], CultureInfo.InvariantCulture);
        placement.Show = int.Parse(saved[1], CultureInfo.InvariantCulture);
        placement.Min.X = int.Parse(saved[2], CultureInfo.InvariantCulture);
        placement.Min.Y = int.Parse(saved[3], CultureInfo.InvariantCulture);
        placement.Max.X = int.Parse(saved[4], CultureInfo.InvariantCulture);
        placement.Max.Y = int.Parse(saved[5], CultureInfo.InvariantCulture);
        placement.Normal.Left = int.Parse(saved[6], CultureInfo.InvariantCulture);
        placement.Normal.Top = int.Parse(saved[7], CultureInfo.InvariantCulture);
        placement.Normal.Right = int.Parse(saved[8], CultureInfo.InvariantCulture);
        placement.Normal.Bottom = int.Parse(saved[9], CultureInfo.InvariantCulture);
        return new WindowState(new IntPtr(long.Parse(parts[0], CultureInfo.InvariantCulture)),
            uint.Parse(parts[1], CultureInfo.InvariantCulture),
            Encoding.UTF8.GetString(Convert.FromBase64String(parts[3])), parts[2] == "1", parts[4][0], placement);
    }
}

internal static class Guard
{
    internal static string RecoveryMutex
    { get { return "Local\\WallpaperOnlyRecovery-" + Process.GetCurrentProcess().SessionId; } }

    internal static Process Start(List<WindowState> windows, out EventWaitHandle done)
    {
        string eventName = "Local\\WallpaperOnlyReady-" + Guid.NewGuid().ToString("N");
        done = new EventWaitHandle(false, EventResetMode.ManualReset, eventName + "-done");
        List<string> states = new List<string>();
        foreach (WindowState window in windows) states.Add(window.Encode());
        using (EventWaitHandle ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName))
        {
            ProcessStartInfo info = new ProcessStartInfo(Application.ExecutablePath);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            info.Arguments = "--restore-guard " + Process.GetCurrentProcess().Id + " " + eventName + " " + string.Join(",", states.ToArray());
            Process process = Process.Start(info);
            if (!ready.WaitOne(5000) || process.HasExited)
            {
                process.Dispose();
                throw new InvalidOperationException("Unable to prepare desktop recovery.");
            }
            return process;
        }
    }

    internal static int Run(string[] args)
    {
        List<WindowState> windows = new List<WindowState>();
        try
        {
            foreach (string value in args[3].Split(',')) windows.Add(WindowState.Decode(value));
            using (Process parent = Process.GetProcessById(int.Parse(args[1], CultureInfo.InvariantCulture)))
            using (EventWaitHandle ready = EventWaitHandle.OpenExisting(args[2]))
            using (EventWaitHandle done = EventWaitHandle.OpenExisting(args[2] + "-done"))
            using (Mutex recovery = new Mutex(false, RecoveryMutex))
            {
                try { recovery.WaitOne(); } catch (AbandonedMutexException) { }
                try
                {
                    ready.Set();
                    while (!done.WaitOne(20) && !parent.HasExited) { }
                }
                finally
                {
                    WindowState.RestoreSession(windows);
                    windows.Clear();
                    recovery.ReleaseMutex();
                }
            }
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { WindowState.RestoreSession(windows); }
        return 0;
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct WindowPlacement
    { public int Length, Flags, Show; public Point Min, Max; public Rect Normal; }
    internal delegate bool EnumProc(IntPtr handle, IntPtr parameter);
    internal delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    internal static string ClassName(IntPtr handle)
    { StringBuilder name = new StringBuilder(256); GetClassName(handle, name, name.Capacity); return name.ToString(); }

    internal static List<WindowState> ShellWindows()
    {
        List<WindowState> result = new List<WindowState>();
        HashSet<IntPtr> seen = new HashSet<IntPtr>();
        EnumProc collect = delegate(IntPtr handle, IntPtr parameter)
        {
            string name = ClassName(handle);
            // DefView can paint the desktop wallpaper as well as hosting icons.
            // Hide only its icon list, keeping DefView and wallpaper hosts visible.
            if (name == "SysListView32" && ClassName(GetParent(handle)) == "SHELLDLL_DefView")
                if (seen.Add(handle)) result.Add(new WindowState(handle));
            return true;
        };
        EnumWindows(delegate(IntPtr handle, IntPtr parameter)
        {
            string name = ClassName(handle);
            if (name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd")
                if (seen.Add(handle)) result.Add(new WindowState(handle));
            if (name == "Progman" || name == "WorkerW")
                EnumChildWindows(handle, collect, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    internal static bool IsTrayOverflow(string name)
    {
        return name == "TopLevelWindowForOverflowXamlIsland" || name == "NotifyIconOverflowWindow";
    }

    internal static int RepairTrayOverflow()
    {
        HashSet<uint> shellProcesses = ShellProcesses();
        int repaired = 0;
        EnumWindows(delegate(IntPtr handle, IntPtr parameter)
        {
            uint pid;
            GetWindowThreadProcessId(handle, out pid);
            if (shellProcesses.Contains(pid) && IsTrayOverflow(ClassName(handle)) && IsIconic(handle))
            {
                bool visible = IsWindowVisible(handle);
                ShowWindow(handle, 4); // Restore normal size without taking focus.
                if (!visible) ShowWindow(handle, 0); // Let the chevron open it normally.
                repaired++;
            }
            return true;
        }, IntPtr.Zero);
        return repaired;
    }

    private static HashSet<uint> ShellProcesses()
    {
        HashSet<uint> processes = new HashSet<uint>();
        EnumWindows(delegate(IntPtr handle, IntPtr parameter)
        {
            if (ClassName(handle) == "Shell_TrayWnd")
            {
                uint pid;
                GetWindowThreadProcessId(handle, out pid);
                processes.Add(pid);
            }
            return true;
        }, IntPtr.Zero);
        return processes;
    }

    internal static bool IsApplicationWindow(IntPtr handle, HashSet<uint> shellProcesses, uint ownPid)
    {
        string name = ClassName(handle);
        uint pid;
        GetWindowThreadProcessId(handle, out pid);
        long style = GetWindowLong(handle, -16);
        long extendedStyle = GetWindowLong(handle, -20);
        if (pid == ownPid || !IsWindowVisible(handle) || IsIconic(handle) ||
            IsTrayOverflow(name) || name == "Progman" || name == "WorkerW" ||
            name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd") return false;
        // Explorer owns numerous titled infrastructure windows. Only its real
        // folder windows belong in the application's minimization list.
        if (shellProcesses.Contains(pid) && name != "CabinetWClass" && name != "ExploreWClass") return false;
        if ((style & 0x40000000L) != 0 || (extendedStyle & 0x80L) != 0) return false;
        bool appWindow = (extendedStyle & 0x40000L) != 0;
        if (GetWindow(handle, 4) != IntPtr.Zero && !appWindow) return false;
        bool caption = (style & 0x00C00000L) == 0x00C00000L;
        bool minimizable = (style & 0x20000L) != 0;
        return GetWindowTextLength(handle) > 0 && (caption || minimizable || appWindow);
    }

    internal static void MinimizeApplicationWindows()
    {
        foreach (WindowState application in ApplicationWindows()) application.Minimize();
    }

    internal static List<WindowState> ApplicationWindows()
    {
        List<WindowState> applications = new List<WindowState>();
        uint ownPid = (uint)Process.GetCurrentProcess().Id;
        HashSet<uint> shellProcesses = ShellProcesses();
        EnumWindows(delegate(IntPtr handle, IntPtr parameter)
        {
            if (IsApplicationWindow(handle, shellProcesses, ownPid))
            {
                try { applications.Add(new WindowState(handle, 'A')); }
                catch (System.ComponentModel.Win32Exception) { /* Closed during enumeration. */ }
            }
            return true;
        }, IntPtr.Zero);
        return applications;
    }

    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder name, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr handle);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr handle, out uint process);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr handle);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint command);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(IntPtr handle, int command);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetWindowPlacement(IntPtr handle, ref WindowPlacement placement);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPlacement(IntPtr handle, ref WindowPlacement placement);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] internal static extern bool AllowSetForegroundWindow(uint process);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int type, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string name);
}
