// ============================================================================
// Orbit File Manager — Authentic Feature, Theme & Demo Data
// Sourced directly from https://github.com/danny19862025/orbit-file-manager
// ============================================================================

export const ORBIT_THEMES = [
  // Community favourites (exact hex values from Themes.cs)
  { name: "Catppuccin Mocha", group: "Community favourites", desc: "Warm pastel dark (default)", isLight: false, base: "#1E1E2E", mantle: "#181825", crust: "#11111B", surface0: "#313244", surface1: "#45475A", surface2: "#585B70", text: "#CDD6F4", subtext: "#A6ADC8", accent: "#CBA6F7", lavender: "#B4BEFE" },
  { name: "Catppuccin Latte", group: "Community favourites", desc: "Soft cream light", isLight: true, base: "#EFF1F5", mantle: "#E6E9EF", crust: "#DCE0E8", surface0: "#CCD0DA", surface1: "#BCC0CC", surface2: "#ACB0BE", text: "#4C4F69", subtext: "#6C6F85", accent: "#8839EF", lavender: "#7287FD" },
  { name: "Tokyo Night", group: "Community favourites", desc: "Deep indigo, electric blue", isLight: false, base: "#1A1B26", mantle: "#16161E", crust: "#101014", surface0: "#292E42", surface1: "#3B4261", surface2: "#545C7E", text: "#C0CAF5", subtext: "#A9B1D6", accent: "#7AA2F7", lavender: "#BB9AF7" },
  { name: "Gruvbox Dark", group: "Community favourites", desc: "Retro charcoal, orange", isLight: false, base: "#282828", mantle: "#1D2021", crust: "#141617", surface0: "#3C3836", surface1: "#504945", surface2: "#665C54", text: "#EBDBB2", subtext: "#BDAE93", accent: "#FE8019", lavender: "#FABD2F" },
  { name: "Gruvbox Light", group: "Community favourites", desc: "Retro parchment, amber", isLight: true, base: "#FBF1C7", mantle: "#F2E5BC", crust: "#EBDBB2", surface0: "#D5C4A1", surface1: "#BDAE93", surface2: "#A89984", text: "#3C3836", subtext: "#665C54", accent: "#AF3A03", lavender: "#B57614" },
  { name: "Everforest", group: "Community favourites", desc: "Muted forest green", isLight: false, base: "#2D353B", mantle: "#232A2E", crust: "#1E2326", surface0: "#343F44", surface1: "#475258", surface2: "#4F585E", text: "#D3C6AA", subtext: "#9DA9A0", accent: "#A7C080", lavender: "#83C092" },
  { name: "Kanagawa", group: "Community favourites", desc: "Sumi ink, wave blue", isLight: false, base: "#1F1F28", mantle: "#181820", crust: "#16161D", surface0: "#2A2A37", surface1: "#363646", surface2: "#54546D", text: "#DCD7BA", subtext: "#A6A69C", accent: "#7E9CD8", lavender: "#DCA561" },
  { name: "Rosé Pine", group: "Community favourites", desc: "Soft warm dark", isLight: false, base: "#191724", mantle: "#1F1D2E", crust: "#121019", surface0: "#26233A", surface1: "#403D52", surface2: "#524F67", text: "#E0DEF4", subtext: "#908CAA", accent: "#C4A7E7", lavender: "#EBBCBA" },
  { name: "Nord", group: "Community favourites", desc: "Cool arctic blue", isLight: false, base: "#2E3440", mantle: "#292E39", crust: "#242933", surface0: "#3B4252", surface1: "#434C5E", surface2: "#4C566A", text: "#ECEFF4", subtext: "#A3ADBF", accent: "#88C0D0", lavender: "#81A1C1" },

  // Editor classics
  { name: "One Dark Pro", group: "Editor classics", desc: "Cool grey, sky blue", isLight: false, base: "#282C34", mantle: "#21252B", crust: "#1B1F23", surface0: "#2C313A", surface1: "#3E4451", surface2: "#4B5263", text: "#ABB2BF", subtext: "#828997", accent: "#61AFEF", lavender: "#E06C75" },
  { name: "Monokai Pro", group: "Editor classics", desc: "Warm charcoal, neon yellow", isLight: false, base: "#2D2A2E", mantle: "#221F22", crust: "#19181A", surface0: "#403E41", surface1: "#5B595C", surface2: "#727072", text: "#FCFCFA", subtext: "#A9A7A9", accent: "#FFD866", lavender: "#FF6188" },
  { name: "Solarized Dark", group: "Editor classics", desc: "Teal slate, low glare", isLight: false, base: "#002B36", mantle: "#00252F", crust: "#001E26", surface0: "#073642", surface1: "#0E4654", surface2: "#586E75", text: "#93A1A1", subtext: "#839496", accent: "#268BD2", lavender: "#2AA198" },
  { name: "Solarized Light", group: "Editor classics", desc: "Warm parchment", isLight: true, base: "#FDF6E3", mantle: "#EEE8D5", crust: "#E4DCC4", surface0: "#E0D9C3", surface1: "#D3CBB2", surface2: "#93A1A1", text: "#073642", subtext: "#586E75", accent: "#268BD2", lavender: "#2AA198" },
  { name: "Ayu Dark", group: "Editor classics", desc: "Obsidian, tangerine", isLight: false, base: "#0B0E14", mantle: "#0D1017", crust: "#07090D", surface0: "#131721", surface1: "#1E232D", surface2: "#2D3640", text: "#BFBDB6", subtext: "#8A8F98", accent: "#FF8F40", lavender: "#E6B450" },
  { name: "Ayu Mirage", group: "Editor classics", desc: "Twilight, orange", isLight: false, base: "#1F2430", mantle: "#1C212B", crust: "#171B24", surface0: "#242936", surface1: "#2F3545", surface2: "#3E4555", text: "#CCCAC2", subtext: "#8A9199", accent: "#FFAD66", lavender: "#FFCC66" },
];

