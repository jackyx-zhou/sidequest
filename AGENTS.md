# Agent guidance

## Project

- Sidequest is a native Windows WPF media browser targeting .NET 8. Keep file browsing and media display native; do not replace them with an HTML page or browser workflow.
- Preserve the read-only browsing model. Never modify, move, or delete user media files as part of browsing.
- The app is designed for Steam Big Picture and controller use. Keep keyboard and controller navigation usable when changing controls or focus behavior.
- Windows Shell thumbnails and Windows-installed media codecs are intentional lightweight integrations. BitLocker unlocking and VeraCrypt mounting remain the responsibility of Windows and VeraCrypt.

## Changes

- Prefer a feature branch and pull request for changes. Keep `main` clean and make each PR focused so features can be reviewed and shipped independently without cluttering the main commit history.
- Do not commit generated output or IDE state such as `bin/`, `obj/`, `.idea/`, `.vs/`, or user-specific settings.
- Keep project documentation and user-facing controls consistent with actual app behavior.
- For UI changes, preserve the dark, high-contrast, controller-friendly design and ensure interactive controls can be reached with both keyboard and controller.

## Build

- Build from the repository root with `dotnet build .\Sidequest.csproj -c Release`.
- The project requires the .NET 8 SDK and Windows Desktop targeting pack.
