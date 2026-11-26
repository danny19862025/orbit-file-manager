<img src="Assets/orbit-256.png" width="96" alt="Orbit icon" align="right">

# Orbit

A fast, modern file manager for Windows 10/11, built with WPF and .NET 10.

## Features

- **Four views** — Grid (thumbnails and colour tags), Details (metadata and sorting), Miller Columns, and Tree
- **Tabs**, breadcrumbs with subfolder menus, back/forward/up, bookmarks, recent folders and colour tags
- **Find anything** (Ctrl+K) — fuzzy search across every local drive, with a background index and live updates
- **Quick preview** (Space) — images, text and code, PDF, audio and video, ZIP contents and folders
- **Command bar** — New, Cut, Copy, Paste, Rename, Share, Delete, Properties, Sort, View and Filter (by kind and date)
- **Drag and drop** — onto folders, tabs, the path bar and the sidebar; box-select; folders spring open while dragging; a Dropzone shelf
- **Archives** — browse ZIP, RAR, 7z, TAR (and .tar.gz/.bz2/.xz) like folders; extract, compress to ZIP, drag files out; mount ISO/VHD images
- **Split view** (Ctrl+\\) — a second pane for copying and moving between two folders; drop a tab on the right half to open it there
- **Disk space map** — see what's filling a drive or folder as bubbles, drill in, and send the biggest items to the Recycle Bin
- **Convert** — images to JPG, PNG, TIFF, BMP or GIF with live size estimates and resizing; video and audio to MP4, WebM, GIF, MP3, M4A, OGG or WAV when [ffmpeg](https://ffmpeg.org/download.html) is installed
- **Locked folders** — turn a folder into a single password-protected `.orbitlock` file (AES-256-GCM, PBKDF2-SHA256 key with 600,000 iterations). Orbit never stores the password, and a forgotten password can't be recovered
- **Recycle Bin** — browse it inside Orbit with original locations and deletion dates; restore, delete permanently, empty, or drop files on it
- **Network** — SMB/Samba and WebDAV through Windows, SFTP over SSH, Google Drive / Dropbox / OneDrive / iCloud sync folders, and optional rclone mounts
- **Themes** — Catppuccin Mocha and Latte, Tokyo Night, Gruvbox Dark and Light, Everforest, Kanagawa, Rosé Pine, Nord, One Dark Pro, Monokai Pro, Solarized Dark and Light, Ayu Dark and Mirage, plus **System / Wallpaper**, which follows your pywal palette or Windows accent colour live. Optional translucent (Mica) window
- **Icon packs** — Windows, Material, Papirus, Adwaita, Colloid, Candy, Phosphor (tinted to your theme) and Devicon, or point Orbit at any freedesktop.org icon theme folder
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

## License

Orbit is released under the [MIT License](LICENSE).