export const ANATOMY_HOTSPOTS = [
  {
    id: "titlebar",
    num: "1",
    x: 24,
    y: 5,
    title: "Title Bar & Orbital Intro",
    desc: "Clean custom chrome that remembers window size & position and greets you with a signature orbital animation.",
    bullets: [
      ["Orbital Intro:", "When Orbit opens, its peach moon makes one smooth lap behind and in front of the planet while the window settles in (respects Windows animation settings)."],
      ["Quick Controls:", "One-click Settings (Ctrl+,), Hide Sidebar toggle, and crisp native window controls."],
      ["Mica Glass:", "Translucent acrylic/Mica backdrop extends across the title bar, sidebar, and every secondary dialog."]
    ]
  },
  {
    id: "tabs",
    num: "2",
    x: 52,
    y: 5,
    title: "Multi-Tab Workspace",
    desc: "Full browser-grade tabbing built for effortless multi-folder workflows and instant split-screen.",
    bullets: [
      ["Keyboard & Mouse:", "Ctrl+T new tab, Ctrl+W close, Ctrl+Tab / Ctrl+Shift+Tab cycle, and middle-click any folder or sidebar item to open in a new tab."],
      ["Drop-to-Tab:", "Drag files directly onto any tab header to move or copy them straight into that tab's folder."],
      ["Drag-to-Split:", "Drag a tab onto the right half of the window to snap it immediately into Split View."],
      ["Session Restore:", "Enable 'Reopen my tabs at startup' to pick up right where you left off."]
    ]
  },
  {
    id: "pathbar",
    num: "3",
    x: 56,
    y: 12,
    title: "Interactive Path Bar & Breadcrumbs",
    desc: "Jump anywhere in your hierarchy, explore sibling subfolders, or type local, UNC, and SFTP addresses directly.",
    bullets: [
      ["History & Up:", "Back (Alt+←), Forward (Alt+→), Up (Alt+↑), mouse side buttons, and one-click ☆ Bookmark (Ctrl+D)."],
      ["Smart Breadcrumbs:", "Click any segment to jump there, click any › chevron to pick a subfolder, or drop files directly onto a crumb."],
      ["Direct Path Input:", "Click empty bar space or press Ctrl+L to type local paths, \\\\server\\share, or sftp:// addresses."],
      ["Live Auto-Refresh:", "Folders update automatically on filesystem changes, plus manual F5 refresh."]
    ]
  },
  {
    id: "commandbar",
    num: "4",
    x: 68,
    y: 19,
    title: "Command Bar & Smart Filters",
    desc: "Every daily file action, instant creation templates, sorting, and multi-dimensional filtering in one calm strip.",
    bullets: [
      ["Instant New:", "Create Folder (Ctrl+Shift+N), Text, Markdown, JSON, HTML, or PowerShell scripts — Orbit starts renaming immediately."],
      ["Core File Ops:", "Cut, Copy, Paste, Rename, Windows Share sheet, Delete, and Orbit's custom Properties window (Alt+Enter)."],
      ["Kind & Date Filters:", "Filter by 8 kinds (Folders, Docs, Pictures, Videos, Music, Archives, Code, Apps) and 6 date windows (Today → Older than a year)."],
      ["⋯ Power Menu:", "Select all/none/invert, Add to Dropzone, Open in Terminal/Explorer, Preview, Disk Space Map, Convert, and Theme shortcuts."]
    ]
  },
  {
    id: "sidebar",
    num: "5",
    x: 11,
    y: 52,
    title: "Modular Resizable Sidebar",
    desc: "Seven collapsible, toggleable sections with drag-to-pin, colour tags, live drive meters, and a built-in Dropzone shelf.",
    bullets: [
      ["Places & Pinning:", "Home, Desktop, Documents, Downloads, Pictures, Music, Videos, Recycle Bin — drag any folder here to pin it."],
      ["Bookmarks & Tags:", "Starred folders plus colour-coded Tags that gather tagged files from across your entire PC."],
      ["Drives & Network:", "Live usage bars with free space, USB Eject, Disk Space Analyzer, saved SMB/SFTP/WebDAV servers, and auto-detected Cloud drives."],
      ["Dropzone Shelf:", "Drag files onto the Dropzone while browsing, navigate elsewhere, then drag them out or Copy/Move here."]
    ]
  },
  {
    id: "statusbar",
    num: "6",
    x: 54,
    y: 95,
    title: "Actionable Status Bar",
    desc: "Always-visible telemetry and one-click triggers for global search, folder filtering, hidden files, and view switching.",
    bullets: [
      ["Bottom-Left Triggers:", "Search everywhere (Ctrl+K), Filter this folder (Ctrl+F), and Show hidden items (Ctrl+H)."],
      ["Live Telemetry:", "Displays exact folder/file counts, current selection count, and calculated total selection size."],
      ["Instant View & Sort:", "Quick-toggle between Grid, Details, Columns, Tree, and sort order without leaving the bottom edge."]
    ]
  }
];

