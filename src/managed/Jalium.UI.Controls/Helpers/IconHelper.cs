using System.Runtime.InteropServices;
using Jalium.UI.Media;
using Jalium.UI.Media.Imaging;
using static Jalium.UI.Interop.Win32.Win32GdiMethods;

namespace Jalium.UI.Controls.Helpers;

internal readonly record struct ProcessIconPixels(
    byte[] Pixels,
    int Width,
    int Height,
    int Stride);

/// <summary>
/// Extracts an application icon from an executable and encodes it as PNG
/// using only Win32 P/Invoke — no System.Drawing dependency.
/// </summary>
internal static partial class IconHelper
{
    internal static byte[]? ExtractProcessIconAsPng(string exePath)
    {
        var pixels = ExtractProcessIconPixels(exePath);
        if (pixels == null)
            return null;

        var source = BitmapSource.Create(
            pixels.Value.Width, pixels.Value.Height, 96, 96, PixelFormat.Bgra32, null,
            pixels.Value.Pixels, pixels.Value.Stride);
        var encoder = new PngBitmapEncoder { Frames = { BitmapFrame.Create(source) } };
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    internal static ProcessIconPixels? ExtractProcessIconPixels(string exePath)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        return ExtractProcessIconPixelsWindows(exePath);
    }

