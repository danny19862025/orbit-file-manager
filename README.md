# Orbit

A fast, modern file manager for Windows 10/11, built with WPF and .NET 10.

## Features

- **Four views** — Grid (thumbnails and colour tags), Details (metadata and sorting), Miller Columns, and Tree
- **Tabs**, breadcrumbs with subfolder menus, back/forward/up, bookmarks, recent folders and colour tags
- **Find anything** (Ctrl+K) — fuzzy search across every local drive, with a background index and live updates
- **Quick preview** (Space) — images, text and code, PDF, audio and video, ZIP contents and folders
- **Command bar** — New, Cut, Copy, Paste, Rename, Share, Delete, Properties, Sort, View and Filter (by kind and date)
- **Drag and drop** — onto folders, tabs, the path bar and the sidebar; box-select; folders spring open while dragging; a Dropzone shelf
- **Network** — SMB/Samba and WebDAV through Windows, SFTP over SSH, Google Drive / Dropbox / OneDrive / iCloud sync folders, and optional rclone mounts
- **Themes** — Catppuccin Mocha, Tokyo Night, Rosé Pine, Nord and Catppuccin Latte, with an optional translucent (Mica) window
- **Icon packs** — Windows icons or the Material Icon Theme
- **Settings** — editable keyboard shortcuts, sidebar sections, and an update check

## Build

Requires the .NET 10 SDK on Windows.

```powershell
dotnet build -c Release
```

The app is written to `bin\Release\net10.0-windows10.0.19041.0\Orbit.exe`.

## Support

If Orbit is useful to you, you can [buy me a coffee](https://buymeacoffee.com/danny1986).

## Credits

See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) for the open-source components and icon set Orbit uses.
