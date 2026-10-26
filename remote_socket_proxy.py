"""Authenticated SSH stdio -> existing same-user Codex Unix socket.

This is a byte proxy, including the WebSocket HTTP upgrade. It never starts
Codex, reads credentials, or interprets task instructions as shell commands.
"""
import os
import select
import socket
import stat
import sys
from pathlib import Path


def main():
    root = Path(os.environ.get("CODEX_HOME") or Path.home() / ".codex")
    path = root / "app-server-control" / "app-server-control.sock"
    try:
        link = path.lstat()
        resolved = path.resolve(strict=True)
        info = resolved.stat()
        parent = path.parent.lstat()
        target_parent = resolved.parent.stat()
        if link.st_uid != os.getuid() or not stat.S_ISSOCK(info.st_mode) or info.st_uid != os.getuid() or not stat.S_ISDIR(parent.st_mode) or parent.st_uid != os.getuid() or parent.st_mode & 0o022 or target_parent.st_uid != os.getuid() or target_parent.st_mode & 0o022:
            raise PermissionError()
    except FileNotFoundError:
        print("REMOTE_SOCKET_MISSING", file=sys.stderr)
        return 2
    except (OSError, PermissionError):
        print("REMOTE_SOCKET_UNSAFE", file=sys.stderr)
        return 3
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as channel:
        try:
            channel.connect(str(resolved))
        except OSError:
            print("REMOTE_SOCKET_CONNECT_FAILED", file=sys.stderr)
            return 4
        while True:
            readable, _, _ = select.select([sys.stdin.buffer, channel], [], [])
            for source in readable:
                if source is channel:
                    data = channel.recv(65536)
                    if not data:
                        return 0
                    sys.stdout.buffer.write(data)
                    sys.stdout.buffer.flush()
                else:
                    data = os.read(sys.stdin.fileno(), 65536)
                    if not data:
                        return 0
                    channel.sendall(data)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (BrokenPipeError, ConnectionError, OSError):
        sys.exit(5)
