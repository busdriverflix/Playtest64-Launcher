"""
A stand-in for the playtest server: the three routes the launcher uses, with a zip to download.
The builds list is sent chunked, on purpose, since that is how the real server may send it.
"""
import io
import json
import sys
import zipfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

TOKEN = "test-token"
GOOD = ("tester@example.com", "correct horse")


def make_zip(version: str) -> bytes:
    top = f"GameA-linux-x64-v{version}"
    buffer = io.BytesIO()

    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as z:
        info = zipfile.ZipInfo(f"{top}/game")
        info.external_attr = (0o100755) << 16
        info.create_system = 3
        z.writestr(info, "#!/bin/sh\necho started > started.txt\n")
        z.writestr(f"{top}/game.p64pak", bytes(range(256)) * 4000)
        z.writestr(f"{top}/readme.txt", "hello\n" * 5000)

    return buffer.getvalue()


ZIPS = {1: make_zip("0.2.0"), 2: make_zip("0.2.1")}

BUILDS = [
    {"id": 2, "version": "0.2.1", "notes": "Neuer Build", "uploadedAt": "2026-11-01T00:00:00Z",
     "files": [
         {"id": 2, "platform": "Windows", "fileName": "GameA-windows-x64-v0.2.1.zip", "fileSize": 10, "available": True},
         {"id": 2, "platform": "Linux", "fileName": "GameA-linux-x64-v0.2.1.zip", "fileSize": len(ZIPS[2]), "available": True},
     ]},
    {"id": 1, "version": "0.2.0", "notes": "", "uploadedAt": "2026-10-01T00:00:00Z",
     "files": [
         {"id": 1, "platform": "Linux", "fileName": "GameA-linux-x64-v0.2.0.zip", "fileSize": len(ZIPS[1]), "available": True},
     ]},
]


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def send_json(self, status, payload, chunked=False):
        body = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Connection", "close")

        if chunked:
            self.send_header("Transfer-Encoding", "chunked")
            self.end_headers()
            half = len(body) // 2

            for part in (body[:half], body[half:]):
                self.wfile.write(f"{len(part):x}\r\n".encode() + part + b"\r\n")

            self.wfile.write(b"0\r\n\r\n")
        else:
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

    def authorised(self):
        return self.headers.get("Authorization") == f"Bearer {TOKEN}"

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        body = json.loads(self.rfile.read(length) or b"{}")

        if self.path == "/api/auth/login":
            if (body.get("email"), body.get("password")) == GOOD and body.get("launcher") is True:
                self.send_json(200, {"success": True, "token": TOKEN, "email": GOOD[0], "message": "Erfolgreich angemeldet."})
            else:
                self.send_json(401, {"success": False, "message": "E-Mail oder Passwort ist falsch."})
        else:
            self.send_json(404, {"message": "no"})

    def do_GET(self):
        if self.path == "/api/builds":
            if not self.authorised():
                return self.send_json(401, {"message": "Nicht angemeldet."})

            return self.send_json(200, BUILDS, chunked=True)

        if self.path.startswith("/api/builds/files/") and self.path.endswith("/download"):
            if not self.authorised():
                return self.send_json(401, {"message": "Nicht angemeldet."})

            data = ZIPS.get(int(self.path.split("/")[4]))

            if data is None:
                return self.send_json(404, {"message": "Diese Datei existiert nicht."})

            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Connection", "close")
            self.end_headers()
            self.wfile.write(data)
            return

        self.send_json(404, {"message": "no"})


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8765
    ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()
