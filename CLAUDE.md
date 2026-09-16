# CLAUDE.md

Guidance for working in this repository.

## What this is

AVH is an emergency remote-control tool. A **host** app runs on the machine to be controlled and
exposes it over **Tailcat.Link** (no IP, no open port). A **client** connects and runs shell
commands and file operations. There are two clients: a windowed hub and a text CLI.

Three projects, one solution (`AgentVirtualHand.slnx`), .NET 10, Tailcat.Link **0.5.2** (NuGet):

- `src/AgentVirtualHand` -> builds **`avh`** (host GUI, and `--headless` mode). Avalonia + Kestrel.
- `src/AgentVirtualHand.Hub` -> builds **`avh-hub`** (windowed client, list of machines).
- `src/AgentVirtualHand.Link` -> builds **`avh-link`** (text client for one machine).

Some files live in the host project and are linked into the clients via `<Compile Include>` /
`<AvaloniaResource Include>`. **Edit the original, not a copy**:

- hub + link: `Server/LinkProtocol.cs`, `Server/ExecLimits.cs`
- hub only: `Server/LinkQuery.cs`, `Server/ExecScript.cs`, `Server/BomText.cs`,
  `Services/PasswordGate.cs`, `Services/AppPaths.cs`, `Services/SingleInstance.cs`,
  `ViewModels/LockViewModel.cs`, `Styles/Theme.axaml`

## Build and release

```bash
dotnet build -c Release AgentVirtualHand.slnx    # build all three
python build.py                                  # standalone single-file exes -> publish/win-x64
python build.py --only host --clean              # one app; --rid linux-x64 for Linux
```

