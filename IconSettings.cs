using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Windows.Forms;

internal static class IconSettings
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--apply")
        {
            try { IconWriter.Apply(args[1], args[2]); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        Application.EnableVisualStyles();
        Application.Run(new IconSettingsWindow());
        return 0;
    }
}

internal sealed class IconSettingsWindow : Form
{
    private readonly PictureBox preview = new PictureBox();
    private readonly Label path = new Label();
    private readonly Button apply = new Button();
    private readonly TextBox target = new TextBox();
    private string source;

    internal IconSettingsWindow(bool loadDefault = true)
    {
        Text = "WallpaperOnly - Icon Settings";
        Font = new Font("Segoe UI", 10);
        ClientSize = new Size(540, 430);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Label title = new Label();
        title.Text = "Choose an icon for WallpaperOnly";
        title.Font = new Font(Font, FontStyle.Bold);
        title.SetBounds(24, 20, 460, 28);
        Controls.Add(title);
        preview.SetBounds(24, 65, 128, 128);
        preview.BorderStyle = BorderStyle.FixedSingle;
        preview.BackColor = Color.WhiteSmoke;
        preview.SizeMode = PictureBoxSizeMode.Zoom;
        Controls.Add(preview);
        Label description = new Label();
        description.Text = "Select a PNG or ICO image.\r\nA square PNG of at least 256 x 256\r\npixels works best.";
        description.SetBounds(174, 65, 340, 94);
        Controls.Add(description);
        Button browse = new Button();
        browse.Text = "Choose image";
        browse.SetBounds(174, 163, 128, 32);
        browse.Click += delegate
        {
            using (OpenFileDialog picker = new OpenFileDialog())
            {
                picker.Title = "Choose an icon image";
                picker.InitialDirectory = AppDomain.CurrentDomain.BaseDirectory;
                picker.Filter = "Icon images (*.png;*.ico)|*.png;*.ico";
                if (picker.ShowDialog(this) == DialogResult.OK) LoadImage(picker.FileName);
            }
        };
        Controls.Add(browse);
        path.SetBounds(24, 213, 492, 48);
        path.Text = "Optional image location: assets/icon.png";
        Controls.Add(path);
        Label targetLabel = new Label();
        targetLabel.Text = "Application executable (renamed files are supported)";
        targetLabel.SetBounds(24, 272, 492, 24);
        Controls.Add(targetLabel);
        target.SetBounds(24, 302, 358, 28);
        target.Text = ApplicationTarget.Find(AppDomain.CurrentDomain.BaseDirectory);
        Controls.Add(target);
        Button chooseTarget = new Button();
        chooseTarget.Text = "Choose EXE";
        chooseTarget.SetBounds(394, 300, 122, 32);
        chooseTarget.Click += delegate
        {
            using (OpenFileDialog picker = new OpenFileDialog())
            {
                picker.Title = "Choose the WallpaperOnly executable";
                picker.Filter = "Windows executables (*.exe)|*.exe";
                picker.InitialDirectory = AppDomain.CurrentDomain.BaseDirectory;
                if (picker.ShowDialog(this) == DialogResult.OK) target.Text = picker.FileName;
            }
        };
        Controls.Add(chooseTarget);
        apply.Text = "Apply icon";
        apply.Enabled = false;
        apply.SetBounds(386, 370, 130, 34);
        apply.Click += delegate
        {
            try
            {
                IconWriter.Apply(source, target.Text);
                MessageBox.Show(this, "The executable icon has been updated.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception error)
            { MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        Controls.Add(apply);
        string defaultPng = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "icon.png");
        string defaultIco = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "icon.ico");
        if (loadDefault && File.Exists(defaultPng)) LoadImage(defaultPng);
        else if (loadDefault && File.Exists(defaultIco)) LoadImage(defaultIco);
    }

    private void LoadImage(string file)
    {
        try
        {
            Image next;
            if (Path.GetExtension(file).Equals(".ico", StringComparison.OrdinalIgnoreCase))
            {
                using (Icon icon = new Icon(file, new Size(256, 256))) next = icon.ToBitmap();
            }
            else using (Image loaded = Image.FromFile(file)) next = new Bitmap(loaded);
            if (preview.Image != null) preview.Image.Dispose();
            preview.Image = next;
            source = Path.GetFullPath(file);
            path.Text = source;
            apply.Enabled = true;
        }
        catch (Exception error)
        { MessageBox.Show(this, "Unable to open the image.\r\n" + error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && preview.Image != null) { preview.Image.Dispose(); preview.Image = null; }
        base.Dispose(disposing);
    }
}

internal static class ApplicationTarget
{
    internal static string Find(string directory)
    {
        string preferred = Path.Combine(directory, "WallpaperOnly.exe");
        if (IsApplication(preferred)) return preferred;
        foreach (string file in Directory.GetFiles(directory, "*.exe"))
            if (IsApplication(file)) return file;
        DirectoryInfo parent = Directory.GetParent(directory.TrimEnd(Path.DirectorySeparatorChar));
        if (parent != null)
        {
            preferred = Path.Combine(parent.FullName, "WallpaperOnly.exe");
            if (IsApplication(preferred)) return preferred;
        }
        return "";
    }
    private static bool IsApplication(string file)
    {
        try { return File.Exists(file) && AssemblyName.GetAssemblyName(file).Name == "WallpaperOnly"; }
        catch (BadImageFormatException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

internal static class IconWriter
{
    private sealed class ExistingResource
    { internal int Type, Id; internal string Name; internal ushort Language; }
    private delegate bool NameCallback(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);
    private delegate bool LanguageCallback(IntPtr module, IntPtr type, IntPtr name, ushort language, IntPtr parameter);

    internal static void Apply(string source, string target)
    {
        if (string.IsNullOrWhiteSpace(target)) throw new InvalidOperationException("Choose the WallpaperOnly executable first.");
        target = Path.GetFullPath(target);
        if (!File.Exists(target)) throw new FileNotFoundException("The selected executable was not found.");
        if (!File.Exists(source)) throw new FileNotFoundException("The image file was not found.");
        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(target)))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule.FileName, target, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Exit wallpaper mode before applying an icon.");
                }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        byte[] icon = Path.GetExtension(source).Equals(".ico", StringComparison.OrdinalIgnoreCase)
            ? File.ReadAllBytes(source) : FromPng(source);
        using (MemoryStream bytes = new MemoryStream(icon)) using (Icon valid = new Icon(bytes)) { }
        string temporary = Path.Combine(Path.GetDirectoryName(target), ".wallpaper-icon-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.Copy(target, temporary);
            UpdateExecutable(temporary, icon);
            File.Copy(temporary, target, true);
            string assets = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets");
            Directory.CreateDirectory(assets);
            string iconFile = Path.Combine(assets, "icon.ico");
            File.WriteAllBytes(iconFile, icon);
            SHChangeNotify(0x2000, 5, target, IntPtr.Zero);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static byte[] FromPng(string path)
    {
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        List<byte[]> frames = new List<byte[]>();
        using (Image image = Image.FromFile(path))
        {
            foreach (int size in sizes)
            using (Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (MemoryStream output = new MemoryStream())
            {
                graphics.Clear(Color.Transparent);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                double scale = Math.Min((double)size / image.Width, (double)size / image.Height);
                int width = Math.Max(1, (int)Math.Round(image.Width * scale));
                int height = Math.Max(1, (int)Math.Round(image.Height * scale));
                graphics.DrawImage(image, (size - width) / 2, (size - height) / 2, width, height);
                bitmap.Save(output, ImageFormat.Png);
                frames.Add(output.ToArray());
            }
        }
        using (MemoryStream output = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(output))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            int offset = 6 + sizes.Length * 16;
            for (int i = 0; i < sizes.Length; i++)
            {
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(frames[i].Length); writer.Write(offset); offset += frames[i].Length;
            }
            foreach (byte[] frame in frames) writer.Write(frame);
            return output.ToArray();
        }
    }

    private static void UpdateExecutable(string path, byte[] icon)
    {
        List<ExistingResource> resources = new List<ExistingResource>();
        IntPtr module = LoadLibraryEx(path, IntPtr.Zero, 2);
        if (module == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try
        {
            foreach (int type in new int[] { 3, 14 })
            {
                NameCallback names = delegate(IntPtr m, IntPtr t, IntPtr name, IntPtr unused)
                {
                    int id = 0; string text = null;
                    if (name.ToInt64() <= ushort.MaxValue) id = name.ToInt32();
                    else text = Marshal.PtrToStringUni(name);
                    LanguageCallback languages = delegate(IntPtr mm, IntPtr tt, IntPtr nn, ushort language, IntPtr pp)
                    { resources.Add(new ExistingResource { Type = type, Id = id, Name = text, Language = language }); return true; };
                    EnumResourceLanguages(m, t, name, languages, IntPtr.Zero);
                    return true;
                };
                EnumResourceNames(module, new IntPtr(type), names, IntPtr.Zero);
            }
        }
        finally { FreeLibrary(module); }
        IntPtr update = BeginUpdateResource(path, false);
        if (update == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        bool committed = false;
        try
        {
            foreach (ExistingResource resource in resources)
            {
                IntPtr name = resource.Name == null ? new IntPtr(resource.Id) : Marshal.StringToHGlobalUni(resource.Name);
                try { Check(UpdateResource(update, new IntPtr(resource.Type), name, resource.Language, null, 0)); }
                finally { if (resource.Name != null) Marshal.FreeHGlobal(name); }
            }
            using (BinaryReader reader = new BinaryReader(new MemoryStream(icon)))
            using (MemoryStream group = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(group))
            {
                Check(reader.ReadUInt16() == 0 && reader.ReadUInt16() == 1);
                ushort count = reader.ReadUInt16();
                if (count == 0 || count > 256) throw new InvalidDataException("Invalid ICO file.");
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write(count);
                for (ushort i = 0; i < count; i++)
                {
                    byte[] dimensions = reader.ReadBytes(8);
                    int size = reader.ReadInt32(); int offset = reader.ReadInt32();
                    if (size <= 0 || offset < 0 || offset > icon.Length - size) throw new InvalidDataException("Invalid ICO file.");
                    byte[] frame = new byte[size]; Array.Copy(icon, offset, frame, 0, size);
                    Check(UpdateResource(update, new IntPtr(3), new IntPtr(i + 1), 0, frame, (uint)frame.Length));
                    writer.Write(dimensions); writer.Write(size); writer.Write((ushort)(i + 1));
                }
                byte[] groupBytes = group.ToArray();
                Check(UpdateResource(update, new IntPtr(14), new IntPtr(32512), 0, groupBytes, (uint)groupBytes.Length));
            }
            Check(EndUpdateResource(update, false));
            committed = true;
        }
        finally { if (!committed) EndUpdateResource(update, true); }
    }

    private static void Check(bool value) { if (!value) throw new System.ComponentModel.Win32Exception(); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumResourceNames(IntPtr module, IntPtr type, NameCallback callback, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumResourceLanguages(IntPtr module, IntPtr type, IntPtr name, LanguageCallback callback, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr BeginUpdateResource(string file, bool deleteAll);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool UpdateResource(IntPtr update, IntPtr type, IntPtr name, ushort language, byte[] data, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool EndUpdateResource(IntPtr update, bool discard);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern void SHChangeNotify(uint action, uint flags, string path, IntPtr other);
}
