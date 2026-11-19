#!/usr/bin/env python3
"""
Kompiluje i uruchamia AVH Hub (do szybkiego sprawdzania zmian w UI).

    python run_hub.py                 # Debug, zamyka dzialajacy hub, buduje i startuje
    python run_hub.py --release       # konfiguracja Release
    python run_hub.py --wait          # czeka na zamkniecie okna (zamiast odpiac proces)
    python run_hub.py -- --pair <kod> # wszystko po "--" trafia do avh-hub

Dzialajacy hub trzeba zamknac przed buildem: exe blokuje wlasny plik, a druga kopia
i tak by nie wystartowala (jedna instancja na sesje).
"""

from __future__ import annotations

import argparse
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
PROJECT = ROOT / "src" / "AgentVirtualHand.Hub"
IS_WINDOWS = sys.platform == "win32"
EXE = "avh-hub.exe" if IS_WINDOWS else "avh-hub"


def stop_running() -> None:
    if IS_WINDOWS:
        subprocess.run(["taskkill", "/F", "/IM", EXE], capture_output=True)
    else:
        subprocess.run(["pkill", "-x", "avh-hub"], capture_output=True)


def main() -> int:
    parser = argparse.ArgumentParser(description="Build and run AVH Hub.")
    parser.add_argument("--release", action="store_true", help="build the Release configuration")
    parser.add_argument("--wait", action="store_true", help="stay attached until the window closes")
    parser.add_argument("hub_args", nargs=argparse.REMAINDER, help="arguments passed to avh-hub after --")
    args = parser.parse_args()

    config = "Release" if args.release else "Debug"
    hub_args = args.hub_args[1:] if args.hub_args[:1] == ["--"] else args.hub_args

    stop_running()

    print(f"Building avh-hub ({config})...", flush=True)
    build = subprocess.run(["dotnet", "build", str(PROJECT), "-c", config, "-nologo", "-v", "q"])
    if build.returncode != 0:
        print("Build failed.", file=sys.stderr)
        return build.returncode

    exe = PROJECT / "bin" / config / "net10.0" / EXE
    if not exe.exists():
        print(f"Built, but {exe} was not found.", file=sys.stderr)
        return 1

    print(f"Starting {exe.relative_to(ROOT)}", flush=True)
    if args.wait:
        return subprocess.run([str(exe), *hub_args], cwd=exe.parent).returncode

    flags = 0
    if IS_WINDOWS:
        flags = subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP
    subprocess.Popen([str(exe), *hub_args], cwd=exe.parent, creationflags=flags,
                     stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                     start_new_session=not IS_WINDOWS)
    return 0


if __name__ == "__main__":
    sys.exit(main())
