"""
The launcher's tests, from outside, the way a tester uses it:

  1. the unpacker against zips Python made (stored, deflated, empty files, a file bigger than the
     deflate window, an executable one), and against the real game zips if the engine repository is next door;
  2. a path that climbs out of the folder is refused;
  3. the whole thing against a stand-in server: wrong password, right password, the list (sent chunked),
     installing a build and starting it, and the background check's notification.

Run through the makefile: make tests
"""
import hashlib
import io
import os
import random
import shutil
import stat
import subprocess
import sys
import tempfile
import threading
import time
import zipfile
from http.server import ThreadingHTTPServer
from pathlib import Path

HERE = Path(__file__).resolve().parent
LAUNCHER = HERE.parent / "build" / "linux" / "launcher"
sys.path.insert(0, str(HERE))
import fake_server  # noqa: E402

failures = 0


def check(condition, what):
    global failures
    print(("ok    " if condition else "FAIL  ") + what)

    if not condition:
        failures += 1


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def extract(zip_path, target):
    return subprocess.run([str(LAUNCHER), "--extract", str(zip_path), str(target)], capture_output=True, text=True)


def test_unpacker(work):
    rng = random.Random(7)
    noise = bytes(rng.getrandbits(8) for _ in range(300_000))		# does not compress: stored blocks
    text = ("The penguin slides. " * 20000).encode()					# compresses to almost nothing: long matches
    mixed = b"".join(noise[i:i + 50] + text[i:i + 500] for i in range(0, 100_000, 50))

    z = work / "a.zip"

    with zipfile.ZipFile(z, "w") as archive:
        archive.writestr(zipfile.ZipInfo("top/"), "")
        archive.writestr(zipfile.ZipInfo("top/empty.txt"), "")
        archive.writestr("top/noise.bin", noise, zipfile.ZIP_STORED)
        archive.writestr("top/noise-deflated.bin", noise, zipfile.ZIP_DEFLATED)
        archive.writestr("top/text.txt", text, zipfile.ZIP_DEFLATED)
        archive.writestr("top/mixed.bin", mixed, zipfile.ZIP_DEFLATED, compresslevel=9)
        archive.writestr("top/sub/dir/deep.txt", "deep", zipfile.ZIP_DEFLATED)
        info = zipfile.ZipInfo("top/run")
        info.create_system = 3
        info.external_attr = 0o100755 << 16
        archive.writestr(info, "#!/bin/sh\n")

    out = work / "a"
    result = extract(z, out)

    check(result.returncode == 0 and result.stdout.strip() == "top", f"unpacks a zip and names the top folder ({result.stderr.strip()})")

    expected = {"empty.txt": b"", "noise.bin": noise, "noise-deflated.bin": noise, "text.txt": text, "mixed.bin": mixed, "sub/dir/deep.txt": b"deep"}

    for name, data in expected.items():
        check((out / "top" / name).exists() and (out / "top" / name).read_bytes() == data, f"{name} comes out as it went in")

    check(os.stat(out / "top" / "run").st_mode & stat.S_IXUSR != 0, "an executable file is still executable")
    check(os.stat(out / "top" / "noise.bin").st_mode & stat.S_IXUSR == 0, "an ordinary file is not made executable")

    # damage: one flipped byte in the middle of a deflated file must be noticed, by the checksum or the stream
    data = bytearray(z.read_bytes())
    data[len(data) // 3] ^= 0xFF
    bad = work / "bad.zip"
    bad.write_bytes(data)
    check(extract(bad, work / "bad").returncode != 0, "a damaged zip is refused")

    # a name that climbs out of the folder
    evil = work / "evil.zip"

    with zipfile.ZipFile(evil, "w") as archive:
        archive.writestr("../escaped.txt", "x")

    check(extract(evil, work / "evil").returncode != 0 and not (work / "escaped.txt").exists(), "a path that climbs out is refused")

    not_a_zip = work / "no.zip"
    not_a_zip.write_bytes(b"hello" * 100)
    check(extract(not_a_zip, work / "no").returncode != 0, "a file that is not a zip is refused")

    # the real thing, when the game's zips are there
    zips = sorted(Path(HERE.parent.parent / "P64-Engine-Claude" / "dist" / "zips").glob("GameA-linux-x64-v*.zip"))

    if zips:
        ours = work / "real"
        theirs = work / "real-unzip"
        result = extract(zips[-1], ours)
        subprocess.run(["unzip", "-q", str(zips[-1]), "-d", str(theirs)], check=True)
        same = all(sha(p) == sha(theirs / p.relative_to(ours)) for p in ours.rglob("*") if p.is_file())
        count = sum(1 for p in ours.rglob("*") if p.is_file())
        check(result.returncode == 0 and same and count > 0, f"the real build {zips[-1].name} unpacks like unzip does ({count} files)")


def run(args, home, env_extra=None, stdin=None):
    env = dict(os.environ, PLAYTEST_LAUNCHER_HOME=str(home), PLAYTEST_SERVER="http://127.0.0.1:8765",
               HOME=str(home), PATH="/usr/bin:/bin", PLAYTEST_LAUNCHER_NOTIFY="print")
    env.update(env_extra or {})
    return subprocess.run([str(LAUNCHER)] + args, input=stdin, capture_output=True, text=True, env=env, timeout=60)


def test_with_a_server(work):
    server = ThreadingHTTPServer(("127.0.0.1", 8765), fake_server.Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    home = work / "home"
    home.mkdir()

    # wrong password, then the right one, then the newer build is installed and started
    wrong = run([], home, stdin="tester@example.com\nnope\n\n")
    check("falsch" in wrong.stdout, "a wrong password is answered with the server's words")
    check(not (home / "launcher.cfg").exists() or "token=test-token" not in (home / "launcher.cfg").read_text(), "and nothing is saved")

    result = run([], home, stdin="tester@example.com\ncorrect horse\n1\n")
    out = result.stdout
    check("0.2.1" in out and "0.2.0" in out, "the list shows both builds for this system")
    check("Installiert." in out and "Das Spiel startet." in out, f"installing and starting the newest build works ({result.stderr.strip()[:200]})")
    check((home / "builds" / "0.2.1" / "installed.txt").read_text().strip() == "GameA-linux-x64-v0.2.1", "the install is marked complete, with the folder name")
    check(not list((home / "downloads").glob("*")), "the downloaded zip is cleaned up")

    # the game was started in its own folder
    started = home / "builds" / "0.2.1" / "GameA-linux-x64-v0.2.1" / "started.txt"

    for _ in range(40):
        if started.exists():
            break

        time.sleep(0.1)

    check(started.exists(), "the game ran, in its own folder")
    check("token=test-token" in (home / "launcher.cfg").read_text(), "the sign-in is remembered")

    # the second time there is no sign-in, and the build is known as installed
    again = run([], home, stdin="q\n")
    check("E-Mail:" not in again.stdout and "0.2.1  - installiert" in again.stdout, "the next start needs no sign-in and knows what is installed")

    # the background check: the person was shown the list (so nothing is new), then the server has a build the person has not seen
    quiet = run(["--check"], home)
    check(quiet.returncode == 0 and "Neuer Playtest-Build" not in quiet.stderr, "the background check says nothing when nothing is new")

    cfg = home / "launcher.cfg"
    cfg.write_text(cfg.read_text().replace("announced_build=2", "announced_build=1"))
    shutil.rmtree(home / "builds" / "0.2.1")
    news = run(["--check"], home)
    check("Neuer Playtest-Build" in news.stderr and "0.2.1" in news.stderr, "the background check announces a build that is new to the person")
    twice = run(["--check"], home)
    check("Neuer Playtest-Build" not in twice.stderr, "and only once")

    # a token the server does not accept: the person is asked to sign in again
    cfg.write_text(cfg.read_text().replace("token=test-token", "token=stale"))
    stale = run(["--check"], home)
    check("neu an" in stale.stderr, "an expired sign-in is announced")
    check("token=stale" not in cfg.read_text(), "and the dead token is dropped")

    server.shutdown()


def main():
    if not LAUNCHER.exists():
        print("build the launcher first: make")
        return 1

    work = Path(tempfile.mkdtemp(prefix="launcher-test-"))

    try:
        test_unpacker(work)
        test_with_a_server(work)
    finally:
        shutil.rmtree(work, ignore_errors=True)

    print("all ok" if failures == 0 else f"{failures} failure(s)")
    return 0 if failures == 0 else 1


sys.exit(main())
