# Sidequest

Sidequest is a native Windows media browser designed for Steam Big Picture and controller use. It is a .NET 8 WPF app; it does not use an HTML page or a browser to browse files or display media.

## Build and launch

Install the .NET 8 SDK, then run from this folder:

```powershell
dotnet build .\Sidequest.csproj -c Release
dotnet run --project .\Sidequest.csproj
```

To add it to Steam, use **Games → Add a Non-Steam Game** and select `bin\Release\net8.0-windows\Sidequest.exe` after building. Launch it from Big Picture. A framework-dependent build uses the installed .NET 8 Desktop Runtime and stays smaller than a self-contained publish.

## Use

- Starts at `C:\`. Type or paste any accessible Windows path, use the drive selector, breadcrumbs, or **Parent** to navigate.
- The grid uses Windows Shell thumbnails and falls back to the file or folder icon. It reads only the current folder and does not modify files.
- Open images and videos with Enter, A, or a double-click. Images display in the app; videos use Windows Media Foundation codecs. If Windows lacks a codec, the app reports the playback failure.
- D-pad or left stick changes selection; A opens; B returns/closes and asks to quit at a drive root; LB/RB changes media while viewing. Keyboard arrows, Enter, Backspace, Escape, left/right, and Space provide keyboard equivalents. Ctrl+Q opens the quit confirmation from anywhere, and Alt+F4 or the window close control also asks before exiting.
- Unlock BitLocker in Windows or mount VeraCrypt volumes in VeraCrypt, then use **Refresh drives**. Sidequest does not handle or store disk passwords.

## Notes

Windows protected folders may still deny listing their contents; Sidequest reports the problem and remains usable. Explorer thumbnail availability depends on the installed Windows thumbnail handlers. The app currently supports XInput-compatible controllers and common Windows image/video extensions.
