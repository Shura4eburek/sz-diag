using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace SzDiag.Agent;

/// <summary>Снимок основного дисплея через GDI (BitBlt). Работает в интерактивной сессии.</summary>
[SupportedOSPlatform("windows")]
public sealed class GdiScreenCapturer : IScreenCapturer
{
    public ScreenCapture Capture()
    {
        try
        {
            var bounds = GetPrimaryBounds();
            using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);

            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return new ScreenCapture(ms.ToArray(), null);
        }
        catch (Exception ex)
        {
            return new ScreenCapture(null, ex.Message);
        }
    }

    private static Rectangle GetPrimaryBounds()
    {
        var w = GetSystemMetrics(0); // SM_CXSCREEN
        var h = GetSystemMetrics(1); // SM_CYSCREEN
        return new Rectangle(0, 0, w, h);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
