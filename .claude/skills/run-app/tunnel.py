"""A loopback-only HTTP CONNECT tunnel, used to cut one program off from the network.

Point a browser at it (--proxy-server=http://127.0.0.1:<port>). While this runs, traffic
passes through untouched (TLS stays end to end; nothing is read or changed). Kill the
process and every open connection drops and new ones are refused: for that browser the
network is off. Start it again and the network is back.

Logs one line per tunnel (time and host:port only) to claudetracker-tunnel.log in the temp folder.
"""
import datetime
import os
import socket
import sys
import tempfile
import threading

LOG = os.path.join(tempfile.gettempdir(), "claudetracker-tunnel.log")
log_lock = threading.Lock()


def log(text):
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%H:%M:%S")
    with log_lock, open(LOG, "a", encoding="utf-8") as handle:
        handle.write(f"{stamp}Z {text}\n")


def pump(source, sink):
    try:
        while True:
            data = source.recv(65536)
            if not data:
                break
            sink.sendall(data)
    except OSError:
        pass
    finally:
        for sock in (source, sink):
            try:
                sock.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass


def serve(client):
    upstream = None
    try:
        client.settimeout(15)
        head = b""
        while b"\r\n\r\n" not in head:
            chunk = client.recv(4096)
            if not chunk or len(head) > 65536:
                return
            head += chunk
        request_line = head.split(b"\r\n", 1)[0].decode("latin-1")
        method, target, _ = request_line.split(" ", 2)
        if method != "CONNECT":
            client.sendall(b"HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
            return
        host, _, port = target.rpartition(":")
        try:
            upstream = socket.create_connection((host.strip("[]"), int(port)), timeout=15)
        except OSError as error:
            log(f"FAILED {target} ({error.__class__.__name__})")
            client.sendall(b"HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
            return
        log(f"CONNECT {target}")
        client.sendall(b"HTTP/1.1 200 Connection Established\r\n\r\n")
        client.settimeout(None)
        upstream.settimeout(None)
        rest = head.split(b"\r\n\r\n", 1)[1]
        if rest:
            upstream.sendall(rest)
        other = threading.Thread(target=pump, args=(client, upstream), daemon=True)
        other.start()
        pump(upstream, client)
        other.join()
    except (OSError, ValueError):
        pass
    finally:
        for sock in (client, upstream):
            if sock is not None:
                try:
                    sock.close()
                except OSError:
                    pass


def main():
    port = int(sys.argv[1])
    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.bind(("127.0.0.1", port))
    server.listen(64)
    log(f"--- tunnel up on 127.0.0.1:{port} (pid {os.getpid()})")
    while True:
        client, _ = server.accept()
        threading.Thread(target=serve, args=(client,), daemon=True).start()


if __name__ == "__main__":
    main()