export const FOUR_VIEWS = [
  {
    id: "grid",
    name: "Grid",
    shortcut: "Ctrl+Shift+1",
    img: "screenshots/photos.png",
    headline: "Large Icons & Crisp Media Thumbnails",
    desc: "Visual-first browsing with virtualized wrap layout (VirtualizingWrapPanel), vibrant icon packs, colour-tag dots, and real photo/video thumbnails even when a Linux or Material icon pack is active.",
    chips: ["Virtualized Wrap Layout", "Real Photo & Video Thumbnails", "Colour-Tag Dots", "Box-Select Drag"]
  },
  {
    id: "details",
    name: "Details",
    shortcut: "Ctrl+Shift+2",
    img: "screenshots/details.png",
    headline: "Information-Dense Sortable Columns",
    desc: "Precision tabular view displaying Name, Date Modified, Type, and Size columns. Click any column header to toggle ascending or descending sort while preserving your active selection.",
    chips: ["Name / Date / Type / Size", "One-Click Header Sorting", "Keyboard Type-to-Jump", "Selection Preserved"]
  },
  {
    id: "columns",
    name: "Columns",
    shortcut: "Ctrl+Shift+3",
    img: "screenshots/columns.png",
    headline: "Finder-Style Miller Columns",
    desc: "Navigate deep folder hierarchies effortlessly without losing context. Each selected folder opens a new column to its right, arrow keys glide across levels, and the rightmost column renders an inline file preview.",
    chips: ["Miller Column Hierarchy", "Inline Final-Column Preview", "Arrow-Key Navigation", "Zero Context Loss"]
  },
  {
    id: "tree",
    name: "Tree",
    shortcut: "Ctrl+Shift+4",
    img: "screenshots/tree.png",
    headline: "Expandable In-Place Hierarchy",
    desc: "Expand any folder in place with ▸ to inspect entire project branches simultaneously. Ideal for codebases, nested archives, and multi-folder comparisons without opening extra tabs.",
    chips: ["In-Place ▸ Expansion", "Full Branch Visibility", "Multi-Folder Selection", "Fast Keyboard Folding"]
  }
];

