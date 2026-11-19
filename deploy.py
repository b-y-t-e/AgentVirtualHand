#!/usr/bin/env python3
"""
Wydanie AVH na https://greysource.eu/avh/ przez FTP (jak deploy.py w TailSync).

    python deploy.py                  # tylko strona (site/)
    python deploy.py --build          # nowa wersja: kompilacja hub + assist, Setup.exe, portable, kanal aktualizacji
    python deploy.py --build --no-bump
    python deploy.py --upload-builds  # wysyla juz zbudowane artifacts/ bez kompilacji
    python deploy.py --build --local  # tylko zbuduj do artifacts/, nic nie wysylaj
    python deploy.py --preview        # podglad strony lokalnie: http://localhost:8041/avh/

Co ladzie na serwerze (/avh):
    index.html, style.css, app.js, ...   strona z site/
    downloads/downloads.json             lista plikow, z ktorej strona buduje przyciski
    downloads/AVH-Hub-<v>-setup.exe      instalator (Velopack, bez admina, z aktualizacjami)
    downloads/AVH-Hub-<v>-portable.zip   exe + msquic.dll, bez instalacji i bez aktualizacji
    updates/hub/, updates/assist/        kanaly Velopack, z ktorych czyta AppUpdates w aplikacji

Dane FTP: ftp_credentials.json obok tego pliku ({"host", "user", "password"}), poza gitem.
Wymaga narzedzia vpk:  dotnet tool install -g vpk
"""

from __future__ import annotations

import argparse
import functools
import http.server
import json
import shutil
import subprocess
import sys
import zipfile
from datetime import datetime, timezone
from ftplib import FTP, error_perm
from pathlib import Path

ROOT = Path(__file__).resolve().parent
FTP_DIR = "/greysource.eu/wwwroot/avh"
URL = "https://greysource.eu/avh/"

CREDENTIALS_FILE = ROOT / "ftp_credentials.json"
VERSION_FILE = ROOT / "version.txt"
SITE_DIR = ROOT / "site"
ARTIFACTS = ROOT / "artifacts"
PUBLISH_DIR = ARTIFACTS / "publish"
DOWNLOADS_DIR = ARTIFACTS / "downloads"
UPDATES_DIR = ARTIFACTS / "velopack"

RID = "win-x64"


class App:
    def __init__(self, key: str, project: str, exe: str, title: str, pack_id: str, file_name: str, icon: str):
        self.key = key              # kanal aktualizacji i klucz w downloads.json
        self.project = project
        self.exe = exe              # plik wykonywalny po publish
        self.title = title          # nazwa w menu Start i w deinstalacji
        # Velopack instaluje do %LocalAppData%\<packId> i kasuje ten katalog przy deinstalacji,
        # dlatego packId rozni sie od katalogow danych (%APPDATA%\avh, %APPDATA%\avh-hub).
        self.pack_id = pack_id
        self.file_name = file_name  # przedrostek plikow do pobrania
        self.icon = icon


APPS = [
    App("hub", "src/AgentVirtualHand.Hub", "avh-hub.exe", "AVH Hub", "AVH.Hub", "AVH-Hub",
        "src/AgentVirtualHand.Hub/Assets/avh-hub.ico"),
    App("assist", "src/AgentVirtualHand.Assist", "avh-assist.exe", "AVH Assist", "AVH.Assist", "AVH-Assist",
        "src/AgentVirtualHand.Assist/Assets/avh-assist.ico"),
]


# ------------------------------------------------------------------------------- wersja

def version() -> str:
    return VERSION_FILE.read_text(encoding="utf-8").strip()


def bump_version() -> str:
    """Kazde wydanie dostaje nowy numer - bez tego zainstalowane aplikacje nie zobacza aktualizacji."""
    parts = version().split(".")
    parts[-1] = str(int(parts[-1]) + 1)
    VERSION_FILE.write_text(".".join(parts) + "\n", encoding="utf-8")
    return version()


# ------------------------------------------------------------------------------- budowanie

def run(command: list[str], **kwargs) -> None:
    print("    $ " + " ".join(command[:3]) + (" ..." if len(command) > 3 else ""), flush=True)
    subprocess.run(command, check=True, cwd=ROOT, **kwargs)


def publish(app: App, out: Path) -> None:
    if out.exists():
        shutil.rmtree(out)
    run([
        "dotnet", "publish", str(ROOT / app.project),
        "-c", "Release", "-r", RID, "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:DebugType=none",
        "-o", str(out), "--nologo", "-v", "q",
    ])
    for pdb in out.glob("*.pdb"):
        pdb.unlink()
    # Bez msquic.dll obok exe nie ma QUIC i kazda sesja idzie po cichu przez relay (CLAUDE.md).
    if not (out / "msquic.dll").exists():
        raise SystemExit(f"Brak msquic.dll w {out} - nie wydajemy {app.title} bez niego")


