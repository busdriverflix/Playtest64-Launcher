# Playtest64 Launcher

A small launcher for the Game A playtest. A tester downloads it once from the website, signs in with their
playtest account, picks a build from a list, and the launcher downloads, unpacks and starts it. In the
background it looks now and then for a new build and says so on the desktop.

The launcher itself is plain C11 with **no libraries of its own**: libc on Linux, and on Windows only what
the system has (WinHTTP, user32, the shell). One file of about 60 KB (Linux) or 390 KB (Windows, static).

## State: a first scaffold

| | Windows | Linux |
|---|---|---|
| Sign in, list builds, download, unpack, start | written, **only compiled** (no Windows here) | done and tested |
| Window | Win32 window with a build list, install/play button, progress | text in the terminal (a window is still to be decided) |
| HTTPS | WinHTTP (the system's) | **not in yet**: only `http://` works, see below |
| Background check + notification | `--watch`, balloon in the tray | `--watch`, `notify-send` |
| Start with the system | `Run` key in the registry | `~/.config/autostart` entry |

### What is missing, in order of importance

1. **TLS on Linux.** The site is HTTPS only, and a C program without a library cannot speak it. The plan is to link a
   small TLS library (BearSSL) statically into the Linux build; `Conn` in `platform/http_posix.c` is the place it goes
   (`conn_open`, `conn_read`, `conn_write`, `conn_close` are the only code that touches the socket). Until then the
   Linux launcher refuses an `https://` address and says why.
2. **The libraries of the game on Linux.** The game's own Linux zip carries a starter (`tools/linux_launcher.c` of the
   engine repository) that names a missing library. Installing it for the tester needs administrator rights and a
   package manager per distribution, so the launcher does not do that yet.
3. **Windows has not been run.** It builds with MinGW (`make windows`) without warnings, but it has not been started.
   It is not DPI aware (Windows scales the window), and a notification does not open the launcher when clicked.
4. **No window on Linux.** There is no native window toolkit that every Linux has; the nearest thing is X11 loaded at run
   time. Open question.
5. **Updating the launcher itself**, a signed build, an installer, and the website's download page for it.

## Building

    make              # Linux: build/linux/launcher
    make windows      # Windows: build/windows/launcher.exe (MinGW cross build)
    make tests        # Linux: the unpacker, the server conversation, the background check

## Using it

    launcher                 the interface (a window on Windows, text on Linux)
    launcher --watch         the background check; never ends; for the system's autostart
    launcher --check         one background check, then it stops (for a timer)
    launcher --extract Z D   unpack a zip into a folder (for testing the unpacker)

Everything it keeps is in one folder: `%LOCALAPPDATA%\Playtest64Launcher` or `~/.local/share/playtest64-launcher`
(`PLAYTEST_LAUNCHER_HOME` moves it). `PLAYTEST_SERVER` changes the server, for testing against a local one.

## The server side

It uses the website's existing routes with the signed-in token as a bearer: `POST /api/auth/login`,
`GET /api/builds`, `GET /api/builds/files/{id}/download`. The one change to the website is that the login takes
`"launcher": true` and then gives a token that lasts 180 days instead of 14, so nobody signs in every two weeks.
That change is in the website's working copy and is **not deployed**.

A caveat that is the website's, not the launcher's: tokens are signed and not stored, and a request does not check
that the address is still on the playtest list. Somebody removed from the list keeps access until their token runs
out, which with 180 days is long. The fix is a lookup in `RequireUser`.

## What it does not store

The password is typed once and sent to the server; it is never written to disk. What is kept is the address, the
token and the number of the newest build the person has been told about.

## The tests

`tests/run_tests.py` builds zips with Python (stored, deflated, a file bigger than the deflate window, an executable
one), unpacks them with the launcher and compares; refuses a damaged zip and a path that climbs out of the folder;
unpacks the real game zip next door and compares it with `unzip`; and runs the whole conversation against
`tests/fake_server.py`: wrong password, right password, the list (sent chunked), install, start, the background
check's notification (once), and an expired token.