    /// <summary>
    /// Returns raw <c>HICON</c> handles for the running executable, sized to the system's large
    /// and small icon metrics, for use as a window class's <c>hIcon</c> / <c>hIconSm</c>.
    /// Either component is <c>0</c> when the icon cannot be extracted.
    /// </summary>
    /// <remarks>
    /// The returned handles are deliberately NOT destroyed by the caller: a window class owns its
    /// icons for as long as the class is registered, and the Jalium window class lives for the
    /// life of the process. Destroying them would leave the class pointing at freed handles.
    ///
    /// <para>These are what Windows draws for the window itself — the taskbar thumbnail header,
    /// Alt-Tab and the window menu — which is a DIFFERENT source from the taskbar *button*: the
    /// shell resolves that one from the executable independently, which is why an app can show a
    /// correct taskbar button and a stale default window icon at the same time.</para>
    /// </remarks>
    internal static (nint Large, nint Small) ExtractProcessIconHandles()
    {
        if (!OperatingSystem.IsWindows())
            return (0, 0);

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
            return (0, 0);

        return (
            ExtractIconHandleWindows(exePath, GetSystemMetrics(SM_CXICON), GetSystemMetrics(SM_CYICON)),
            ExtractIconHandleWindows(exePath, GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON)));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static nint ExtractIconHandleWindows(string exePath, int cx, int cy)
    {
        if (cx <= 0 || cy <= 0)
        {
            cx = cy = 32;
        }

        // Same preference order as ExtractProcessIconPixelsWindows, minus the shared
        // IDI_APPLICATION fallback: leaving hIcon at 0 lets Windows apply its own default,
        // which is exactly what a shared IDI_APPLICATION handle would have drawn anyway.
        if (PrivateExtractIconsW(exePath, 0, cx, cy, out var hIcon, 0, 1, 0) != 0 && hIcon != 0)
        {
            return hIcon;
        }

        var count = ExtractIconExW(exePath, 0, out var hIconLarge, out var hIconSmall, 1);
        if (count == 0)
        {
            return 0;
        }

        // Keep whichever matches the requested size better; destroy the other.
        var wantSmall = cx <= GetSystemMetrics(SM_CXSMICON);
        var keep = wantSmall && hIconSmall != 0 ? hIconSmall : hIconLarge;
        var drop = keep == hIconSmall ? hIconLarge : hIconSmall;
        if (drop != 0) DestroyIcon(drop);
        return keep;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static ProcessIconPixels? ExtractProcessIconPixelsWindows(string exePath)
    {
        nint hIcon = 0;
        bool isSharedIcon = false;
        try
        {
            // Prefer the highest-resolution frame. ExtractIconExW only ever returns the
            // system "large" icon (SM_CXICON, typically 32x32); a finely-drawn round logo
            // taken at 32px and shown in the title bar reads as a coarse low-poly shape —
            // the reported "circle became a heptagon". PrivateExtractIconsW lets us request
            // 256x256 explicitly, so Windows returns the icon's largest/closest frame, which
            // stays smooth when scaled down to the title-bar size.
            var extracted = PrivateExtractIconsW(exePath, 0, 256, 256, out hIcon, 0, 1, 0);
            if (extracted == 0 || hIcon == 0)
            {
                // Fallback 1: the standard large icon (usually 32x32).
                hIcon = 0;
                var count = ExtractIconExW(exePath, 0, out var hIconLarge, out var hIconSmall, 1);
                if (hIconSmall != 0) DestroyIcon(hIconSmall);
                hIcon = count != 0 ? hIconLarge : 0;
            }

            if (hIcon == 0)
            {
                // Fallback 2: the shared system application icon. A shared icon must NOT be
                // passed to DestroyIcon, so flag it so the finally block leaves it alone.
                hIcon = LoadIconW(0, IDI_APPLICATION);
                isSharedIcon = true;
                if (hIcon == 0)
                {
                    return null;
                }
            }

            return IconHandleToPixels(hIcon);
        }
        finally
        {
            if (hIcon != 0 && !isSharedIcon) DestroyIcon(hIcon);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static ProcessIconPixels? IconHandleToPixels(nint hIcon)
    {
        if (!GetIconInfo(hIcon, out var iconInfo))
        {
            return null;
        }

        nint hdc = 0;
        try
        {
            var hbmColor = iconInfo.hbmColor;
            if (hbmColor == 0)
            {
                return null;
            }

            // Get bitmap dimensions.
            var bmpSize = Marshal.SizeOf<BITMAP>();
            var bmp = new BITMAP();
            if (GetObjectW(hbmColor, bmpSize, ref bmp) == 0)
            {
                return null;
            }

            var width = bmp.bmWidth;
            var height = bmp.bmHeight;
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            // Prepare BITMAPINFOHEADER for 32-bit BGRA.
            var bih = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            };

            var pixelData = new byte[width * height * 4];
            hdc = CreateCompatibleDC(0);
            if (hdc == 0)
            {
                return null;
            }

            var lines = GetDIBits(hdc, hbmColor, 0, (uint)height, pixelData, ref bih, 0);
            if (lines == 0)
            {
                return null;
            }

            // Check if the color bitmap already has an alpha channel.
            var hasAlpha = false;
            for (var i = 3; i < pixelData.Length; i += 4)
            {
                if (pixelData[i] != 0)
                {
                    hasAlpha = true;
                    break;
                }
            }

            if (!hasAlpha && iconInfo.hbmMask != 0)
            {
                // Read the mask bitmap and apply it as alpha.
                var maskData = new byte[width * height * 4];
                var maskBih = bih;
                var maskLines = GetDIBits(hdc, iconInfo.hbmMask, 0, (uint)height, maskData, ref maskBih, 0);
                if (maskLines > 0)
                {
                    for (var i = 0; i < width * height; i++)
                    {
                        // In AND mask: 0 = opaque, 1 = transparent (when reading as 32bpp, 0x00 = opaque, 0xFF = transparent).
                        pixelData[i * 4 + 3] = (byte)(maskData[i * 4] == 0 ? 255 : 0);
                    }
                }
                else
                {
                    // If mask read fails, make fully opaque.
                    for (var i = 3; i < pixelData.Length; i += 4)
                    {
                        pixelData[i] = 255;
                    }
                }
            }

            return new ProcessIconPixels(pixelData, width, height, width * 4);
        }
        finally
        {
            if (hdc != 0) DeleteDC(hdc);
            if (iconInfo.hbmColor != 0) DeleteObject(iconInfo.hbmColor);
            if (iconInfo.hbmMask != 0) DeleteObject(iconInfo.hbmMask);
        }
    }

    #region Win32 P/Invoke

    private const nint IDI_APPLICATION = 32512;

    // System icon metrics, used to size the window class's hIcon / hIconSm.
    private const int SM_CXICON = 11;
    private const int SM_CYICON = 12;
    private const int SM_CXSMICON = 49;
    private const int SM_CYSMICON = 50;

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ExtractIconExW(string lpszFile, int nIconIndex, out nint phiconLarge, out nint phiconSmall, uint nIcons);

    // Extracts an icon at an explicit pixel size (we ask for 256x256) instead of the fixed
    // system large-icon size ExtractIconExW returns. Returns the number of icons extracted.
    [LibraryImport("user32.dll", EntryPoint = "PrivateExtractIconsW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PrivateExtractIconsW(string szFileName, int nIconIndex, int cxIcon, int cyIcon, out nint phicon, nint piconId, uint nIcons, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetIconInfo(nint hIcon, out ICONINFO piconinfo);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint hIcon);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
    private static partial nint LoadIconW(nint hInstance, nint lpIconName);

    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static partial int GetObjectW(nint h, int c, ref BITMAP pv);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(nint hdc, nint hbm, uint start, uint cLines, byte[] lpvBits, ref BITMAPINFOHEADER lpbmi, uint usage);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint hdc);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public nint bmBits;
    }

    #endregion
}
