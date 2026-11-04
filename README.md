# Playtest64 Launcher

The launcher for the Game A playtest. A tester downloads it once from the website, signs in with their playtest
account, picks a build in a list, and the launcher downloads, unpacks and starts it. In the background it looks now
and then for a new build and says so in a message in the corner of the screen.

C# with Avalonia (like the asset compiler), published as **one self-contained file per system**: the .NET runtime and
Avalonia are inside, so there is nothing to install first. The first version was plain C without libraries; that
prototype is kept in `c-prototype/` for reference (it could not speak HTTPS on Linux without a TLS library, and a window
on Linux would have been a project of its own).

## State: a first scaffold

Done and tested on Linux (`dotnet test`, 13 tests against a stand-in server): sign in, list the builds that have a file
for this system, download with progress, unpack safely (a path that climbs out of the folder is refused, a file that is
not a zip is refused), mark a build complete only when it is, start it in its own folder, and the background check
(announces a new build once, notices a token that ran out, copes with no network).

**Not run by me**, so the first look is yours: the window itself (`launcher/Views/MainWindow.axaml`), the message in the
corner (`ToastWindow`), the tray icon of the background mode, and everything on Windows. It builds for both.

### Still to do

- **Linux libraries.** .NET needs the system's OpenSSL for HTTPS, and Avalonia needs the usual X11 libraries; nearly every
  desktop has them, not every one. Installing something for the tester needs administrator rights and a package manager
  per distribution, so the launcher does not do that.
- **The launcher's own updates**, an installer or a shortcut, signing the Windows build (without it SmartScreen warns), the
  website's download page for the launcher.
- **Tray icon on Linux** depends on the desktop (it needs a StatusNotifier host); without one the background mode still
  works but has no icon to quit it by.
- A "feedback" page in the window, later.

## Building

    dotnet build launcher/Launcher.csproj          # to try
    dotnet test tests/Launcher.Tests               # the logic against a stand-in server
    ./publish.sh                                   # win-x64 and linux-x64, one file each, in build/publish/

## Running

    Playtest64Launcher                the window
    Playtest64Launcher --background   the background check with a tray icon, no window: what "check for updates when I sign in" starts
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