def pack_portable(app: App, source: Path, v: str) -> Path:
    target = DOWNLOADS_DIR / f"{app.file_name}-{v}-portable.zip"
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as z:
        for f in sorted(source.rglob("*")):
            if f.is_file():
                z.write(f, f"{app.title}/{f.relative_to(source).as_posix()}")
    return target


def is_released(app: App, v: str) -> bool:
    """Wersja jest juz w lokalnym kanale (np. --no-bump): vpk nie spakuje jej drugi raz."""
    feed = UPDATES_DIR / app.key / "releases.win.json"
    if not feed.exists():
        return False
    assets = json.loads(feed.read_text(encoding="utf-8-sig")).get("Assets", [])
    return any(a.get("Version") == v for a in assets)


def pack_installer(app: App, source: Path, v: str) -> Path:
    """Setup.exe (Velopack): bez uprawnien administratora, skrot w menu Start, deinstalacja w ustawieniach.
    Katalog kanalu zostaje miedzy wydaniami - vpk robi z poprzedniego pakietu delte."""
    out = UPDATES_DIR / app.key
    out.mkdir(parents=True, exist_ok=True)
    if not is_released(app, v):
        for old in out.glob("*-Setup.exe"):
            old.unlink()
        run([
            "vpk", "pack",
            "--packId", app.pack_id,
            "--packVersion", v,
            "--packDir", str(source),
            "--mainExe", app.exe,
            "--runtime", RID,
            "--packTitle", app.title,
            "--packAuthors", "Andrzej Ból",
            "--icon", str(ROOT / app.icon),
            "--outputDir", str(out),
            "--yes",
        ])
    setup = next(out.glob("*-Setup.exe"))
    target = DOWNLOADS_DIR / f"{app.file_name}-{v}-setup.exe"
    shutil.copy2(setup, target)
    return target


def build() -> None:
    if shutil.which("vpk") is None:
        raise SystemExit("Brak narzedzia vpk: dotnet tool install -g vpk")

    v = version()
    if DOWNLOADS_DIR.exists():
        shutil.rmtree(DOWNLOADS_DIR)
    DOWNLOADS_DIR.mkdir(parents=True)

    manifest = {"version": v, "built": datetime.now(timezone.utc).isoformat(timespec="seconds"), "apps": {}}
    for app in APPS:
        print(f"  {app.title}")
        out = PUBLISH_DIR / app.key
        publish(app, out)
        setup = pack_installer(app, out, v)
        portable = pack_portable(app, out, v)
        manifest["apps"][app.key] = {
            "setup": {"file": setup.name, "size": setup.stat().st_size},
            "portable": {"file": portable.name, "size": portable.stat().st_size},
        }

    (DOWNLOADS_DIR / "downloads.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    for f in sorted(DOWNLOADS_DIR.iterdir()):
        print(f"    {f.name:<36} {f.stat().st_size / 1e6:6.1f} MB")


# ------------------------------------------------------------------------------- wysylanie

def ensure_dir(ftp: FTP, path: str) -> None:
    try:
        ftp.mkd(path)
    except error_perm:
        pass  # juz istnieje


def list_files(ftp: FTP, path: str) -> set[str]:
    try:
        return {Path(name).name for name in ftp.nlst(path)}
    except error_perm:
        return set()


def remote_size(ftp: FTP, path: str) -> int | None:
    try:
        ftp.voidcmd("TYPE I")
        return ftp.size(path)
    except error_perm:
        return None


def store(ftp: FTP, local: Path, remote: str) -> None:
    print(f"  -> {remote} ({local.stat().st_size / 1e3:,.0f} kB)", flush=True)
    with local.open("rb") as f:
        ftp.storbinary(f"STOR {remote}", f, blocksize=1 << 20)


def upload_dir(ftp: FTP, local: Path, remote: str, stale: list[str], keep: tuple[str, ...] = ()) -> None:
    """Wysyla katalog; do `stale` trafiaja pliki z serwera, ktorych nie ma juz lokalnie."""
    ensure_dir(ftp, remote)
    names = set()
    for item in sorted(local.iterdir()):
        names.add(item.name)
        if item.is_dir():
            upload_dir(ftp, item, f"{remote}/{item.name}", stale)
        else:
            store(ftp, item, f"{remote}/{item.name}")
    stale.extend(f"{remote}/{name}" for name in list_files(ftp, remote) - names - set(keep))


def upload_downloads(ftp: FTP, stale: list[str]) -> None:
    """Paczki najpierw, downloads.json na koncu: strona nie pokaze linku do pliku, ktorego jeszcze nie ma."""
    remote = f"{FTP_DIR}/downloads"
    ensure_dir(ftp, remote)
    names = set()
    for item in sorted(DOWNLOADS_DIR.iterdir(), key=lambda p: p.name == "downloads.json"):
        names.add(item.name)
        store(ftp, item, f"{remote}/{item.name}")
    stale.extend(f"{remote}/{name}" for name in list_files(ftp, remote) - names)


def upload_updates(ftp: FTP) -> None:
    """Kanaly aktualizacji. Pakiety najpierw, releases.win.json na koncu: aplikacja nie zobaczy
    wydania bez jego pliku. Raz wyslany pakiet (ten sam rozmiar) nie idzie drugi raz."""
    ensure_dir(ftp, f"{FTP_DIR}/updates")
    for app in APPS:
        local = UPDATES_DIR / app.key
        if not (local / "releases.win.json").exists():
            continue
        remote = f"{FTP_DIR}/updates/{app.key}"
        ensure_dir(ftp, remote)
        present = list_files(ftp, remote)
        files = [f for f in sorted(local.iterdir())
                 if f.is_file() and not f.name.endswith(("-Setup.exe", "-Portable.zip"))]
        files.sort(key=lambda f: f.name.startswith("releases."))
        for item in files:
            target = f"{remote}/{item.name}"
            if item.suffix == ".nupkg" and item.name in present and remote_size(ftp, target) == item.stat().st_size:
                continue
            store(ftp, item, target)


def delete_stale(ftp: FTP, stale: list[str]) -> None:
    # Usuwanie dopiero po wyslaniu wszystkiego - wczesniej strona wskazywalaby skasowane pliki.
    for path in stale:
        try:
            ftp.delete(path)
            print(f"  x  {path}")
        except error_perm:
            pass  # katalog - zostawiamy


# ------------------------------------------------------------------------------- podglad

def preview(port: int) -> None:
    """Strona pod /avh/ jak na serwerze; /avh/downloads/ z artifacts/downloads, jesli jest build."""

    class Handler(http.server.SimpleHTTPRequestHandler):
        def translate_path(self, path: str) -> str:
            path = path.split("?", 1)[0]
            if path.startswith("/avh/downloads/"):
                return str(DOWNLOADS_DIR / path[len("/avh/downloads/"):])
            if path.startswith("/avh/"):
                return str(SITE_DIR / (path[len("/avh/"):] or "index.html"))
            return str(SITE_DIR / "__missing__")

        def end_headers(self) -> None:
            self.send_header("Cache-Control", "no-store")
            super().end_headers()

    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), functools.partial(Handler))
    print(f"Podglad: http://localhost:{port}/avh/   (Ctrl+C konczy)")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


