using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

internal sealed partial class ScreenCrop : IDisposable
{
    private const uint CopyPixels = 0x00CC0020;
    private readonly nint bitmap;
    private readonly nint dc;
    private readonly nint oldBitmap;
    private readonly nint pixels;
    private readonly byte[] image;
    private readonly CropArea area;
    private nint capturedWindow;
    private Point capturedOrigin;

    public ScreenCrop(CropArea area)
    {
        this.area = area;
        var length = checked(area.Width * area.Height * 4);
        image = new byte[54 + length];
        image[0] = (byte)'B';
        image[1] = (byte)'M';
        SetInt(2, image.Length);
        SetInt(10, 54);
        SetInt(14, 40);
        SetInt(18, area.Width);
        SetInt(22, area.Height);
        image[26] = 1;
        image[28] = 32;
        SetInt(34, length);

        dc = CreateCompatibleDC(0);
        if (dc == 0) throw new InvalidOperationException("Could not create the capture device.");
        bitmap = CreateDIBSection(dc, image.AsSpan(14, 40), 0, out pixels, 0, 0);
        if (bitmap == 0 || (oldBitmap = SelectObject(dc, bitmap)) is 0 or -1)
        {
            Dispose();
            throw new InvalidOperationException("Could not allocate the capture buffer.");
        }
    }

    public byte[]? Read()
    {
        var window = GetForegroundWindow();
        if (window == 0 || IsIconic(window) || !GetClientRect(window, out var rect) ||
            area.X + area.Width > rect.Right || area.Y + area.Height > rect.Bottom ||
            !IsGame(window))
            return null;

        var origin = new Point { X = area.X, Y = area.Y };
        if (!ClientToScreen(window, ref origin) || !IsVisible(window, origin))
            return null;

        var screen = GetDC(0);
        if (screen == 0) return null;
        try
        {
            if (!BitBlt(dc, 0, 0, area.Width, area.Height, screen, origin.X, origin.Y, CopyPixels))
                return null;
            if (!IsVisible(window, origin)) return null;
            Marshal.Copy(pixels, image, 54, image.Length - 54);
            capturedWindow = window;
            capturedOrigin = origin;
            return image;
        }
        finally
        {
            ReleaseDC(0, screen);
        }
    }

    public bool IsCurrent()
    {
        var origin = new Point { X = area.X, Y = area.Y };
        return ClientToScreen(capturedWindow, ref origin) && origin.X == capturedOrigin.X &&
            origin.Y == capturedOrigin.Y && IsVisible(capturedWindow, origin);
    }

    private static bool IsGame(nint window)
    {
        GetWindowThreadProcessId(window, out var pid);
        if (pid == 0) return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName.ToLowerInvariant() is
                "blackdesert64" or "blackdesert32" or "blackdesertonline" or "blackdesert";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private bool IsVisible(nint window, Point origin)
    {
        if (GetForegroundWindow() != window) return false;
        return IsInside(window, origin.X, origin.Y) &&
            IsInside(window, origin.X + area.Width - 1, origin.Y) &&
            IsInside(window, origin.X, origin.Y + area.Height - 1) &&
            IsInside(window, origin.X + area.Width - 1, origin.Y + area.Height - 1) &&
            IsInside(window, origin.X + area.Width / 2, origin.Y + area.Height / 2);
    }

    private static bool IsInside(nint window, int x, int y) =>
        GetAncestor(WindowFromPoint(new Point { X = x, Y = y }), 2) == window;

    private void SetInt(int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(offset, 4), value);

    public void Dispose()
    {
        if (oldBitmap != 0 && oldBitmap != -1) SelectObject(dc, oldBitmap);
        if (bitmap != 0) DeleteObject(bitmap);
        if (dc != 0) DeleteDC(dc);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [LibraryImport("user32.dll")] private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] private static partial int GetWindowThreadProcessId(nint window, out uint pid);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsIconic(nint window);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetClientRect(nint window, out Rect rect);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ClientToScreen(nint window, ref Point point);
    [LibraryImport("user32.dll")] private static partial nint WindowFromPoint(Point point);
    [LibraryImport("user32.dll")] private static partial nint GetAncestor(nint window, uint flags);
    [LibraryImport("user32.dll")] private static partial nint GetDC(nint window);
    [LibraryImport("user32.dll")] private static partial int ReleaseDC(nint window, nint dc);
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll")] private static partial nint CreateDIBSection(nint dc, ReadOnlySpan<byte> info, uint usage, out nint bits, nint section, uint offset);
    [LibraryImport("gdi32.dll")] private static partial nint SelectObject(nint dc, nint item);
    [LibraryImport("gdi32.dll")] private static partial int DeleteObject(nint item);
    [LibraryImport("gdi32.dll")] private static partial int DeleteDC(nint dc);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint dc, int x, int y, int width, int height, nint source, int sx, int sy, uint operation);
}

internal sealed record CropArea(int X, int Y, int Width, int Height)
{
    public static CropArea Parse(string value)
    {
        var fields = value.Split(',');
        if (fields.Length != 4 || fields.Any(field => !int.TryParse(field, out _)))
            throw new ArgumentException("Use --durability-region x,y,width,height.");
        var values = Array.ConvertAll(fields, int.Parse);
        var area = new CropArea(values[0], values[1], values[2], values[3]);
        if (area.X is < 0 or > 16384 || area.Y is < 0 or > 16384 ||
            area.Width is < 1 or > 1024 || area.Height is < 1 or > 256)
            throw new ArgumentException("Durability region must fit within 1024 by 256 pixels.");
        return area;
    }
}
