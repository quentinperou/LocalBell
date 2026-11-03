# CLAUDE.md

Guidance for Claude Code and contributors working on this repository. `README.md` (English) and `README.fr.md`
(French) will describe the project for users.

## The project

**LocalBell**: a small open-source (MIT) Windows app, a **doorbell between the PCs of a local network**, on the
principle of LocalSend: install it, name the PC, every other PC running it sees it and can **ring it, with or without
a message**; the PC rung shows a **popup in a corner** and rings, and can reply in one click ("J'arrive"…). For
colleagues of a shop or an office calling each other. **Windows 10 (2004+) and Windows 11.** No server anywhere; the
internet only for the update check of the GitHub version (none in the Microsoft Store version). UI in English and French. Author: [QuentinPerou](https://github.com/quentinperou) (a photo shop); credits
wording: "Developed by QuentinPerou, with Claude Code (Anthropic)". Named **DeskBell** at first (the folder
"Sonnette", then "DeskBell"); renamed **LocalBell** by the author on October 31, 2026, before any release ("ça me
paraît plus cohérent"): code, protocol tag and API path, settings folder, installer and docs alike.

**Talk to the author in French.** Code, comments, commit messages and these docs are in English.

## Project status (October 31, 2026)

Version **0.1.1**, not released. **Git repository since October 31, 2026** (branch `main`, first commit `32644a4`
"LocalBell 0.1.0: …", made by Claude at the author's request, as QuentinPerou with his GitHub noreply address in the
repository's own config). Not on GitHub yet: he publishes `quentinperou/LocalBell` from home with his account.

- Done: the network (`LocalBell.Core`, tested: 113 tests with the update feed), the icon, the app: the list window, the popup (discreet
  or loud), the ring sounds, the bell near the clock with its menu, "do not disturb" (moon button, banner, menu),
  favourites (star), single instance, **Settings on six pages** (as approved: name of the PC, start with Windows,
  do not disturb, language, theme; sound + own WAV/MP3 file, volume, ring when Windows is muted, repeat; popup style
  with previews, corner, screen, closing; ready-made messages and replies; this PC's address, PCs added by hand, PCs
  found; about; updates since October 31, a seventh page), the installer (with its firewall rule), the build script,
  the GitHub workflow.
- Tried on the author's PC: two LocalBell (`--profile test-a` / `test-b`) find each other, ring and reply ("ça
  marche !", October 31); Settings opened by UI Automation, each page pictured (`.local\maquettes\essai-parametres-*`).
- **The taskbar widget** (October 31): the bell or the favourite, the badge of missed rings, the moon, its flyout
  (missed rings and "Rappeler", the PCs, the message, everyone) and its right-click menu; the menus of the bell near
  the clock and of the widget drawn as Windows' (NowPlaying's `Menus.xaml`, with a sub-menu). Placed on the author's
  PC after the weather; his own try pending.
- Then, at his requests (October 31): the widget moves aside for the widgets of other apps (it lay under NowPlaying's)
  and Settings → General → "Widget de la barre des tâches" (left / near the clock / hidden); Settings laid out as
  NowPlaying's (the app and this PC on the left above the menu, the page title up to the top of the right column);
  **the README** (`README.md`, `README.fr.md`, as PageCompose's but shorter) with its picture `.github/hero.png`
  (`.local\readme\hero.cs`, see the README picture below).
- **Developer mode** (Settings → About, as ScreenClip's and TextCase's, his request): `Services/DevLog` writes
  `localbell.log` next to the settings (200 KB, then its last half): the node's events through `NodeOptions.Log`
  (cards, computers online / gone, announcements, scans, rings and replies with their ids, failures), the place of
  the widget when it changes, the popups, the sound turned back on, the errors. **Never the text of the messages.**
- README picture: proposals B (light, windows overlapping, as ScreenClip's), C (a real desktop, windows side by side)
  and **D, chosen** (B with the popup above the taskbar on the right, the windows raised and apart): `hero.cs -- D`, copied to
  `.github\hero.png`; the three kept in `.local\readme\`.
- **Renamed LocalBell** (October 31, his "Oui, renomme tout"; it was DeskBell), the project folder too: `F:\LocalBell`.
- **Updates from GitHub and a Microsoft Store version** (October 31, his request "comme pour mes autres projets"):
  PageCompose's `UpdateFeed` / `SemanticVersion` (Core, tested) and `Updater` (check at startup, Stable/Beta channel,
  SHA-256, silent install, relaunch near the clock), a bar over the list (drawn as the "do not disturb" banner) and a
  seventh Settings page, Updates; NowPlaying's Store version (`store/`, `scripts/build-store.ps1`,
  `Services/AppPackage`): the same exe in an MSIX package, built once here (72 MB, makeappx accepted the manifest).
  Not tried yet: the packaged app itself (registering it installs it on the PC: ask him), an update installed from a
  real release (none before he publishes), the bar on screen. Before the Store: he reserves the name in Partner
  Center (NowPlaying's account) and the `Identity` of the manifest is checked against it.
- **The widget in a process of its own, owned by the taskbar** (October 31, his "Oui, vas-y"), as NowPlaying's: it
  stays over the taskbar with Start open and whatever raises the taskbar; a hung widget process is killed and started
  again (tried). He checked it stays with Start open ("Oui, il reste").
- **Audit** (October 31, late; `.local\audit-2026-10-31.md`, git-ignored): fixed before the first install: UDP port
  taken → the app goes on without hearing announcements (a note on the Network page); unreadable settings keep their
  id (`settings.json.bad`, tolerant enums, written to the disk before the swap); no new widget process while Windows
  closes the app (an update); the PC's sound put back on Quit; the user's own sound file rung whole; installer x64
  only; `build.ps1` keeps `dist\store`. Network hardening: `/peers` followed for LAN addresses only (64 at most, 8 at
  a time), no HTTP redirects, answers ≤ 64 KB, 32 connections at a time on the server, directory capped at 250 and
  only verified PCs kept, rings throttled per address (5, then one every 2 s), replies taken only for rings sent to
  that PC, invisible characters dropped from names, ids `[0-9A-Za-z-]`, IPv4 only, loops that log instead of dying,
  the hand-added addresses asked in parallel. Then from its list C (October 31, end of the day, his "Oui, vas-y"):
  "do not disturb" turned on while it rings silences the bell (the callers told nobody answered); one popup per PC
  that rings (its new ring takes the place of the one before); two PCs with one id (settings copied with a disk
  image) tell each other apart, the one whose name comes last takes a new id (`BellNode.IsThisPc`, `IdChanged`);
  the quick panel and the widget's menu open below a taskbar on top; a failure at startup ends the app with a message
  instead of leaving it running with no window. The rest of C is left for later.
- **Installed on two PCs** (0.1.0, October 31: the shop PC and BUREAU3): they see each other, ring and reply.
- **0.1.1** (October 31, after his first install): the ring repeated twice by default (was 3), a click on the bell near
  the clock opens the widget's quick panel (or the window, a setting), the music box (a fourth bell, for Halloween).
- Next: 0.1.1 on BUREAU3 (installed on the shop PC); the list C of the audit.
  Ideas for later: a remembered window place; **keyboard shortcuts to ring each PC** (his idea, October 31: one per
  PC, set in Settings; global shortcuts, so mind the clashes with other apps).

## Design decisions (approved by the author, October 31, 2026)

The pictures are in `.local\maquettes\` (git-ignored, made by `.local\mockups.cs` and `.local\icon-mockups.cs`).

- **Main window: a list** (one PC per row: a dot, its name, its status, a star, "Sonner"), then the optional message,
  the ready-made messages (pills), "Sonner tout le monde". Tiles were the other option, kept in mind.
- **Popup: discreet or loud, chosen in Settings, loud by default** (discreet: the size of a Windows notification;
  loud: an orange band, the name large). It never takes the keyboard (not activated), stays above the windows, closes on a reply or its
  cross (or after a time set in Settings). The bell rings again while it waits (twice by default; 3 until his try of the installed app, October 31).
- **What happens to a ring shows on the row of the PC rung**, never over the list (his choice C among three): its
  card is outlined, "Ça sonne… · message" (accent), then its reply in green ("J'arrive · 13:53"), or in grey "Vu,
  fermé sans réponse" / "Pas de réponse" (the PC rung tells it: "Ça sonne…" must never stay when it no longer rings),
  "Ne sera pas dérangé" (yellow), "Ne répond pas" (red). A notice over the bottom only for what has no row
  ("Aucun poste en ligne"); its shadow is a layer of its own (an effect on the text blurs it).
- **"Sonner tout le monde" in two clicks** (his idea, choice A): the first turns it yellow for 3 s, "Cliquez encore :
  sonner N postes", a bar running out; the second rings.
- **Taskbar widget: the bell alone without a favourite** (a click opens the list; a red badge counts the missed
  rings), **the bell and the favourite's name with one** (a click rings it, the arrow opens the list). The favourite
  is the starred row.
- **Settings on six pages with a menu on the left** (a seventh, Updates, since October 31, as in his other apps), laid out as NowPlaying's (the icon, "LocalBell" and this PC above
  the menu, the page title at the top of the right column): General (the name of the PC, start with Windows, the
  place of the widget, do not disturb, language, theme), Ring (sound: three bells and a music box for Halloween, the day
  the app was made, the others proposed kept in `.local\sons\`; own WAV/MP3, volume of its own, ring when Windows is muted,
  repeat), Popup (style, corner, screen, closing), Messages (ready-made messages, the three replies), Network (this
  PC's address, PCs added by hand, PCs found, refresh), About.
- **A click on the bell near the clock opens the widget's quick panel**, above the bell (the author, October 31, after
  his first install), or the window: Settings → General → "Clic sur l'icône", as ScreenClip's. The right click
  always opens the menu.
- **A new version shows without opening Settings** (his wish, October 31): the banner over the list, a banner at the
  top of the quick panel (his choice A among two, `.local\maquettes\maj-*`) and "Mettre à jour vers x.y.z" first in the
  right-click menu, in the accent colour; the panel and the menu install from the window, whose banner shows the progress.
- **The widget fits a small taskbar** (Windows 11 26H2 lets it be 32 px; the widget stuck out of it, October 31):
  smaller bell, button and badge, 4 px from its edges. A vertical taskbar (left or right) hides it.
- **Forget a PC that is no more** (his idea, November 3): a right click on its row → "Oublier ce poste", for an
  offline PC only (an online one would come back at once): out of the list, of the PCs kept for the next start, no
  longer the favourite; back if it is ever heard from again.
- **"Do not disturb" at hand**: right click on the widget or the bell near the clock (30 min, 1 h, until tomorrow,
  until turned back on), the moon beside Settings in the list (accent while on, with a banner), a moon in place of
  the bell on the widget. While on: no sound, no popup, rings kept as missed. Others see a yellow dot and get a toast
  "X ne sera pas dérangé" when they ring.
- **Icon**: a white counter bell (push button, dome, base) on a rounded square with an amber-to-orange gradient
  (`#FFC24B` → `#F26B2A`); 16, 20, 24, 32 px drawn on whole pixels (`scripts\make-icons.cs`).
- **Must work on any network**, not only the author's (he does not know the shop's IP ranges): no address or range
  assumed anywhere.

## Architecture

- `src/LocalBell.Core` (net10.0, no UI, **all the network, unit-tested**):
  - `Protocol/BellProtocol`: constants (UDP and first TCP port **53318**, multicast group **224.0.0.168**,
    link-local as LocalSend's 224.0.0.167), the JSON (camelCase, enums as strings), `Clean` (names and messages
    received: no control characters, cut to their length). `Protocol/Messages`: `PeerInfo` (id made once per
    install, name, port, status, version), `Announcement`, `Hello`, `PeerAddress`, `Ring`, `RingAnswer`, `Reply`.
  - `Network/LocalNetwork`: the IPv4 addresses of **every** card that is up (not loopback, not tunnels, not 169.254),
    `Neighbours` (the hosts a scan asks: at most the /24 of each address).
  - `Network/Discovery`: UDP, the port shared (ReuseAddress: a second LocalBell on the same PC), the group joined on
    every card; an announcement goes out of every card, to the group **and** to the card's broadcast address.
  - `Network/MiniHttpServer`: a tiny HTTP/1.1 server on a TcpListener (one JSON request per connection, 64 KB at
    most, 5 s): HttpListener needs a URL reservation as administrator, ASP.NET Core a runtime the Desktop one lacks.
    The preferred port, else the next nine, else any.
  - `Peers/PeerDirectory`: the PCs by id, online while heard from, offline after 100 s of silence or when they leave;
    up to three addresses each (a PC on two networks), the newest first; kept offline in the list.
  - `BellNode`: one PC. Endpoints `POST /api/localbell/v1/hello` (introduce oneself, get the server's `PeerInfo`),
    `GET /peers` (the PCs it knows online), `POST /ring` (answer: quiet when in "do not disturb"), `POST /reply`
    (`ReplyKind`: a reply text, the popup closed, or unanswered when the bell stopped or the popup closed itself).
    Finding the others, from the simplest to the most stubborn: (1) an announcement on every card, answered by a
    hello to its server (UDP answer if HTTP fails); every 30 s a "still here" announcement; (2) when nobody answered
    after 3 s, a hello to every address of each card's /24 (a scan; `Scan = false` / `--no-scan` turns it off);
    (3) the addresses added by hand, asked every 30 s; (4) each PC newly found is asked the PCs it knows (one link
    between two networks joins them all). PCs not heard for 45 s get a hello; leaving, a PC tells the others (UDP
    and HTTP). Rings carry an id: one received twice (two addresses) shows once. Started on the thread pool; events
    come on pool threads.
  - `Updates/UpdateFeed` + `SemanticVersion` (PageCompose's): the GitHub "list releases" API of
    `quentinperou/LocalBell`, the asset `LocalBell-Setup-<version>.exe` and its SHA-256 (`digest`), the newest
    version of the channel (never a downgrade), the download.
- `src/LocalBell.App` (WPF, net10.0-windows10.0.19041.0 for the Windows projection, Fluent theme, Mica on Windows 11):
  - `App`: arguments `--hidden`, `--profile NAME` (settings in `%LOCALAPPDATA%\LocalBell\profiles\NAME`, its own id:
    two LocalBell on one PC), `--no-scan`; one instance per profile (a second launch shows the window); language and
    theme (`LOCALBELL_LANG`, `LOCALBELL_THEME` force them); first start: the ready-made messages and replies in the UI
    language. On exit, the node tells the others (off the UI thread). `ShutdownMode` explicit: the cross of the window
    hides it, Quit is in the menu of the bell near the clock.
  - `MainWindow` + `ViewModels/MainViewModel` (rows in place, `PeerItem`, chips, toast, "do not disturb",
    favourites, known PCs saved, rename), `RingCard` (the card of a ring, discreet or loud: in `RingPopup` and as
    the previews of Settings → Popup), `Services/RingPopups` (pile in the corner of the main screen or of the mouse's,
    bell repeated, closing after a time), `Services/BellSound` (sounds synthesized by the app: no file, no rights;
    volume applied to the samples; the user's own WAV/MP3 through the WPF MediaPlayer), `Services/WindowsVolume`
    (Core Audio: the PC's sound turned back on, and to 25 % at least, while it rings; put back after, unless the user
    changed it), `Services/WindowsStartup` (the user's Run key, `--hidden`; only the installed app, in Program Files,
    follows the setting at each start), `Services/TrayIcon` (Windows Forms NotifyIcon, a WPF Fluent menu opened from
    a small active window), `Services/AppSettings` (`settings.json`).
  - `Services/Updater` (PageCompose's): check at startup unless turned off, download to `%TEMP%\LocalBell-Update`,
    the installer run with `/SP- /SILENT /NORESTART /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS /RELAUNCH=1` (the
    Restart Manager closes LocalBell: WPF quits on its `WM_QUERYENDSESSION`, the node tells the others; the
    installer starts it again with `--hidden`); `UpdateBar` over the list, Settings → Updates.
  - `Services/AppPackage` (NowPlaying's): the Store version is the same exe run from its MSIX package
    (`GetCurrentPackageFullName`). In it: the startup task of the manifest instead of the Run key (a package's HKCU
    writes go to its own copy), started by it = `--hidden` (`ActivationKind.StartupTask`); no GitHub update (the
    Updates page opens the Store); the firewall rules of the manifest (a package extension, makeappx refuses them under
    the application) instead of the installer's netsh; the GitHub version, if installed, offered once for removal
    (one LocalBell at a time: the same mutex). `store/`: `AppxManifest.xml` (identity to check against Partner
    Center), `Assets/` (by `make-icons.cs`), `listing.md` (the texts for Partner Center).
  - `TaskbarWidget` (in a process of its own) + `WidgetFlyout` (in the app): NowPlaying's taskbar widget technique
    (`src/NowPlaying/TaskbarWidget.xaml.cs` and `Services/Taskbar*.cs` of github.com/quentinperou/NowPlaying), since
    October 31 (the author: it vanished while Start was open, and came and went with NowPlaying's widget raising
    the taskbar every 15 s). The window is **owned by the taskbar** (`GWLP_HWNDPARENT`): an owned window always stays
    above its owner, Start or search open too. That tie shares the two threads' input, so it runs in
    `LocalBell.exe --taskbar PIPE` (`Services/TaskbarClient`: no network, no settings), started by the app
    (`Services/TaskbarHost`): one line of JSON per change over a named pipe (`Services/TaskbarLink`: `TaskbarState`,
    `LineChannel`), its clicks back ("flyout", "ring", "menu", with `AllowSetForegroundWindow` so that the app may
    activate its flyout and its menu), its placement for the developer log ("log"); a ping a second, a widget silent
    2 s is killed (the taskbar free again) and started again (1 s, then 2, 4… up to 60 s while it keeps failing). Tried
    by suspending it: killed after 2.3 s, a new one 2 s later. It quits when the pipe closes (the app gone) or its
    taskbar does (Explorer restarted). Made at its first showing, out of sight, with its content (made beforehand,
    empty, Windows held it to 136 px). `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` (`WM_MOUSEACTIVATE` → `MA_NOACTIVATE`),
    topmost, put back on top at each foreground change (`SetWinEventHook`), an app bar without room for
    `ABN_FULLSCREENAPP`, transparent through DWM (`ThemeMode="None"`), the arrow pointer (no hand, as the taskbar's
    buttons); placed after the Widgets (weather) button found by UI Automation, else (Windows 10, icons aligned left,
    or Settings → near the clock) left of `TrayNotifyWnd` and of the weather (with the icons on the left Windows puts
    it there); the buttons looked for again when the taskbar changes (its rectangle, its notification area,
    `TaskbarAl`), 2 s later (they slide), and every 10 s; a read without the Start button is dropped (Start or search
    open: the taskbar lifted into band 6, its buttons read as off screen, the widget went over the weather); past
    the windows of other apps laid over the taskbar (`OtherWidgets`: visible, not cloaked, within the bar's height,
    narrower than half of it, not Explorer's nor LocalBell's; looked for every 2 s, and those seen asked again by
    their handle: with Start open EnumWindows does not list the taskbar's owned windows, and the widget went under
    NowPlaying's); colours from Windows' mode (`WindowsTheme`). Watched from the shell on October 31 (z-order,
    `WindowFromPoint`, `GetWindowBand`, every 50 ms): 3 minutes of Start, rings and flyout, the widget always in place. The flyout is the app's, active
    while shown, hidden when it loses the focus (the missed rings are then seen). `--no-widget` for the second
    LocalBell of a try.
  - `Menus.xaml` + `Services/PopupMenu`: the menus of the bell near the clock and of the widget, drawn as Windows 11's
    in Windows' mode (NowPlaying's, with sub-menus), opened from a tiny window activated first, above the taskbar for
    the widget.
  - `SettingsWindow`: seven pages from a menu on the left (PageCompose's structure; Updates since October 31), each
    change applied and saved at once; a language change builds the windows again (texts are read when a window is
    built).
  - `Properties/Strings.resx` + `Strings.fr.resx`, `{local:Tr Key}` in XAML, `Strings.Get/Format/Count` in code.
- `tests/LocalBell.Core.Tests` (xUnit, 113 tests): the protocol, the addresses, the directory, the server, `BellNode`
  end to end (several nodes on 127.0.0.1, without UDP: nothing leaves the PC), the update feed, and the parity of the
  two languages.

## Precautions

- **Never ring another PC of the shop** in a test: two LocalBell on the author's PC (`.local\test-two.ps1`, profiles
  `test-a` and `test-b`, `--no-scan`). A ring shows a popup and a sound on the author's screen: ask first.
- Tests drive the app with UI Automation only, **never keys**; pictures of the app windows by PrintWindow only
  (`.local\capture.ps1`), never a screen copy (customer data on screen at the shop).
- Starting the app (not a test runner) makes Windows ask to let it on the network: the author's choice.
- Claude's shell is sandboxed: it does not see `%LOCALAPPDATA%` as the app does (settings written from it are not
  read by the app).
- Start a build for the author through Explorer (`.local\test-two.ps1` does, through shortcuts): started from
  Claude's shell, an app went down with the Claude app when it updated itself (PageCompose, October 19, 2026).

## Commands

```powershell
dotnet build LocalBell.slnx
dotnet test tests\LocalBell.Core.Tests
.\scripts\build.ps1                      # tests, dist\app, dist\LocalBell-Setup-<version>.exe (Inno Setup 6)
.\scripts\build-store.ps1                # tests, dist\store\LocalBell_<version>.0_x64.msix (Windows SDK)
dotnet run scripts\make-icons.cs         # the icon files and the Store logos (committed), from the repository root
```

The Store version in development: `Add-AppxPackage -Register dist\store\layout\AppxManifest.xml` (Developer Mode,
on at the shop PC; it installs LocalBell for the user, with its startup task and firewall rules: ask the author
first), `Get-AppxPackage QuentinPerou.LocalBell | Remove-AppxPackage` after.

## Working on several PCs

As PageCompose: the folder lives on a USB drive (`F:\LocalBell` at the shop), Claude's memory in
`.local/claude/memory/` (git-ignored), loaded by the root `CLAUDE.local.md`; the auto memory of Claude Code is off
(`.claude/settings.local.json`). Keep the memory, `CLAUDE.local.md` and "Project status" above up to date. Git on
the drive: `git -c safe.directory=<path> …` on the shop PC.

## Releasing

As PageCompose: bump `Directory.Build.props`, commit "LocalBell X.Y.Z" (the author's commits, **no Co-Authored-By
line**), lightweight tag `vX.Y.Z`, `git push origin main vX.Y.Z` (the tag alone leaves `main` behind on GitHub); the
workflow builds and publishes the release with the installer (a tag with a suffix is a pre-release, for the Beta
channel). Never push nor sign in to GitHub from the shop PC: he publishes from
home with his account. The Store version goes out **by hand**, as NowPlaying's: `scripts\build-store.ps1`, then a new
submission in Partner Center (the `.msix`, the texts of `store/listing.md`); final versions only (x.y.z.0).
