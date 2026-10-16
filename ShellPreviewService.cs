using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Sidequest;

internal static class ShellPreviewService
{
    private static readonly Guid ImageFactoryId = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");
    private const uint ShgfiIcon = 0x00000100;
    private const uint ShgfiLargeIcon = 0x00000000;
    private const uint ShgfiUseFileAttributes = 0x00000010;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeNormal = 0x00000080;

    public static BitmapSource? GetPreview(string path, bool isDirectory, int edge)
    {
        if (!isDirectory && MediaTypes.IsImage(path))
        {
            var imagePreview = GetImagePreview(path, edge);
            if (imagePreview is not null) return imagePreview;
        }

        var thumbnail = GetShellThumbnail(path, edge);
        if (thumbnail is not null) return thumbnail;
        return GetShellIcon(path, isDirectory);
    }

    private static BitmapSource? GetImagePreview(string path, int edge)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = edge;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private static BitmapSource? GetShellThumbnail(string path, int edge)
    {
        IShellItemImageFactory? factory = null;
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            var iid = ImageFactoryId;
            var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
            if (hr < 0 || factory is null) return null;
            hr = factory.GetImage(new SizeNative(edge, edge), 0x00000009, out hBitmap); // thumbnail only + allow larger source
            if (hr < 0 || hBitmap == IntPtr.Zero) return null;
            // Keep the bitmap's actual dimensions. Forcing every thumbnail into an edge-by-edge
            // bitmap can stretch small Shell previews and make them look soft in the grid.
            var source = Imaging.CreateBitmapSourceFromHBitmap(hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch { return null; }
        finally
        {
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (factory is not null && Marshal.IsComObject(factory)) Marshal.ReleaseComObject(factory);
        }
    }

    private static BitmapSource? GetShellIcon(string path, bool isDirectory)
    {
        try
        {
            var info = new ShellFileInfo();
            var flags = ShgfiIcon | (isDirectory ? ShgfiUseFileAttributes : 0);
            var attrs = isDirectory ? FileAttributeDirectory : FileAttributeNormal;
            if (SHGetFileInfo(path, attrs, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), flags) == IntPtr.Zero || info.Icon == IntPtr.Zero) return null;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally { DestroyIcon(info.Icon); }
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [StructLayout(LayoutKind.Sequential)] private struct SizeNative { public int Width; public int Height; public SizeNative(int width, int height) { Width = width; Height = height; } }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SizeNative size, uint flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindingContext, ref Guid interfaceId, out IShellItemImageFactory factory);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint fileAttributes, ref ShellFileInfo info, uint fileInfoSize, uint flags);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
}
