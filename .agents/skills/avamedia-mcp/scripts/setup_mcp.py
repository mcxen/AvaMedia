#!/usr/bin/env python3
"""Reuse/start AvaMedia's background endpoint and register an AI client."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
from urllib.parse import urlparse
from mcp_client import DEFAULT_URL, rpc


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default=DEFAULT_URL)
    parser.add_argument("--client", choices=("codex", "claude", "none"), default="codex")
    parser.add_argument("--executable", help="Installed executable, .app bundle, or developer DLL")
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--start", action="store_true", help="Start only when the endpoint is unavailable")
    parser.add_argument("--state-directory", help="Independent settings/queue directory")
    parser.add_argument("--log-directory", default=str(Path.home() / ".avamedia-mcp"))
    args = parser.parse_args()
    process = None
    try:
        try:
            catalog = rpc(args.url, "tools/list", timeout=5)
        except OSError:
            catalog = None
        if catalog is None:
            if not args.start or not args.executable:
                raise RuntimeError("Endpoint unavailable. Enable MCP in an existing AvaMedia instance, or provide --start --executable.")
            endpoint = urlparse(args.url)
            if endpoint.hostname not in ("127.0.0.1", "localhost") or endpoint.scheme != "http" or endpoint.path != "/mcp":
                raise ValueError("Automatic startup requires a local http://127.0.0.1:PORT/mcp URL.")
            executable = Path(args.executable).expanduser().resolve()
            if executable.suffix == ".app":
                executable = executable / "Contents" / "MacOS" / "AvaMedia.Desktop"
            if not executable.is_file():
                raise FileNotFoundError(f"Application executable not found: {executable}")
            command = [args.dotnet, str(executable)] if executable.suffix == ".dll" else [str(executable)]
            command += ["--mcp-background", "--mcp-port", str(endpoint.port or 80)]
            if args.state_directory:
                command += ["--mcp-state-directory", str(Path(args.state_directory).expanduser().resolve())]
            log_root = Path(args.log_directory).expanduser().resolve()
            log_root.mkdir(parents=True, exist_ok=True)
            log_path = log_root / f"host-{time.time_ns()}.log"
            options = {"start_new_session": True} if os.name != "nt" else {
                "creationflags": subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP,
            }
            with log_path.open("ab") as log:
                process = subprocess.Popen(command, cwd=executable.parent, stdin=subprocess.DEVNULL,
                                           stdout=log, stderr=subprocess.STDOUT, **options)
            deadline = time.monotonic() + 20
            while time.monotonic() < deadline:
                if process.poll() is not None:
                    raise RuntimeError(f"Background host exited ({process.returncode}); read {log_path}")
                try:
                    catalog = rpc(args.url, "tools/list", timeout=2)
                    break
                except OSError:
                    time.sleep(0.5)
            if catalog is None:
                raise RuntimeError(f"Endpoint has not become ready. Process {process.pid} was retained; read {log_path} before retrying.")
        names = {tool["name"] for tool in catalog.get("tools", [])}
        if "avamedia_capabilities" not in names:
            raise RuntimeError("This endpoint is not AvaMedia; keep existing client configuration unchanged.")
        missing = sorted({"avamedia_get_runtime", "avamedia_wait_task", "avamedia_preview_organization"} - names)
        if args.client != "none":
            client = shutil.which(args.client)
            if client is None:
                raise FileNotFoundError(f"{args.client} CLI is unavailable. Configure the printed URL in your AI client.")
            command = [client, "mcp", "add", "avamedia", "--url", args.url] if args.client == "codex" else [
                client, "mcp", "add", "--transport", "http", "--scope", "user", "avamedia", args.url,
            ]
            subprocess.run(command, check=True, timeout=20, stdout=sys.stderr)
        print(json.dumps({"url": args.url, "client": args.client, "toolCount": len(names),
                          "missingBackgroundTools": missing, "startedPid": process.pid if process else None},
                         ensure_ascii=False, indent=2))
        if missing:
            print("The running host lacks newer tools; install a build that provides them before using those workflows.", file=sys.stderr)
        return 0
    except (OSError, RuntimeError, ValueError, subprocess.SubprocessError) as error:
        print(str(error), file=sys.stderr)
        if process is not None and process.poll() is None:
            print(f"Background process {process.pid} retained; do not start a duplicate host.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
