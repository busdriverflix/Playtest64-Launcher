# Playtest64 Launcher

The launcher for the Game A playtest. A tester downloads it once from the website, signs in with their playtest
account, picks a build in a list, and the launcher downloads, unpacks and starts it. In the background it looks now
and then for a new build and says so in a message in the corner of the screen.

C# with Avalonia (like the asset compiler), published as **one self-contained file per system**: the .NET runtime and
Avalonia are inside, so there is nothing to install first. The first version was plain C without libraries; that
prototype is kept in `c-prototype/` for reference (it could not speak HTTPS on Linux without a TLS library, and a window
on Linux would have been a project of its own).

## State: a first scaffold

Done and tested on Linux (`dotnet test`, 33 tests against a stand-in server): sign in, list the builds that have a file
for this system, download with progress, unpack safely (a path that climbs out of the folder is refused, a file that is
not a zip is refused), mark a build complete only when it is, start it in its own folder, and the background check
(announces a new build once, notices a token that ran out, copes with no network).

**Not run by me**, so the first look is yours: the window itself (`launcher/Views/MainWindow.axaml`), the message in the
corner (`ToastWindow`), and everything on Windows. It builds for both.

The window shows the website's news (text with paragraphs and bullet lists as on the site, screenshots, and a button that
picks the build a post announces) next to the build list, and wears the website's seasonal look (Halloween 1 Oct - 7 Nov,
Weihnachten 1-26 Dec, Silvester 27 Dec - 3 Jan, Ostern two weeks before Easter Sunday to Easter Monday): `Core/Themes.cs`
has the colours and the dates, which have to be kept in step with the website's `themes.css` and `useTheme.ts` by hand. A
checkbox switches the season's look off; `PLAYTEST_THEME=weihnachten` (and so on) forces one for a look. The flourishes of the
website (bats, snow, the Creepster font) are not in.

### Still to do

- **Linux libraries.** .NET needs the system's OpenSSL for HTTPS, and Avalonia needs the usual X11 libraries; nearly every
  desktop has them, not every one. Installing something for the tester needs administrator rights and a package manager
  per distribution, so the launcher does not do that.
- An installer or a shortcut, signing the Windows build (without it SmartScreen warns).
- **The launcher's own updates are in**: it asks `GET /api/launcher/latest?platform=` (the version in `Launcher.csproj` against the
  one the admin typed in when uploading), shows an "Launcher-Update auf X" button in the bar and tells of it once in the
  background toast. A click downloads the file next to the running one, checks its SHA-256, renames the running file to
  `.old`, moves the new one in, starts it and ends; the next start deletes the `.old`. **Bump `<Version>` in
  `Launcher.csproj` before every `publish.sh` that is uploaded**, or the new launcher thinks it is the old one.
- **The system check** (`Core/SystemCheck.cs`): on Linux it looks for `libglfw.so.3` and `libvulkan.so.1`, on both systems it makes a Vulkan
  instance and asks for the graphics cards (type, the swapchain extension). A missing library, no card, or a software card
  (llvmpipe) shows a panel above "Spielen"; on apt, dnf, pacman and zypper systems a button installs the packages through
  `pkexec` (the desktop's password dialog), with the command shown for copying. NVIDIA's own driver is never touched.
  `Playtest64Launcher --system` prints the same to the console.
- **The graphics API box** ("Grafik-API", `Core/Graphics.cs`): automatic, Vulkan or OpenGL, kept in `settings.json` and handed to the game as the
  environment variable `P64_GRAPHICS` (nothing for automatic). A variable and not an argument, because a game from before there was a
  choice ignores a variable and would take an argument for its pack. The game decides by itself with automatic: Vulkan, and OpenGL when
  Vulkan does not start.
- **The check at login is a single look**, not a service: `--background` waits 20 s, asks the server once (a few more tries after 1, 3 and
  10 minutes if there was no network yet), shows a toast if there is news and ends. A click on the toast opens the launcher and the
  process stays as the launcher. Only while the launcher window is open does it check again every 6 hours.
- A "feedback" page in the window, later.

## Building

    dotnet build launcher/Launcher.csproj          # to try
    dotnet test tests/Launcher.Tests               # the logic against a stand-in server
    ./publish.sh                                   # win-x64 and linux-x64, one file each, in build/publish/

Against false alarms from antivirus programs (the launcher is not code-signed): the file is **not compressed** (`EnableCompressionInSingleFile`
was taken out: a compressed file reads as "packed" to scanners; the Windows file is 41 MB instead of 23 MB), and it carries an icon and a
product name, company, description and copyright (`Launcher.csproj`, `Assets/icon.ico`). What stays and can still look suspicious to a
scanner: it replaces itself (self-update) and, if the tester ticks the box, starts at login (autostart).

## Running

    Playtest64Launcher                the window
    Playtest64Launcher --background   one check at login, no window and no tray icon, then it ends: what "check for updates when I sign in" starts
    Playtest64Launcher --check        one check written to the console, then it stops (tests, timers)

It keeps everything in one folder: `%LOCALAPPDATA%\Playtest64Launcher` or `~/.local/share/Playtest64Launcher`
(`PLAYTEST_LAUNCHER_HOME` moves it). `PLAYTEST_SERVER` changes the server, for testing against a local one.

## The server side

It uses the website's own routes with the signed-in token as a bearer: `POST /api/auth/login`, `GET /api/builds`,
`GET /api/builds/files/{id}/download`. One change was made to the website: the login takes `"launcher": true` and then
gives a token that lasts 180 days instead of 14. That change is in the website's working copy and **not deployed**.

A caveat that is the website's, not the launcher's: tokens are signed and not stored, and a request does not check that
the address is still on the playtest list. Somebody removed from the list keeps access until their token runs out, which
with 180 days is long. The fix is a lookup in the website's `RequireUser`.

## What it keeps

The password is typed once and sent to the server; it is never written down. Kept are the address, the token (the file is
readable by the user only on Linux) and the number of the newest build the person was told about.
