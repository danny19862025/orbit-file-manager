<img src="Assets/orbit-256.png" width="96" alt="Orbit icon" align="right">

# Orbit

**A fast, modern file manager for Windows 10 and 11 — and Linux.**
Tabs, four views, instant search, previews, archives, split panes, network drives, themes and icon packs — in one calm, keyboard-friendly window.

[**🌐 Visit the Orbit Website**](https://danny19862025.github.io/orbit-file-manager/) · **⬇ Download:** [**Windows**](https://github.com/danny19862025/orbit-file-manager/releases/latest/download/OrbitSetup-1.0.1.exe) · [**Linux**](https://github.com/danny19862025/orbit-file-manager/releases/latest/download/Orbit-1.0.0-linux-x64.tar.gz) · [all releases](https://github.com/danny19862025/orbit-file-manager/releases/latest) · [Report a bug](https://github.com/danny19862025/orbit-file-manager/issues) · [Buy me a coffee](https://buymeacoffee.com/danny1986)

![Orbit](docs/screenshots/main-grid.png)

---

## Contents

- [Install](#install) — [Windows](#windows--step-by-step) · [Linux](#linux) · [Update](#update) · [Uninstall](#uninstall) · [Installation problems](#installation-problems)
- [The main window](#the-main-window) — sidebar, tabs, path bar, command bar, status bar
- [Views](#views) — Grid, Details, Columns, Tree
- [Working with files](#working-with-files) — open, create, copy, move, rename, delete, drag and drop, tags, Properties
- [Find anything](#find-anything-ctrlk) · [Quick preview](#quick-preview-space) · [Split view](#split-view-ctrl)
- [Archives](#archives) · [Disk space map](#disk-space-map) · [Convert](#convert) · [Locked folders](#locked-folders) · [Recycle Bin](#recycle-bin)
- [Network and cloud](#network-and-cloud)
- [Settings](#settings) — Appearance, Icons, General, Network, Shortcuts, About
- [Keyboard shortcuts](#keyboard-shortcuts) · [Command line](#command-line)
- [Where Orbit keeps its data](#where-orbit-keeps-its-data) · [Privacy](#privacy)
- [Build from source](#build-from-source) · [Credits and licence](#credits-and-licence)

---

## Install

Orbit is free. Every download contains everything Orbit needs — you **don't** have to install .NET or anything else first.

### Which file do I need?

All files are on the [Releases page](https://github.com/danny19862025/orbit-file-manager/releases/latest):

| Your computer | Download | What it is |
|---|---|---|
| Windows 10 / 11 | **[`OrbitSetup-1.0.1.exe`](https://github.com/danny19862025/orbit-file-manager/releases/latest/download/OrbitSetup-1.0.1.exe)** | The installer — **recommended**. |
| Windows 10 / 11 | [`Orbit-1.0.1-win-x64.zip`](https://github.com/danny19862025/orbit-file-manager/releases/latest/download/Orbit-1.0.1-win-x64.zip) | Portable copy: unzip anywhere and run `Orbit.exe`. Nothing is installed. |
| Linux (64-bit) | **[`Orbit-1.0.0-linux-x64.tar.gz`](https://github.com/danny19862025/orbit-file-manager/releases/latest/download/Orbit-1.0.0-linux-x64.tar.gz)** | Linux package with an install script. |
| Any | [`SHA256SUMS.txt`](https://github.com/danny19862025/orbit-file-manager/releases/latest/download/SHA256SUMS.txt) | Checksums, if you want to check your download wasn't damaged or changed. |

### Windows — step by step

**Requirements:** Windows 10 (version 1903 or later) or Windows 11, 64-bit. No administrator rights needed.

1. **Check your Windows.** Press <kbd>Win</kbd>+<kbd>R</kbd>, type `winver` and press <kbd>Enter</kbd> — you need Windows 10 version 1903 or newer, or Windows 11. (Almost every PC sold since 2010 is 64-bit.)
2. **Download the installer:** [`OrbitSetup-1.0.1.exe`](https://github.com/danny19862025/orbit-file-manager/releases/latest/download/OrbitSetup-1.0.1.exe). It's saved to your **Downloads** folder.
3. *(Optional)* **Check the download.** Open **PowerShell** in your Downloads folder and run
   ```powershell
   Get-FileHash .\OrbitSetup-1.0.1.exe
   ```
   The `Hash` it prints should be the same as the `OrbitSetup-1.0.1.exe` line in [`SHA256SUMS.txt`](https://github.com/danny19862025/orbit-file-manager/releases/latest/download/SHA256SUMS.txt) (letter case doesn't matter).
4. **Run the installer** — double-click `OrbitSetup-1.0.1.exe`.
   > **If you see “Windows protected your PC”:** Orbit isn't code-signed yet, so Microsoft SmartScreen warns about it the first time.
   > Click **More info**, then **Run anyway**.
5. **Choose your options** in the *Orbit Setup* window:

   <img src="docs/screenshots/installer.png" width="480" alt="Orbit Setup">

   - **Install location** — `%LocalAppData%\Programs\Orbit` by default (that's `C:\Users\<you>\AppData\Local\Programs\Orbit`). Click **Browse…** to pick another folder; an `Orbit` folder is created inside it.
   - **Create a desktop shortcut** — tick it if you want an icon on your desktop.
   - **Open Orbit when setup finishes** — ticked by default.
6. Click **Install**. It takes a few seconds; there's no administrator prompt, because Orbit installs **for your account only**.
7. **Start Orbit** — it opens by itself if you left the box ticked. Later, open the **Start** menu and type **Orbit**. Right-click it › **Pin to taskbar** to keep it one click away.

That's it. The installer also registered Orbit in **Settings › Apps**, so you can remove it like any other app.

**Portable copy instead (no installer):**

1. Download [`Orbit-1.0.1-win-x64.zip`](https://github.com/danny19862025/orbit-file-manager/releases/latest/download/Orbit-1.0.1-win-x64.zip).
2. Right-click the zip › **Properties**, tick **Unblock** if it's there, then **OK** (stops Windows warning about every file inside).
3. Right-click › **Extract All…** and choose a folder — for example a USB stick or `D:\Apps\Orbit`.
4. Open that folder and double-click **`Orbit.exe`**. Nothing is added to the Start menu or Settings › Apps; to remove it, delete the folder.

**Silent install (for scripts and IT):** `OrbitSetup-1.0.1.exe /quiet` installs with the defaults; add `/dir="D:\Apps\Orbit"` to choose the folder and `/desktop` for a desktop shortcut.

### Linux

![Orbit on Linux](docs/screenshots/linux-grid.png)

Orbit runs on Linux too — the same app, with the same window, views, menus, shortcuts, themes, icon packs and tools —
using your desktop's **Trash**, mounted **drives** with Eject, **GVfs** for SMB / WebDAV shares, your **icon theme** and
thumbnails, and **mpv** for video and music previews. See **[Orbit for Linux](linux/README.md)** for everything that works differently.

**Requirements:** a 64-bit (x86-64) Linux with a desktop — KDE Plasma, GNOME, Xfce, Cinnamon, Hyprland and others, on
Wayland or X11 (Orbit uses XWayland on Wayland). No root / `sudo` needed for Orbit itself.

1. **Check your computer is 64-bit x86.** Open a terminal and run
   ```sh
   uname -m
   ```
   It should print `x86_64`. (ARM computers such as a Raspberry Pi print `aarch64`; there's no ARM download yet — see [Build from source](#build-from-source).)
2. **Download the package** — either click [`Orbit-1.0.0-linux-x64.tar.gz`](https://github.com/danny19862025/orbit-file-manager/releases/latest/download/Orbit-1.0.0-linux-x64.tar.gz) (it's saved to `~/Downloads`), or in the terminal:
   ```sh
   cd ~/Downloads
   wget https://github.com/danny19862025/orbit-file-manager/releases/latest/download/Orbit-1.0.0-linux-x64.tar.gz
   ```
   (No `wget`? Use `curl -LO <same address>` instead.)
3. *(Optional)* **Check the download:**
   ```sh
   cd ~/Downloads
   wget https://github.com/danny19862025/orbit-file-manager/releases/latest/download/SHA256SUMS.txt
   sha256sum -c --ignore-missing SHA256SUMS.txt
   ```
   It should print `Orbit-1.0.0-linux-x64.tar.gz: OK`.
4. **Unpack it:**
   ```sh
   cd ~/Downloads
   tar xf Orbit-1.0.0-linux-x64.tar.gz
   cd Orbit-1.0.0-linux-x64
   ```
   (Or right-click the file in your file manager › *Extract here*, then open a terminal in the new folder.)
5. **Install it:**
   ```sh
   ./install.sh
   ```
   This takes a second and installs **for your account only**:
   - the program → `~/.local/opt/orbit`
   - a launcher in your **app menu** → `~/.local/share/applications/orbit-file-manager.desktop`
   - the `orbit` command → `~/.local/bin/orbit`
   - the app icon → `~/.local/share/icons/hicolor/256x256/apps/orbit-file-manager.png`

   It prints `Installed Orbit to …` when it's done. You can now delete the downloaded `.tar.gz` and the unpacked folder.
6. **Start Orbit** — open your app menu and search for **Orbit** (it's under *System* / *Utilities* as a *File Manager*), or type `orbit` in a terminal.
   Right-click its taskbar icon › *Pin to Task Manager* / *Add to Favourites* to keep it handy.
   - If the terminal says `orbit: command not found`, `~/.local/bin` isn't on your PATH yet. Log out and back in, or add it:
     - **bash:** `echo 'export PATH="$HOME/.local/bin:$PATH"' >> ~/.bashrc`
     - **zsh:** `echo 'export PATH="$HOME/.local/bin:$PATH"' >> ~/.zshrc`
     - **fish:** `fish_add_path ~/.local/bin`
   - If it doesn't show up in the app menu straight away, log out and back in (KDE users can also run `kbuildsycoca6`).
7. *(Optional)* **Make Orbit your default app for folders** — so folders you open from other apps, the desktop or “Show in folder” open in Orbit:
   ```sh
   ~/.local/opt/orbit/install.sh --default
   ```
   (To go back, choose your old file manager in *System Settings › Default Applications*, or run `xdg-mime default org.kde.dolphin.desktop inode/directory` — use `org.gnome.Nautilus.desktop` on GNOME.)
8. *(Optional)* **Install the helpers** for the extra features. Orbit works without them and tells you when one is needed; most desktops already have several:

   | Helper | Turns on |
   |---|---|
   | `mpv` | Playing videos and music inside Quick preview |
   | `ffmpeg` | Video thumbnails, video / audio conversion, HEIC / AVIF pictures |
   | `poppler` (`pdftoppm`) | PDF previews and thumbnails |
   | `imagemagick` | Converting pictures to TIFF / BMP / GIF |
   | `gvfs` + `gvfs-smb` | SMB / Samba (Windows shares, NAS) and WebDAV |
   | `udisks2` | Eject for USB sticks, mounting ISO images |
   | `rclone` + `fuse3` | Mounting Google Drive / Dropbox as a folder |

   One command installs them all:
   - **Arch, Manjaro, EndeavourOS:** `sudo pacman -S --needed mpv ffmpeg poppler imagemagick gvfs gvfs-smb udisks2 rclone fuse3`
   - **Debian, Ubuntu, Linux Mint, Pop!_OS:** `sudo apt install mpv ffmpeg poppler-utils imagemagick gvfs-backends udisks2 rclone fuse3`
   - **Fedora:** `sudo dnf install mpv ffmpeg-free poppler-utils ImageMagick gvfs-smb udisks2 rclone fuse3`

Your settings are saved in `~/.config/Orbit/settings.json` — the same format as on Windows — and folders locked with a password open on either system.

## Update

**Windows:** open **Settings › About › Check for updates** in Orbit. If a newer version exists, click **Download**, run the new `OrbitSetup` — it recognises your install, shows **Update** instead of Install, closes Orbit if it's running, replaces the program files and keeps all your settings. For the portable copy, download the new zip and replace the folder.

**Linux:** **Settings › About › Check for updates** tells you when there's a new version and opens its page. Download the new `Orbit-<version>-linux-x64.tar.gz` and repeat steps 4–5 above (`tar xf …`, `./install.sh`). The installer closes Orbit if it's running, replaces the program and keeps your settings.

## Uninstall

**Windows:** **Settings › Apps › Installed apps › Orbit File Manager › Uninstall** (or run `Orbit.exe --uninstall`). Orbit asks before removing anything, then asks separately whether to also delete your settings — answer **No** to keep them for a later install. Only the folder the installer created is ever deleted. For the portable copy, just delete its folder.

**Linux:** in a terminal run
```sh
~/.local/opt/orbit/uninstall.sh            # removes Orbit, keeps your settings
~/.local/opt/orbit/uninstall.sh --purge    # removes Orbit and your settings and search index too
```
It removes the program, the app-menu launcher, the `orbit` command and the icon. If you'd made Orbit your default folder app, pick another one in *System Settings › Default Applications*.

## Installation problems

| Problem | Fix |
|---|---|
| Windows: *“Windows protected your PC”* | Orbit isn't code-signed yet. Click **More info › Run anyway**. Check the file against `SHA256SUMS.txt` first if you like (step 3). |
| Linux: `./install.sh: Permission denied` | The execute bit was lost (some archive tools do that). Run `bash install.sh` instead. |
| Linux: `orbit: command not found` | Add `~/.local/bin` to your PATH — see step 6. |
| Linux: Orbit isn't in the app menu | Log out and back in. On KDE you can run `kbuildsycoca6` instead. |
| Linux: videos don't play in Quick preview | Install `mpv` (step 8). **Open** always plays them in your usual player. |
| Linux: “needs GVfs” when connecting to a share | Install `gvfs` and `gvfs-smb` (step 8). |
| Anything else | [Report a bug](https://github.com/danny19862025/orbit-file-manager/issues) and attach `error.log` from `%AppData%\Orbit` (Windows) or `~/.config/Orbit` (Linux). |

---

## The main window

![Main window](docs/screenshots/main-grid.png)

### Title bar

- The **Orbit icon** — when Orbit opens, its little moon makes one lap round the planet while the window settles in (skipped if Windows animations are turned off).
- **⚙ Settings** (`Ctrl+,`) and **Hide sidebar**.
- Minimise / maximise / close. Orbit remembers its size and position.

### Sidebar

Drag the divider to resize it. Each section can be collapsed with its arrow, and Bookmarks, Tags, Drives, Network, Recent and Dropzone can be hidden in **Settings › General**.

| Section | What it does |
|---|---|
| **Places** | Home, Desktop, Documents, Downloads, Pictures, Music, Videos and the **Recycle Bin**. **Drag any folder here to pin it**; right-click a pinned place to move it up or down, copy its path or remove it. Drop files on a place to copy or move them there. |
| **Bookmarks** | Folders you starred with ☆ or `Ctrl+D`. |
| **Tags** | Colour tags you've given to files and folders. Click a tag to see everything with it, from any folder. Right-click to remove a tag from all items. |
| **Drives** | Every drive with a usage bar and free space. Right-click a drive for **Analyze disk space**, **Eject** (USB sticks and discs) and Properties. |
| **Network** | Saved servers (SMB, SFTP, WebDAV, Google Drive, Dropbox), detected cloud folders (OneDrive, Google Drive, Dropbox, iCloud) and **Connect to server…** — see [Network and cloud](#network-and-cloud). |
| **Recent** | Folders you visited recently (right-click › *Remove from Recent*). |
| **Dropzone** | A shelf for collecting files while you browse. Drag files onto it, go somewhere else, then drag them out — or right-click › *Copy here* / *Move here*. |

Middle-click a sidebar item to open it in a new tab. Right-click for Open, Open in new tab, Copy path, Open in Terminal, Open in Explorer and Properties.

### Tabs

`Ctrl+T` new tab, `Ctrl+W` close, `Ctrl+Tab` / `Ctrl+Shift+Tab` to switch, middle-click a folder to open it in a new tab. Drop files onto a tab to put them in that tab's folder. **Drag a tab onto the right half of the window to open it in [split view](#split-view-ctrl).** With *Reopen my tabs at startup* on, your tabs come back next time.

### Path bar

- **Back / Forward / Up** (`Alt+←` `Alt+→` `Alt+↑`, or the mouse's back and forward buttons) and **☆ Bookmark**.
- **Breadcrumbs** — click any part to jump there; click a `›` to pick one of its subfolders; drop files on a crumb to put them there.
- Click empty space in the bar (or press `Ctrl+L`) to **type or paste a path** — local folders, `\\server\share` network paths and `sftp://` addresses all work.
- **⟳ Refresh** (`F5`). Folders also refresh on their own when files change.

### Command bar

| Button | |
|---|---|
| **New** | Folder, Text document, Markdown document, JSON file, HTML page, PowerShell script. |
| **Cut · Copy · Paste · Rename · Share · Delete** | The usual file operations. **Share** opens the Windows share sheet. |
| **ⓘ Properties** | Orbit's Properties window — see [Properties](#properties). |
| **Sort** | Name, Date modified, Size, Type; ascending or descending. |
| **View** | Grid, Details, Columns, Tree; Split view; Hidden items; Photo and video thumbnails; Sidebar. |
| **Filter** | By **kind** — Folders, Documents, Pictures, Videos, Music, Archives, Code, Apps and shortcuts — and by **date modified** — Today, Yesterday, Last 7 days, Last 30 days, This year, Older than a year. **Filter by name…** (`Ctrl+F`); **Clear filters** resets. |
| **⋯ More** | Select all / none / invert, Copy path, Add to Dropzone, Open in Terminal, Open in File Explorer, Preview selected, **Analyze disk space…**, **Convert selected…**, Icons and theme…, Properties, Settings. |

### Status bar

Bottom-left: **Search everywhere** (`Ctrl+K`), **Filter this folder** (`Ctrl+F`) and **Show hidden items** (`Ctrl+H`). Bottom-right: how many folders and files are here, what's selected and its total size, and quick sort and view switches.

---

## Views

Switch with **View** or `Ctrl+Shift+1…4`. Each view keeps your sort order and selection.

| | |
|---|---|
| **Grid** — large icons and photo/video thumbnails, colour-tag dots. | ![Grid](docs/screenshots/photos.png) |
| **Details** — name, date, type and size columns; click a header to sort. | ![Details](docs/screenshots/details.png) |
| **Columns** — Finder-style Miller columns: each selected folder opens a new column to its right, and the last column previews the selected file. Arrow keys move through the hierarchy. | ![Columns](docs/screenshots/columns.png) |
| **Tree** — folders expand in place with ▸, so you can see a whole branch at once. | ![Tree](docs/screenshots/tree.png) |

**Hidden items:** `Ctrl+H` (or View › Hidden items). **Thumbnails:** with an icon pack active, View › *Photo and video thumbnails* keeps real thumbnails for pictures and videos.

---

## Working with files

- **Open:** double-click or `Enter`. Folders open in place (middle-click: new tab); files open in their default app. Right-click › **Open with…** to choose an app, **Show in folder** for search and tag results.
- **Select:** click, `Ctrl`-click, `Shift`-click, `Ctrl+A`, or **drag a box** on empty space. Type the first letters of a name to jump to it.
- **Create:** New › Folder (`Ctrl+Shift+N`) or a new text, Markdown, JSON, HTML or PowerShell file — Orbit starts renaming it straight away.
- **Copy, cut and paste:** `Ctrl+C`, `Ctrl+X`, `Ctrl+V` — works together with File Explorer. Right-click a folder › *Paste into folder*.
- **Rename:** `F2`.
- **Delete:** `Delete` sends to the Recycle Bin; `Shift+Delete` deletes permanently (Orbit asks first).
- **Copy path:** `Ctrl+Shift+C`.
- **Drag and drop:** onto folders, tabs, breadcrumbs, sidebar places, the Dropzone, the split pane, or other apps. Hold **Ctrl** to copy or **Shift** to move. Hovering over a folder while dragging springs it open.
- **Tags:** right-click › **Tags** to give items colour tags; they show as dots and in the sidebar's Tags section. *Clear tags* removes them.
- **Open in Terminal / Open in Explorer** from the right-click or ⋯ menu.

Copying, moving and deleting go through Windows itself, so you get the normal progress dialog, conflict prompts and undo.

### Properties

`Alt+Enter`, the ⓘ button or right-click › **Properties**. Shows type, location, size (folders are counted in the background), what a folder contains, created / modified / accessed dates, and **Read-only** and **Hidden** switches. **Windows properties…** opens the classic Windows dialog. For a whole drive, Orbit opens the Windows dialog with its used/free chart.

---

## Find anything (`Ctrl+K`)

A search box for **every local drive**. Type part of a name — the search is fuzzy, so `invc 2026` finds `Invoices 2026` — and results are ranked by how well they match. `↑` `↓` to move, `Enter` to open, `Ctrl+Enter` to open in a new tab.

Orbit builds an index of your drives in the background the first time (system folders such as `Windows` and `$Recycle.Bin` are skipped), keeps it up to date as files change, and caches it so later starts are instant.

## Quick preview (`Space`)

![Preview](docs/screenshots/preview.png)

Select a file and press `Space`. `←` `→` move through the folder, `Space` or `Esc` closes, **Open** opens the file.

| Kind | Examples |
|---|---|
| Pictures | jpg, png, gif, bmp, tiff, ico, webp, heic, avif, jxr, dds |
| Text and code | txt, md, json, xml, yaml, toml, ini, log, and source code |
| PDF | pages rendered with Windows' own PDF engine |
| Audio and video | plays inline — mp3, wav, flac, m4a, ogg, opus, mp4, mkv, mov, webm, avi… |
| ZIP | the list of files inside |
| Folders | what's inside |

## Split view (`Ctrl+\`)

![Split view](docs/screenshots/split.png)

A second pane on the right for moving and copying between two folders. It has its own up, swap and refresh buttons, and the usual keys (`Enter`, `Backspace`, `F5`, `Delete`, `Ctrl+C/X/V`, `Ctrl+Shift+C`, `Alt+Enter`, `Space`). Drag files between panes, drag the divider to resize, or **drag a tab onto the right half** of the window to open it there. Right-click a folder › **Open in split view**; right-click in the pane › *Open in the main pane*.

---

## Archives

![Archive](docs/screenshots/archive.png)

Open **ZIP, RAR, 7z and TAR** (including `.tar.gz`, `.tgz`, `.tar.bz2`, `.tar.xz`) and ZIP-based files such as `.jar`, `.apk`, `.nupkg`, `.vsix`, `.whl` and `.cbz` **like ordinary folders**:

- browse inside, sort, preview and open files (they're extracted to a temporary folder for you);
- **drag files out** or copy them;
- right-click an archive › **Extract to "name\"**, **Extract here**; inside an archive › *Extract to the archive's folder*, *Extract to…*, *Extract everything here / to…*;
- select files and folders › **Compress to ZIP**;
- **ISO, VHD and VHDX** disk images: **Mount** them as a drive, and **Eject** from the sidebar.

Archives are read-only inside Orbit. Password-protected entries are reported clearly instead of failing silently.

## Disk space map

![Disk space](docs/screenshots/disk-space.png)

Right-click a drive › **Analyze disk space**, or ⋯ › **Analyze disk space…** for the current folder. Orbit scans in the background and draws the biggest items as bubbles sized by the space they use.

- **Click** a bubble to highlight it — the footer shows its name, size and share of the folder, and the drive bar shows its share of the disk.
- **Double-click** a folder bubble (or right-click › *Look inside*) to drill in; **↑** goes back up.
- **Show in Orbit** brings Orbit to the front with that item, so you can decide what to do with it there.

The map is **read-only** — it never deletes anything.

## Convert

![Convert](docs/screenshots/convert.png)

Right-click pictures, videos or audio › **Convert…** (or ⋯ › *Convert selected…*).

- **Pictures** → JPG, PNG, TIFF, BMP or GIF, using Windows' own codecs. Each format card shows a **live estimate** of how much bigger or smaller the result will be. Choose the JPG quality and an optional maximum size (3840, 1920 or 1280 px). Transparent images get a white background in formats without transparency.
- **Video** → MP4 (H.264), WebM (VP9), animated GIF, or MP3 (the audio track). **Audio** → MP3, M4A, OGG or WAV. These use [ffmpeg](https://ffmpeg.org/download.html): Orbit finds it automatically if it's installed (for example `winget install ffmpeg`), and explains how to get it if not.

Converted files are saved next to the originals and **never overwrite anything** (`photo (2).jpg` if needed). Several files of the same kind convert in one go.

## Locked folders

![Lock](docs/screenshots/lock.png)

Right-click a folder › **Lock with password…** to turn it into a single encrypted **`.orbitlock`** file. Double-click the file (or right-click › **Unlock…**) and enter the password to get the folder back.

- **AES-256-GCM** encryption in 1 MB chunks, with a key made from your password by **PBKDF2-SHA256 (600,000 iterations)**. Wrong passwords, changed bytes and cut-off files are all detected.
- Nothing is ever written to disk unencrypted, and the password is never stored.
- Before the original folder is touched, Orbit reads the whole locked file back to check it. The original then goes to the **Recycle Bin** (you can untick this) — empty the bin afterwards so no unlocked copy is left.
- Unlocking restores folders, files and modification dates exactly, next to the locked file.
- Orbit refuses to lock whole drives, Windows folders or your entire user folder.

> ⚠️ **A forgotten password cannot be recovered — by anyone.**

## Recycle Bin

Click **Recycle Bin** in Places to browse it inside Orbit. The columns show each item's **original location** and **date deleted**.

- Right-click an item › **Restore** (back to exactly where it was, recreating the folder if needed), **Preview**, **Show original folder**, or **Delete permanently…**
- Right-click empty space › **Restore all items** or **Empty Recycle Bin…** (Windows confirms).
- **Drop files on the Recycle Bin** in the sidebar to delete them.

Orbit reads Windows' own Recycle Bin records, so it always agrees with File Explorer.

---

## Network and cloud

**Connect to server…** (sidebar › Network, or Settings › Network):

| Type | How it works |
|---|---|
| **SMB / Samba** | Windows file shares (`\\server\share`), NAS boxes, Samba on Linux. The connection is made by Windows; Orbit doesn't save the password. |
| **SFTP (SSH)** | Built in. Browse, open, upload (drag in, or *Upload files…*), download (*Download to Downloads / to…*), rename and delete on any SSH server. Password or private key — your `~/.ssh` keys are tried automatically. **Open SSH terminal** from the server's menu. |
| **WebDAV** | Nextcloud, ownCloud and other WebDAV servers, with HTTPS if you like. |
| **Google Drive / Dropbox** | Uses the official desktop app's synced folder when it's installed; otherwise Orbit can mount it as a drive letter with **rclone** and WinFsp (links to both are in the dialog). |

Saved servers appear in the sidebar with a status dot; right-click for Connect, Open in new tab, Edit…, Copy address, Disconnect and Remove from sidebar. **OneDrive, Google Drive, Dropbox and iCloud** sync folders are detected automatically.

**Trusted SSH servers:** the first time you connect to an SFTP server Orbit remembers its identity and warns you if it ever changes. Forget a server in Settings › Network if it was reinstalled.

---

## Settings

Open with ⚙ or `Ctrl+,`. Every change applies instantly and is remembered.

### Appearance

![Appearance](docs/screenshots/settings-appearance.png)

**Themes** — each restyles the whole app:

| Community favourites | Editor classics | Dynamic |
|---|---|---|
| Catppuccin Mocha *(default)* · Catppuccin Latte · Tokyo Night · Gruvbox Dark · Gruvbox Light · Everforest · Kanagawa · Rosé Pine · Nord | One Dark Pro · Monokai Pro · Solarized Dark · Solarized Light · Ayu Dark · Ayu Mirage | **System / Wallpaper** — follows your [pywal](https://github.com/dylanaraps/pywal) palette (`~/.cache/wal/colors.json`) if you use pywal or winwal, otherwise your Windows accent colour and light/dark mode. Updates live when either changes. |

**Window:**

- **Translucent window (Mica)** — lets the Windows 11 acrylic blur show through.
- **Transparency** — 5 % to 90 %. The sidebar stays a little clearer than the file list so names stay readable. Settings, Properties and Orbit's other windows share the same glass.

### Icons

![Icons](docs/screenshots/settings-icons.png)

| Pack | |
|---|---|
| **Windows** | Your system's own icons and thumbnails. |
| **Material** *(default)* | Material Icon Theme — 1,100+ colourful file and folder icons. |
| **Papirus · Adwaita · Colloid · Candy** | Popular Linux icon themes. |
| **Phosphor** | Duotone line icons drawn in your theme's colours. |
| **Devicon** | Programming-language and tool logos, with Material for everything else. |
| **Freedesktop theme…** | Point Orbit at **any** Linux icon theme folder (one with `index.theme`, e.g. from gnome-look.org). |

**Previews:** *Keep photo and video thumbnails* shows real thumbnails for media even with an icon pack.

### General

![General](docs/screenshots/settings-general.png)

- **Browsing:** Show hidden items, Reopen my tabs at startup.
- **Sidebar:** show or hide Bookmarks, Tags, Drives, Network, Recent and Dropzone (Places is always shown).
- **View:** default layout (Grid, Details, Columns, Tree), sort by Name / Date / Size / Type, sort descending.

### Network

![Network](docs/screenshots/settings-network.png)

**Connect to server…**, **Re-detect cloud folders**, and the list of **Trusted SSH servers** (with Forget).

### Shortcuts

![Shortcuts](docs/screenshots/settings-shortcuts.png)

Every shortcut in the table below can be changed: click it and press the new keys (`Esc` cancels, `Backspace` removes it). **Reset all** restores the defaults. The **Fixed** list shows the built-in ones that always work the same way.

### About

![About](docs/screenshots/settings-about.png)

Version, **Check for updates**, links to the **GitHub repository**, **Report a bug**, **Buy me a coffee**, **Open settings folder**, and credits.

---

## Keyboard shortcuts

| General | | Tabs | |
|---|---|---|---|
| Find anything | `Ctrl+K` | New tab | `Ctrl+T` |
| Settings | `Ctrl+,` | Close tab | `Ctrl+W` |
| Preview | `Space` | Next / previous tab | `Ctrl+Tab` / `Ctrl+Shift+Tab` |
| Properties | `Alt+Enter` | | |

| Navigation | | View | |
|---|---|---|---|
| Back / Forward | `Alt+←` / `Alt+→` | Filter this folder | `Ctrl+F` |
| Up one folder | `Alt+↑` | Show / hide hidden items | `Ctrl+H` |
| Type a path | `Ctrl+L` | Grid / Details / Columns / Tree | `Ctrl+Shift+1` … `4` |
| Refresh | `F5` | Split view | `Ctrl+\` |
| Bookmark this folder | `Ctrl+D` | | |

| Files | | Fixed (always the same) | |
|---|---|---|---|
| New folder | `Ctrl+Shift+N` | Open the selected item | `Enter` |
| Rename | `F2` | Back | `Backspace` / mouse back |
| Copy / Cut / Paste | `Ctrl+C` / `Ctrl+X` / `Ctrl+V` | Clear selection or filter | `Esc` |
| Copy path | `Ctrl+Shift+C` | Context menu | `Menu` / `Shift+F10` |
| Delete to Recycle Bin | `Delete` | Open folder in a new tab | Middle-click |
| Delete permanently | `Shift+Delete` | Box-select | Drag on empty space |
| | | Copy / move while dragging | Hold `Ctrl` / `Shift` |

## Command line

```
Orbit.exe [folder or file]           open there (a file is selected in its folder; an archive opens like a folder)
Orbit.exe --preview <file>           open the file's folder and preview it
Orbit.exe --space <folder>           open the disk space map
Orbit.exe --convert <file>           open Convert for a file
Orbit.exe --lock <folder>            lock a folder with a password
Orbit.exe --unlock <file.orbitlock>  unlock a locked folder
Orbit.exe recycle:                   open the Recycle Bin
Orbit.exe --uninstall                uninstall a copy installed with OrbitSetup
```

## Where Orbit keeps its data

| | |
|---|---|
| `%AppData%\Orbit\settings.json` | All settings: tabs, bookmarks, tags, pinned places, saved servers (never passwords), theme, shortcuts… |
| `%AppData%\Orbit\error.log` | Errors, if any — attach it to bug reports. |
| `%LocalAppData%\Orbit\search-index.bin` | The *Find anything* index cache (safe to delete; it's rebuilt). |
| `%LocalAppData%\Programs\Orbit` | The program itself, when installed with the installer. |

## Privacy

Orbit has **no telemetry and no accounts**. It only uses the network when you connect to a server, open a cloud folder, or press **Check for updates** (which asks GitHub for the latest release). Passwords you type for servers or locked folders are never saved by Orbit.

---

## Build from source

Requires Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
git clone https://github.com/danny19862025/orbit-file-manager.git
cd orbit-file-manager
dotnet build -c Release
.\bin\Release\net10.0-windows10.0.19041.0\Orbit.exe
```

**Release packages** — builds the self-contained portable zip, the installer and checksums into `dist\`:

```powershell
pwsh -File tools\build-release.ps1
```

The installer (`installer\`) is a small .NET Framework 4.8 app, so it runs on any Windows 10/11 without installing anything. `tools\make-icon.ps1` redraws the app icon (`Assets\orbit.ico`).

**Linux:** `dotnet run --project linux`, and `linux/build-release.sh` for the tarball — see [linux/README.md](linux/README.md).

### Project layout

| | |
|---|---|
| `MainWindow.*.cs` | The main window, split by area: views, sidebar, search, preview, split view, archives, network, drag and drop, tools, Recycle Bin, keys, intro animation. |
| `Themes.cs`, `WallpaperTheme.cs`, `Glass.cs` | Themes, the wallpaper theme and translucency. |
| `IconPack.cs`, `Freedesktop.cs`, `IconService.cs` | Icon packs, shell icons and thumbnails. |
| `Archives.cs`, `FolderLock.cs`, `Converter.cs`, `RecycleBin.cs`, `Network.cs`, `SearchIndex.cs` | Archives, locked folders, conversion, Recycle Bin, network and cloud, search index. |
| `*Window.xaml` | Settings, Properties, Connect, Convert, Lock and Disk space windows. |
| `installer\` | `OrbitSetup.exe`. |
| `linux\` | Orbit for Linux (Avalonia) — a separate project the Windows build never touches. |

---

## Support

If Orbit is useful to you, you can [buy me a coffee](https://buymeacoffee.com/danny1986) ☕ — and [bug reports and ideas](https://github.com/danny19862025/orbit-file-manager/issues) are always welcome.

## Credits and licence

Orbit is released under the [MIT License](LICENSE).

It builds on [SSH.NET](https://github.com/sshnet/SSH.NET) (SFTP), [SharpCompress](https://github.com/adamhathcock/sharpcompress) (archives), [SharpVectors](https://github.com/ElinamLLC/SharpVectors) (SVG icons), the [Material Icon Theme](https://github.com/material-extensions/vscode-material-icon-theme), [Papirus](https://github.com/PapirusDevelopmentTeam/papirus-icon-theme), [Adwaita](https://gitlab.gnome.org/GNOME/adwaita-icon-theme), [Colloid](https://github.com/vinceliuice/Colloid-icon-theme), [candy-icons](https://github.com/EliverLara/candy-icons), [Phosphor](https://github.com/phosphor-icons/core) and [Devicon](https://github.com/devicons/devicon). The Linux icon themes ship as separate files under their own licences (GPL-3.0, LGPL-3.0 / CC BY-SA 3.0) — see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) and `icons\licenses`.
