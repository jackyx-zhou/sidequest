using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace Sidequest;

public sealed class BrowserItem : INotifyPropertyChanged
{
    private BitmapSource? _thumbnail;

    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public string Extension => IsDirectory ? "Folder" : (Path.GetExtension(Name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext : "File");
    public bool IsImage => !IsDirectory && MediaTypes.IsImage(Name);
    public bool IsVideo => !IsDirectory && MediaTypes.IsVideo(Name);
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public static BrowserItem FromPath(string path)
    {
        var info = new FileInfo(path);
        var directory = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
        var modified = directory ? Directory.GetLastWriteTime(path) : info.LastWriteTime;
        long size = directory ? 0 : info.Length;
        return new BrowserItem { Name = Path.GetFileName(path), FullPath = path, IsDirectory = directory, Size = size, Modified = modified };
    }
}

public static class MediaTypes
{
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { ".bmp", ".gif", ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".ico", ".wdp", ".jxr" };
    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase) { ".asf", ".avi", ".m2ts", ".m4v", ".mkv", ".mov", ".mp4", ".mpeg", ".mpg", ".mts", ".ts", ".vob", ".webm", ".wmv" };
    public static bool IsImage(string name) => Images.Contains(Path.GetExtension(name));
    public static bool IsVideo(string name) => Videos.Contains(Path.GetExtension(name));
    public static bool IsMedia(string name) => IsImage(name) || IsVideo(name);
}