export const SAMPLE_SEARCH_INDEX = [
  { name: "Invoices 2026", where: "C:\\Users\\Danny\\Documents\\Finance", isDir: true, kind: "folder" },
  { name: "invoice_Q4_final.pdf", where: "C:\\Users\\Danny\\Documents\\Finance\\Invoices 2026", isDir: false, kind: "pdf" },
  { name: "OrbitSetup-1.0.0.exe", where: "C:\\Users\\Danny\\Projects\\orbit-file-manager\\dist", isDir: false, kind: "app" },
  { name: "MainWindow.Intro.cs", where: "C:\\Users\\Danny\\Projects\\orbit-file-manager", isDir: false, kind: "code" },
  { name: "FolderLock.cs", where: "C:\\Users\\Danny\\Projects\\orbit-file-manager", isDir: false, kind: "code" },
  { name: "backup-vault.orbitlock", where: "D:\\Backups\\Encrypted", isDir: false, kind: "lock" },
  { name: "release-assets.tar.gz", where: "C:\\Users\\Danny\\Downloads", isDir: false, kind: "archive" },
  { name: "wallpaper-tokyo-night.png", where: "C:\\Users\\Danny\\Pictures\\Wallpapers", isDir: false, kind: "image" },
  { name: "product-demo-4k.mp4", where: "D:\\Media\\Videos\\Orbit", isDir: false, kind: "video" },
  { name: "settings.json", where: "C:\\Users\\Danny\\AppData\\Roaming\\Orbit", isDir: false, kind: "code" },
  { name: "Themes.cs", where: "C:\\Users\\Danny\\Projects\\orbit-file-manager", isDir: false, kind: "code" },
  { name: "design-mockups.zip", where: "C:\\Users\\Danny\\Documents\\Design", isDir: false, kind: "archive" },
  { name: "arch-linux-2026.iso", where: "D:\\ISOs", isDir: false, kind: "archive" },
  { name: "ambient-synth-loop.flac", where: "C:\\Users\\Danny\\Music\\Production", isDir: false, kind: "audio" },
];

export const DISK_SPACE_TREE = {
  name: "Local Disk (C:)",
  sizeGB: 412,
  totalGB: 512,
  children: [
    {
      name: "Projects", sizeGB: 148, color: "#CBA6F7", isDir: true,
      children: [
        { name: "orbit-file-manager", sizeGB: 44, color: "#89B4FA", isDir: false },
        { name: "rust-crates-cache", sizeGB: 52, color: "#FAB387", isDir: false },
        { name: "unreal-assets", sizeGB: 36, color: "#A6E3A1", isDir: false },
        { name: "docker-volumes", sizeGB: 16, color: "#F38BA8", isDir: false },
      ]
    },
    {
      name: "Media & Captures", sizeGB: 124, color: "#89B4FA", isDir: true,
      children: [
        { name: "4K Screen Captures", sizeGB: 68, color: "#CBA6F7", isDir: false },
        { name: "RAW Photography", sizeGB: 39, color: "#F9E2AF", isDir: false },
        { name: "FLAC Library", sizeGB: 17, color: "#94E2D5", isDir: false },
      ]
    },
    {
      name: "Downloads", sizeGB: 64, color: "#FAB387", isDir: true,
      children: [
        { name: "ISOs & Disk Images", sizeGB: 34, color: "#F38BA8", isDir: false },
        { name: "Archives (.zip/.7z)", sizeGB: 19, color: "#A6E3A1", isDir: false },
        { name: "Installers", sizeGB: 11, color: "#B4BEFE", isDir: false },
      ]
    },
    { name: "Games & Steam", sizeGB: 54, color: "#A6E3A1", isDir: false },
    { name: "AppData", sizeGB: 22, color: "#F38BA8", isDir: false },
  ]
};

