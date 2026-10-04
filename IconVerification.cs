using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

internal static class IconVerification
{
    [STAThread]
    private static int Main(string[] args)
    {
        string folder = AppDomain.CurrentDomain.BaseDirectory;
        Application.EnableVisualStyles();
        using (IconSettingsWindow window = new IconSettingsWindow(false))
        {
            window.ShowInTaskbar = false;
            window.Show();
            Application.DoEvents();
        }
        string png = Path.Combine(folder, "test-icon.png");
        string target = Path.Combine(folder, "IconFixture.exe");
        using (Bitmap bitmap = new Bitmap(256, 256))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.FillRectangle(Brushes.CornflowerBlue, 12, 12, 232, 232);
            bitmap.Save(png, ImageFormat.Png);
        }
        File.Copy(args[0], target, true);
        if (ApplicationTarget.Find(folder) != target) throw new Exception("Renamed application discovery failed.");
        IconWriter.Apply(png, target);
        using (Icon extracted = Icon.ExtractAssociatedIcon(target))
        using (Bitmap image = extracted.ToBitmap())
        {
            Color center = image.GetPixel(image.Width / 2, image.Height / 2);
            if (center.B < 200 || center.R > 140) throw new Exception("Executable icon was not updated.");
        }
        IconWriter.Apply(Path.Combine(folder, "assets", "icon.ico"), target);
        Console.WriteLine("PASS: renamed EXE discovery; PNG and ICO update executable icon resources.");
        return 0;
    }
}
