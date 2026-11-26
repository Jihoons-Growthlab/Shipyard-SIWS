#!/usr/bin/env python3
"""Install the loopback owner gateway as a macOS login service."""

import json
import os
from pathlib import Path
import plistlib
import re
import shutil
import subprocess
import tempfile


LABEL = "local.siws.nginx-owner"


def replace_file(path, contents):
    with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as stream:
        temp = Path(stream.name)
        try:
            stream.write(contents)
            stream.flush()
            temp.replace(path)
        finally:
            temp.unlink(missing_ok=True)


def main():
    nginx = os.environ.get("NGINX_BIN") or shutil.which("nginx") or "/opt/homebrew/bin/nginx"
    tailscale = os.environ.get("TAILSCALE_BIN") or shutil.which("tailscale") or "/usr/local/bin/tailscale"
    status = json.loads(subprocess.check_output([tailscale, "status", "--json"]))
    self_node = status["Self"]
    host = self_node["DNSName"].rstrip(".").lower()
    owner = status["User"][str(self_node["UserID"])]["LoginName"]
    if status["BackendState"] != "Running" or not re.fullmatch(r"[a-z0-9.-]+\.ts\.net", host):
        raise SystemExit("Connect Tailscale with MagicDNS before installing the owner gateway.")
    if not re.fullmatch(r"[A-Za-z0-9@._+%-]+", owner):
        raise SystemExit("The current Tailscale login cannot be used as an owner identifier.")

    runtime = Path.home() / "Library" / "Application Support" / "SIWS" / "nginx-owner"
    runtime.mkdir(parents=True, exist_ok=True, mode=0o700)
    (runtime / "temp").mkdir(exist_ok=True, mode=0o700)
    config = runtime / "nginx.conf"
    template = Path(__file__).with_name("owner.conf.in").read_text()
    contents = template.replace("__SIWS_HOST__", host).replace("__SIWS_OWNER__", owner).encode()
    with tempfile.NamedTemporaryFile(dir=runtime) as candidate:
        candidate.write(contents)
        candidate.flush()
        subprocess.run([nginx, "-t", "-p", str(runtime) + "/", "-c", candidate.name], check=True)
    replace_file(config, contents)

    agents = Path.home() / "Library" / "LaunchAgents"
    agents.mkdir(exist_ok=True)
    plist = agents / f"{LABEL}.plist"
    replace_file(plist, plistlib.dumps({
        "Label": LABEL,
        "ProgramArguments": [nginx, "-p", str(runtime) + "/", "-c", str(config), "-g", "daemon off;"],
        "RunAtLoad": True,
        "KeepAlive": True,
        "StandardErrorPath": str(runtime / "nginx.log"),
    }))

    domain = f"gui/{os.getuid()}"
    service = f"{domain}/{LABEL}"
    if subprocess.run(["launchctl", "print", service], capture_output=True).returncode == 0:
        # The installed program arguments stay the same on a configuration refresh.
        subprocess.run(["launchctl", "kickstart", "-k", service], check=True)
    else:
        subprocess.run(["launchctl", "bootstrap", domain, str(plist)], check=True)
    print("Installed the SIWS owner gateway on 127.0.0.1:18082.")


if __name__ == "__main__":
    main()