export const ICON_PACKS = [
  { name: "Material (Default)", desc: "1,100+ colourful file & folder icons from Material Icon Theme" },
  { name: "Windows Native", desc: "Your system's own shell icons & native thumbnails" },
  { name: "Papirus", desc: "Crisp flat Linux icon theme (GPL-3.0)" },
  { name: "Adwaita", desc: "Classic GNOME aesthetic (LGPL-3.0 / CC BY-SA)" },
  { name: "Colloid", desc: "Modern sleek rounded Linux icon suite" },
  { name: "Candy", desc: "Vibrant neon gradient icons" },
  { name: "Phosphor", desc: "Duotone line icons dynamically tinted to your active theme" },
  { name: "Devicon + Custom", desc: "Language/tool logos + load any Linux Freedesktop index.theme folder" },
];

export const SETTINGS_PAGES = [
  { id: "appearance", title: "Appearance", sub: "15 themes, pywal/Wallpaper & Mica glass", img: "screenshots/settings-appearance.png", desc: "Pick from 9 community favourites, 6 editor classics, or live System / Wallpaper sync (pywal, winwal, Windows accent). Toggle Windows 11 Mica translucency and dial transparency from 5% to 90%." },
  { id: "icons", title: "Icons", sub: "8 built-in packs + Freedesktop themes", img: "screenshots/settings-icons.png", desc: "Switch seamlessly between Windows, Material (1,100+ icons), Papirus, Adwaita, Colloid, Candy, Phosphor duotone, Devicon, or point Orbit at any Linux icon theme folder with index.theme." },
  { id: "general", title: "General", sub: "Startup tabs, sidebar sections & defaults", img: "screenshots/settings-general.png", desc: "Control hidden items, reopen tabs at startup, toggle individual sidebar sections (Bookmarks, Tags, Drives, Network, Recent, Dropzone), and pick your default view and sort order." },
  { id: "network", title: "Network", sub: "Servers, cloud auto-detect & SSH trust", img: "screenshots/settings-network.png", desc: "Connect to SMB, SFTP, WebDAV, Google Drive, or Dropbox, re-detect local cloud sync folders (OneDrive, iCloud, Drive, Dropbox), and manage Trusted SSH host keys." },
  { id: "shortcuts", title: "Shortcuts", sub: "Rebind every customizable hotkey", img: "screenshots/settings-shortcuts.png", desc: "Click any shortcut and press your preferred key combo (Esc cancels, Backspace clears, Reset all restores defaults). Fixed OS-standard shortcuts are clearly listed alongside." },
  { id: "about", title: "About", sub: "Update checker, logs & credits", img: "screenshots/settings-about.png", desc: "Check GitHub releases for updates with one click, open your settings folder (%AppData%\\Orbit), report bugs, and view open-source credits." },
];

export const KEYBOARD_SHORTCUTS = [
  // General
  { cat: "General", action: "Find anything (every drive)", keys: "Ctrl+K" },
  { cat: "General", action: "Open Settings", keys: "Ctrl+," },
  { cat: "General", action: "Quick Preview", keys: "Space" },
  { cat: "General", action: "Properties", keys: "Alt+Enter" },
  // Tabs
  { cat: "Tabs", action: "New tab", keys: "Ctrl+T" },
  { cat: "Tabs", action: "Close tab", keys: "Ctrl+W" },
  { cat: "Tabs", action: "Next tab", keys: "Ctrl+Tab" },
  { cat: "Tabs", action: "Previous tab", keys: "Ctrl+Shift+Tab" },
  // Navigation
  { cat: "Navigation", action: "Back / Forward", keys: "Alt+← / Alt+→" },
  { cat: "Navigation", action: "Up one folder", keys: "Alt+↑" },
  { cat: "Navigation", action: "Type or paste path", keys: "Ctrl+L" },
  { cat: "Navigation", action: "Refresh folder", keys: "F5" },
  { cat: "Navigation", action: "Bookmark folder", keys: "Ctrl+D" },
  // View
  { cat: "View", action: "Filter this folder", keys: "Ctrl+F" },
  { cat: "View", action: "Show / hide hidden items", keys: "Ctrl+H" },
  { cat: "View", action: "Grid / Details / Columns / Tree", keys: "Ctrl+Shift+1…4" },
  { cat: "View", action: "Toggle Split View", keys: "Ctrl+\\" },
  // Files
  { cat: "Files", action: "New folder", keys: "Ctrl+Shift+N" },
  { cat: "Files", action: "Rename selected", keys: "F2" },
  { cat: "Files", action: "Copy / Cut / Paste", keys: "Ctrl+C / X / V" },
  { cat: "Files", action: "Copy full path", keys: "Ctrl+Shift+C" },
  { cat: "Files", action: "Delete to Recycle Bin", keys: "Delete" },
  { cat: "Files", action: "Delete permanently", keys: "Shift+Delete" },
  // Fixed
  { cat: "Fixed", action: "Open selected item", keys: "Enter" },
  { cat: "Fixed", action: "Clear selection or filter", keys: "Esc" },
  { cat: "Fixed", action: "Context menu", keys: "Shift+F10" },
  { cat: "Fixed", action: "Open folder in new tab", keys: "Middle-click" },
  { cat: "Fixed", action: "Box-select items", keys: "Drag empty space" },
  { cat: "Fixed", action: "Copy / Move while dragging", keys: "Hold Ctrl / Shift" },
];