On Windows the publish folder also gets `msquic.dll` (from the package's `buildTransitive` targets).
It must ship next to the exe; without it QUIC is missing and every session silently uses the relay.

Always stop running processes before publishing (the exe locks its own file):
`Get-Process avh,avh-hub,avh-link | Stop-Process -Force`.

## How the pieces fit

- **Transport vs API.** Tailcat.Link is only transport. The host runs a Kestrel HTTP server on
  `127.0.0.1` on a **system-assigned port**; `LinkHost` forwards link requests to it with the
  peer's session token. Exec, fs and session logic live once, in `RemoteHttpServer`.
- **Two wire formats in one handler** (`LinkHost.HandleAsync`, helpers in `LinkWire`):
  - **empty metadata = legacy** (`avh-link` and its daemon): the whole `LinkRequest` JSON, body
    included, is the content; the reply is a whole `LinkResponse` JSON. Buffered in memory.
  - **metadata present = streaming** (hub): the envelope without body travels in
    `LinkContent.Metadata`, the body is the content stream of any size.
  Keep both working - `avh-link` has not moved to streaming.
- **Streamed files bypass loopback.** In the streaming path `fs/upload` and `fs/download`
  (`LinkFileRoutes`) are handled directly by `StreamedFileTransfers`: upload goes to a `~<guid>.tmp`
  next to the target and is renamed at the end; download streams from disk. They skip the loopback
  `AuthFilter`, so they do their own audit and wrap the file in `SessionBoundStream`, which throws
  `AccessClosedException` once the session is gone or its token changed. Everything else still goes
  through loopback.
- **Hub side** (`LinkConnection`): uploads are spooled to a delete-on-close temp file so the
  library can rewind and resume after a relay blip; downloads are copied 1:1 into the HTTP response
  with `Content-Length`; small bodies are buffered so they can be logged.
- **Exec limits** (`ExecLimits`): one link round-trip must return within ~30 s. Response hold is
  capped at 25 s (`MaxResponseHoldSeconds`, the `wait=` long-poll on `/api/exec/{id}`), and a sync
  `exec` at 22 s (`MaxSyncExecSeconds`, leaving time to drain output). Host, HelpText, PromptBuilder
  and `avh-link` usage all read these constants - never hard-code the numbers.
- **Scripts.** `exec` accepts `command`, `script` or `scriptBase64`. `PreparedCommand` writes a
  script to a private temp dir (`CreateTempSubdirectory`, 0700 on Unix) and `ShellDialect` decides
  how each shell runs it (extension, encoding, exit code). Process output is split into lines by
  `OutputLineDecoder` (handles UTF-16 output from native tools).
- **Multi-peer.** `TailcatLink.HostManyAsync` -> `ILinkHost`; several clients pair at once.
  `SessionManager` keeps one session per peer key: own token, own time window. Cutting one off
  does not touch the others.
- **Invite codes are single-use** (`InvitationRequest { SingleUse = true }`). Using one grants a
  session immediately (`PeerJoined` -> `SessionManager.Open`) and the code disappears from the UI.
  A paired machine without a session can be let in again from the host window (`AdmitMachine`).
- **Isolation in the hub.** Each connection = its own loopback port + own bearer token + own
  Tailcat pairing folder (`links/<id>`). `PromptBuilder` bakes one machine's URL and token into
  the model prompt, so a prompt for one machine cannot reach another.

## Security rules - do not regress

- Every host endpoint except `/api/status` goes through `AuthFilter`; anything that skips loopback
  (streamed transfers) must check the session itself and stop when it ends.
- HTTP servers bind to `127.0.0.1` only.
- Anything that changes the machine (exec, script, fs write/upload/delete/move) must show up in
  the audit log. Known gaps today: stdin, fs read/list/mkdir, downloads over the legacy path.
- **Cut off all**, **Stop link**, closing the window and headless Ctrl+C kill all processes;
  single-machine Cut off and session expiry do not (processes are not tied to a session).
- Hub tokens are stored in plain text in `connections.json` - do not spread them further (logs,
  error messages).

## Conventions that matter

- **All user-facing text is English** (UI labels, hints, log messages, console output, API
  errors, HelpText, PromptBuilder, CLI usage). Code comments and commit messages are often Polish;
  a few leftover Polish API errors/log lines still exist - translate them when touching that code.
- **Do not rename internal identifiers** without cause: `AppName = "agentvirtualhand"` is the
  Tailcat pairing identity - changing it breaks every existing pairing. Namespaces and class
  names stay `AgentVirtualHand.*` even though the product shows as "AVH".
- **On-disk names are short and neutral**: `%APPDATA%\avh` and `%APPDATA%\avh-hub` (see
  `AppPaths`), `avh.link.json` (see `HiddenLinkStore`), `avh-error.log` next to the exe, temp files
  `~<guid>.tmp`, script dir `avh-*`. Keep new artifacts in the same style.
- **Time-dependent logic takes an injected `TimeProvider`** (see `LockViewModel`,
  `SessionBoundStream`, `LinkHost`) so it can be unit-tested without waiting.
- **State is in memory.** Sessions and host tokens do not survive a host restart - that is intended.

## Testing

There is no test project; checks are throwaway console apps under the scratchpad that link the
real source files and assert with a small `Check` helper. When touching security or timing
(password gate, lock countdown, session expiry, session-bound transfers, folder migration), write
such a check and run it rather than relying on the GUI - GUI automation here cannot reliably type
into a non-foreground window.

End-to-end: run `avh --headless`, grab the printed code, and drive `avh-link` against it (legacy
path). For the streaming path, pair `avh-hub --pair <code>` and hit its loopback URL with `curl`;
check large transfers with a SHA-256 in both directions.

## Gotchas hit before

- Kestrel reads `appsettings.json` from the working directory as its own config; a stray one
  next to the exe added a phantom endpoint. `builder.Configuration.Sources.Clear()` prevents it.
- The Fluent theme draws its own focus border on `TextBox`; plain selectors lose to it. Text-box
  colors/thickness are set via `TextControl*` resource keys in `App.axaml`.
- Raw string literals (`$$"""..."""`) do **not** process `\\` - a doubled backslash stays doubled,
  which is exactly the correct JSON form in HelpText/PromptBuilder examples. In `$$` literals,
  constants are interpolated with `{{...}}`.
- `HeartbeatInterval` is set to **5 s** on both ends (`LinkHost`, `LinkConnection`), below Tailcat's
  ~10 s silence threshold. The default 15 s let an idle relayed link drop and reconnect every 10 s.
  Do not raise it back without re-checking idle behaviour over the relay.
- Tailcat.Link is consumed only as a NuGet package - do not add a project reference to its source.
  Its changelog is the package release notes; 0.5.2 keeps relay sessions alive through cuts.
- Tailcat.Link rejects a `LinkContent` with an empty `ContentType`; `LinkWire` always defaults to
  `application/octet-stream` (the real type rides in the envelope).
- `MediaTypeHeaderValue`'s constructor throws on `application/json; charset=utf-8`; use `TryParse`
  when forwarding a client's content type.
- A script sent as UTF-8 with BOM starts with U+FEFF, which breaks the first command in `.sh`/`.cmd`;
  `PreparedCommand` strips it.