# ------------------------------------------------------------------------------- main

def main() -> int:
    parser = argparse.ArgumentParser(description="Wydanie AVH (hub + assist) i strony na greysource.eu/avh")
    parser.add_argument("--build", action="store_true", help="skompiluj hub i assist, zrob Setup.exe i portable, wyslij")
    parser.add_argument("--no-bump", action="store_true", help="przy --build nie podnos wersji (zainstalowane nie dostana aktualizacji)")
    parser.add_argument("--upload-builds", action="store_true", help="wyslij istniejace artifacts/ bez kompilacji")
    parser.add_argument("--local", action="store_true", help="przy --build tylko zbuduj, nic nie wysylaj")
    parser.add_argument("--preview", action="store_true", help="podglad strony lokalnie, nic nie wysyla")
    parser.add_argument("--port", type=int, default=8041)
    args = parser.parse_args()

    if args.preview:
        preview(args.port)
        return 0

    if args.build:
        if not args.no_bump:
            print(f"wersja {bump_version()} (version.txt - zacommituj po deployu)")
        # Dzialajacy exe blokuje wlasny plik w bin/ - zamknij go przed kompilacja.
        for app in APPS:
            subprocess.run(["taskkill", "/F", "/IM", app.exe], capture_output=True)
        print("[1/2] Kompilacja i paczki...")
        build()

    if args.local:
        print(f"Zbudowane w {DOWNLOADS_DIR} - nic nie wyslano.")
        return 0

    send_builds = args.build or args.upload_builds
    if send_builds and not (DOWNLOADS_DIR / "downloads.json").exists():
        raise SystemExit("Brak artifacts/downloads - uruchom z --build")

    if not CREDENTIALS_FILE.exists():
        raise SystemExit(f"Brak {CREDENTIALS_FILE.name} ({{\"host\", \"user\", \"password\"}})")
    creds = json.loads(CREDENTIALS_FILE.read_text(encoding="utf-8"))

    print("[2/2] Wysylanie..." if args.build else "Wysylanie...")
    with FTP(creds["host"], timeout=300) as ftp:
        ftp.login(creds["user"], creds["password"])
        ftp.set_pasv(True)
        stale: list[str] = []

        if send_builds:
            upload_downloads(ftp, stale)
            upload_updates(ftp)

        # downloads/ i updates/ na serwerze nie sa czescia site/ - nie traktujemy ich jako nieaktualnych.
        upload_dir(ftp, SITE_DIR, FTP_DIR, stale, keep=("downloads", "updates"))
        delete_stale(ftp, stale)

    print(f"Gotowe: {URL}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