export const CLI_COMMANDS_WINDOWS = [
  { cmd: "Orbit.exe D:\\Projects\\App", desc: "Open folder, select file, or browse archive like a folder" },
  { cmd: "Orbit.exe --preview photo.heic", desc: "Open the file's folder and launch Quick Preview" },
  { cmd: "Orbit.exe --space C:\\Users\\Danny", desc: "Open the interactive Disk Space Map for a folder" },
  { cmd: "Orbit.exe --convert clip.mov", desc: "Open Convert directly for an image, video, or audio file" },
  { cmd: "Orbit.exe --lock D:\\Private", desc: "Encrypt a folder into a password-locked .orbitlock file" },
  { cmd: "Orbit.exe --unlock Private.orbitlock", desc: "Unlock and restore an encrypted .orbitlock archive" },
  { cmd: "Orbit.exe recycle:", desc: "Jump straight into Orbit's integrated Recycle Bin" },
  { cmd: "OrbitSetup-1.0.0.exe /quiet /desktop", desc: "Silent per-user install (no admin UAC prompt)" },
];

export const CLI_COMMANDS_LINUX = [
  { cmd: "orbit ~/Projects/orbit", desc: "Open folder, select file, or browse archive like a folder" },
  { cmd: "orbit smb://server/share", desc: "Mount and open an SMB / Samba or sftp:// network share" },
  { cmd: "orbit trash:///", desc: "Open the freedesktop.org Trash directly in Orbit" },
  { cmd: "orbit --preview photo.avif", desc: "Open the file's folder and launch Quick Preview (mpv / pdftoppm)" },
  { cmd: "orbit --space ~/Downloads", desc: "Open the interactive Disk Space Map for a folder" },
  { cmd: "orbit --convert video.mkv", desc: "Open Convert directly (ffmpeg / ImageMagick)" },
  { cmd: "orbit --lock ~/Private", desc: "Encrypt to .orbitlock (100% compatible with Windows)" },
  { cmd: "./install.sh --default", desc: "Install to ~/.local/opt/orbit (no root) & set as default file manager" },
];

export const LINUX_PARITY_ROWS = [
  ["Trash / Recycle Bin", "Windows Recycle Bin", "freedesktop.org Trash (~/.local/share/Trash & .Trash-<uid>), shared with Dolphin & Files"],
  ["Drives & Mounting", "Drive letters + Windows Eject", "Mounted drives (/, /home, /run/media) with labels, free space & udisks2 Eject / ISO mount"],
  ["Network Shares", "Windows SMB & WebClient", "GVfs (gio mount) for SMB/WebDAV + built-in SSH.NET SFTP & rclone + fuse3 cloud mounts"],
  ["Translucent Window", "Windows 11 Mica / Acrylic", "Compositor blur request (KDE Plasma, Hyprland, picom…) with 5%–90% transparency"],
  ["System / Wallpaper Theme", "pywal or Windows accent", "pywal, KDE Plasma, or GNOME live accent colour & light/dark mode"],
  ["Media & PDF Preview", "Windows codecs & PDF engine", "Embedded mpv for video/audio, pdftoppm for PDFs, ffmpeg + ImageMagick conversion"],
  ["Icon Theme", "Windows Shell + 7 packs", "System desktop theme (Breeze, Adwaita, Papirus…) via MIME database + 7 packs"],
  ["Settings & .orbitlock", "%AppData%\\Orbit\\settings.json", "~/.config/Orbit/settings.json (identical format) & cross-platform .orbitlock vaults"],
];
