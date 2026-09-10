# CLAUDE.md

Guidance for working in this repository.

## What this is

AVH is an emergency remote-control tool. A **host** app runs on the machine to be controlled and
exposes it over **Tailcat.Link** (no IP, no open port). A **client** connects and runs shell
commands and file operations. There are two clients: a windowed hub and a text CLI.

Three projects, one solution (`AgentVirtualHand.slnx`):

- `src/AgentVirtualHand` -> builds **`avh`** (host GUI, and `--headless` mode). Avalonia + Kestrel.
- `src/AgentVirtualHand.Hub` -> builds **`avh-hub`** (windowed client, list of machines).
- `src/AgentVirtualHand.Link` -> builds **`avh-link`** (text client for one machine).

`Server/LinkProtocol.cs`, `Services/PasswordGate.cs`, `ViewModels/LockViewModel.cs`,
`Services/AppPaths.cs` and `Styles/Theme.axaml` live in the host project and are linked into the
others via `<Compile Include>` / `<AvaloniaResource Include>`. Edit the original, not a copy.

## Build and release

```bash
dotnet build -c Release AgentVirtualHand.slnx    # build all three
python build.py                                  # standalone single-file exes -> publish/win-x64
python build.py --only host --clean              # one app; --rid linux-x64 for Linux
```

Always stop running processes before publishing (the exe locks its own file):
`Get-Process avh,avh-hub,avh-link | Stop-Process -Force`.

## How the pieces fit

- **Transport vs API.** Tailcat.Link is only transport. The host runs a Kestrel HTTP server on
  `127.0.0.1` on a **system-assigned port**; `LinkHost` forwards each link request to that
  loopback server. All real logic (exec, files, sessions) lives once, behind HTTP.
- **Multi-peer.** `TailcatLink.HostManyAsync` -> `ILinkHost`; several clients pair at once.
  `SessionManager` keeps one session per peer key: own token, own time window. Cutting one off
  does not touch the others.
- **Invite codes are single-use** (`InvitationRequest { SingleUse = true }`). Using one grants a
  session immediately (`PeerJoined` -> `SessionManager.Open`) and the code disappears from the UI.
- **Isolation in the hub.** Each connection = its own loopback port + own bearer token + own
  Tailcat pairing folder (`links/<id>`). `PromptBuilder` bakes one machine's URL and token into
  the model prompt, so a prompt for one machine cannot reach another.

## Conventions that matter

- **All user-facing text is English** (UI labels, hints, log messages, console output, HelpText,
  PromptBuilder, CLI usage). Keep it that way.
- **Do not rename internal identifiers** without cause: `AppName = "agentvirtualhand"` is the
  Tailcat pairing identity - changing it breaks every existing pairing. Namespaces and class
  names stay `AgentVirtualHand.*` even though the product shows as "AVH".
- **On-disk names are deliberately opaque** so they do not reveal the tool's purpose:
  `%APPDATA%\avh` and `%APPDATA%\avh-hub` (see `AppPaths`), `avh.link.json` (see
  `HiddenLinkStore`), `avh-blad.log`. Keep new artifacts equally neutral.
- **Time-dependent logic takes an injected `TimeProvider`** (see `LockViewModel`) so it can be
  unit-tested without waiting. Follow that pattern for anything with a clock.
- **State is in memory.** Sessions and tokens do not survive a host restart - that is intended.

## Testing

There is no test project; checks are throwaway console apps under the scratchpad that link the
real source files and assert with a small `Check` helper. When touching security or timing
(password gate, lock countdown, session expiry, folder migration), write such a check and run it
rather than relying on the GUI - GUI automation here cannot reliably type into a non-foreground
window. For end-to-end, run `avh --headless`, grab the printed code, and drive `avh-link` against it.

## Gotchas hit before

- Kestrel reads `appsettings.json` from the working directory as its own config; a stray one
  next to the exe added a phantom endpoint. `builder.Configuration.Sources.Clear()` prevents it.
- The Fluent theme draws its own focus border on `TextBox`; plain selectors lose to it. Text-box
  colors/thickness are set via `TextControl*` resource keys in `App.axaml`.
- Raw string literals (`$$"""..."""`) do **not** process `\\` - a doubled backslash stays doubled,
  which is exactly the correct JSON form in HelpText/PromptBuilder examples.
