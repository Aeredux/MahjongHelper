using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MahjongHelper.Mahjong;

/// <summary>
/// GDI <c>PrintWindow</c> / <c>BitBlt</c> of the FFXIV main window. Used when
/// the game screenshot writer and Dalamud viewport capture both fail.
/// Not unit-tested (HWND / user32).
/// </summary>
public static class WindowCaptureGdi
{
    public const string PrintWindowMethod = "CaptureFallback/PrintWindow";
    public const string BitBltMethod = "CaptureFallback/BitBlt";

    public readonly record struct CapturePixels(
        bool Ok,
        string Method,
        int Width,
        int Height,
        int Stride,
        byte[] Bgra,
        string Detail);

    public static CapturePixels TryCapture()
    {
        var hwnd = TryGetGameHwnd(out var hwndDetail);
        if (hwnd == 0)
            return new CapturePixels(false, ScreenshotCapturePaths.MethodName, 0, 0, 0, [], hwndDetail);

        if (!GetClientRect(hwnd, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            return new CapturePixels(false, ScreenshotCapturePaths.MethodName, 0, 0, 0, [], $"GetClientRect failed hwnd=0x{hwnd:X} {hwndDetail}");

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width > 8192 || height > 8192)
            return new CapturePixels(false, ScreenshotCapturePaths.MethodName, width, height, 0, [], $"client size {width}x{height} rejected");

        var printed = TryBlit(hwnd, width, height, usePrintWindow: true);
        if (printed.Ok && !IsMostlyBlack(printed.Bgra))
            return printed with { Detail = $"{printed.Detail}; {hwndDetail}" };

        var blt = TryBlit(hwnd, width, height, usePrintWindow: false);
        if (blt.Ok && !IsMostlyBlack(blt.Bgra))
            return blt with { Detail = $"{blt.Detail}; {hwndDetail}" };

        if (printed.Ok)
            return printed with { Detail = $"{printed.Detail}; mostly-black then BitBlt also empty; {hwndDetail}" };
        if (blt.Ok)
            return blt with { Detail = $"{blt.Detail}; {hwndDetail}" };

        return new CapturePixels(
            false,
            ScreenshotCapturePaths.MethodName,
            width,
            height,
            0,
            [],
            $"PrintWindow+BitBlt failed hwnd=0x{hwnd:X} {hwndDetail} print={printed.Detail} blt={blt.Detail}");
    }

    public static nint TryGetGameHwnd(out string detail)
    {
        var byClass = FindWindowW("FFXIVGAME", null);
        if (byClass != 0)
        {
            detail = $"hwnd=0x{byClass:X} via FindWindow(FFXIVGAME)";
            return byClass;
        }

        try
        {
            var main = Process.GetCurrentProcess().MainWindowHandle;
            if (main != 0)
            {
                detail = $"hwnd=0x{main:X} via Process.MainWindowHandle";
                return main;
            }
        }
        catch (Exception ex)
        {
            detail = $"FindWindow miss; MainWindowHandle threw {ex.Message}";
            return 0;
        }

        detail = "no FFXIVGAME / MainWindowHandle";
        return 0;
    }

    private static CapturePixels TryBlit(nint hwnd, int width, int height, bool usePrintWindow)
    {
        var method = usePrintWindow ? PrintWindowMethod : BitBltMethod;
        var hdcWindow = usePrintWindow ? GetWindowDC(hwnd) : GetDC(hwnd);
        if (hdcWindow == 0)
            return new CapturePixels(false, method, width, height, 0, [], usePrintWindow ? "GetWindowDC=0" : "GetDC=0");

        var hdcMem = CreateCompatibleDC(hdcWindow);
        var hBitmap = CreateCompatibleBitmap(hdcWindow, width, height);
        var old = SelectObject(hdcMem, hBitmap);
        try
        {
            bool ok;
            if (usePrintWindow)
            {
                ok = PrintWindow(hwnd, hdcMem, PwClientOnly | PwRenderFullContent)
                     || PrintWindow(hwnd, hdcMem, PwRenderFullContent)
                     || PrintWindow(hwnd, hdcMem, PwClientOnly);
            }
            else
            {
                ok = BitBlt(hdcMem, 0, 0, width, height, hdcWindow, 0, 0, SrcCopy);
            }

            if (!ok)
                return new CapturePixels(false, method, width, height, 0, [], usePrintWindow ? "PrintWindow=false" : "BitBlt=false");

            var stride = width * 4;
            var bgra = new byte[stride * height];
            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BiRgb,
                },
            };

            var copied = GetDIBits(hdcMem, hBitmap, 0, (uint)height, bgra, ref info, DibRgbColors);
            if (copied == 0)
                return new CapturePixels(false, method, width, height, stride, [], "GetDIBits=0");

            return new CapturePixels(true, method, width, height, stride, bgra, $"{method} {width}x{height}");
        }
        finally
        {
            SelectObject(hdcMem, old);
            if (hBitmap != 0)
                DeleteObject(hBitmap);
            if (hdcMem != 0)
                DeleteDC(hdcMem);
            if (hdcWindow != 0)
            {
                if (usePrintWindow)
                    ReleaseDC(hwnd, hdcWindow);
                else
                    ReleaseDC(hwnd, hdcWindow);
            }
        }
    }

    internal static bool IsMostlyBlack(byte[] bgra)
    {
        if (bgra.Length < 16)
            return true;
        var samples = 0;
        var dark = 0;
        for (var i = 0; i + 3 < bgra.Length; i += 16)
        {
            samples++;
            if (bgra[i] < 8 && bgra[i + 1] < 8 && bgra[i + 2] < 8)
                dark++;
        }

        return samples > 0 && dark * 100 >= samples * 98;
    }

    private const uint SrcCopy = 0x00CC0020;
    private const uint PwClientOnly = 0x00000001;
    private const uint PwRenderFullContent = 0x00000002;
    private const uint BiRgb = 0;
    private const uint DibRgbColors = 0;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetWindowDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(nint hwnd, nint hdcBlt, uint nFlags);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleBitmap(nint hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint ho);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(nint hdc, int x, int y, int cx, int cy, nint hdcSrc, int x1, int y1, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint hdc, nint hbm, uint start, uint lines, byte[] bits, ref BITMAPINFO lpbmi, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }
}
