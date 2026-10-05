"""A release feed whose setup download misbehaves, to see the updater's limits hold.

  python odd-feed.py stall          sends the first megabyte of a 50 MB setup, then nothing
  python odd-feed.py declared-huge  says the setup is 700 MB before sending any of it
  python odd-feed.py endless        sends a setup with no stated size, and never stops

Serves http://127.0.0.1:38920/releases.json in GitHub's shape, like feed.ps1 (stop that one
first: same port). Run it hidden, watch the installed copy against it, then kill it:

  $feed = Start-Process python -ArgumentList "odd-feed.py", "stall" -WindowStyle Hidden -PassThru
  powershell -File watch-update.ps1 -WatchSeconds 92     # stall needs 10 + 10 + 60 seconds
  Stop-Process -Id $feed.Id

Expect "Couldn't download the update" with "Install" still offered, nothing left in
%LocalAppData%\\ClaudeTracker\\Updates, and in the log: "The operation was canceled." a
minute into the silence (stall); "the setup offered is larger than 600 MB" at once
(declared-huge) or after 600 MB have arrived and been deleted again (endless).
What it tells of each request goes to stderr.
"""
import http.server
import json
import sys
import time

MODE = sys.argv[1] if len(sys.argv) > 1 else ""
if MODE not in ("stall", "declared-huge", "endless"):
    sys.exit(__doc__)
PORT = 38920
BASE = f"http://127.0.0.1:{PORT}"
RELEASES = [{
    "tag_name": "v0.2.0", "html_url": f"{BASE}/release.html", "prerelease": False, "draft": False,
    "published_at": "2026-10-05T12:00:00Z",
    "assets": [
        {"name": "ClaudeTracker-Setup.exe", "browser_download_url": f"{BASE}/ClaudeTracker-Setup.exe"},
        {"name": "ClaudeTracker-Setup.exe.sig", "browser_download_url": f"{BASE}/ClaudeTracker-Setup.exe.sig"},
    ],
}, {
    "tag_name": "v0.0.1", "html_url": f"{BASE}/release.html", "prerelease": False, "draft": False,
    "published_at": "2026-10-03T12:00:00Z", "assets": [],
}]
CHUNK = b"\0" * (1024 * 1024)


def say(text):
    sys.stderr.write(text + "\n")
    sys.stderr.flush()


class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, pattern, *arguments):
        say("%s %s" % (time.strftime("%H:%M:%S"), pattern % arguments))

    def do_GET(self):
        if self.path == "/releases.json":
            body = json.dumps(RELEASES).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        elif self.path == "/ClaudeTracker-Setup.exe.sig":
            self.send_response(200)
            self.send_header("Content-Length", "64")
            self.end_headers()
            self.wfile.write(b"\0" * 64)
        elif self.path == "/ClaudeTracker-Setup.exe":
            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            if MODE == "stall":
                self.send_header("Content-Length", str(50 * 1024 * 1024))
            elif MODE == "declared-huge":
                self.send_header("Content-Length", str(700 * 1024 * 1024))
            self.end_headers()
            sent = 0
            try:
                if MODE == "stall":
                    self.wfile.write(CHUNK)
                    self.wfile.flush()
                    say("sent 1 MB, now silent")
                    time.sleep(600)
                else:
                    while True:
                        self.wfile.write(CHUNK)
                        sent += 1
            except OSError as gone:
                say(f"the app hung up after {sent} MB ({type(gone).__name__})")
        else:
            self.send_error(404)


class Server(http.server.ThreadingHTTPServer):
    daemon_threads = True


Server(("127.0.0.1", PORT), Handler).serve_forever()
