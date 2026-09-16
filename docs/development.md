# Development

## Build

Needs the .NET 10 SDK; release builds also need Python 3.

```bash
dotnet build -c Release AgentVirtualHand.slnx   # build all three apps
dotnet run --project src/AgentVirtualHand       # run the host window

python build.py                     # single-file release, win-x64 -> publish/win-x64
python build.py --rid linux-x64     # Linux
python build.py --only hub --clean  # one app, cleaning the output folder first
```

On Windows the output also has `msquic.dll` (copied by the Tailcat.Link package). Ship it next to
the exe - without it QUIC is missing and every connection silently goes through the relay.

Stop running copies before publishing - the exe locks its own file:
`Get-Process avh,avh-hub,avh-link | Stop-Process -Force`.

## Project layout

```
src/AgentVirtualHand/              avh - the controlled machine
  Program.cs                       window start + --headless mode
  Server/SessionManager.cs         access window and token per machine
  Server/LinkHost.cs               Tailcat.Link handler: session check, forwarding to loopback
  Server/StreamedFileTransfers.cs  uploads/downloads straight to/from disk
  Server/RemoteHttpServer.cs       Kestrel on 127.0.0.1, all endpoints
  Server/ShellRunner.cs            commands and background jobs
  Server/ShellDialect.cs           powershell / pwsh / cmd / bash / sh
  Server/LinkProtocol.cs           message format shared with both clients
  Server/HelpText.cs               reference returned by /api/help
  ViewModels/, Views/              the window
  Services/                        settings, data paths, password lock

src/AgentVirtualHand.Link/         avh-link
  Program.cs                       commands -> requests
  LinkDaemon.cs                    background link + named pipe

src/AgentVirtualHand.Hub/          avh-hub
  Services/LinkConnection.cs       one machine: link + local port and token
  Services/ConnectionStore.cs      saved machines and tokens
  Services/PromptBuilder.cs        prompt for one machine
  ViewModels/HubViewModel.cs       machine list: add, turn on/off, remove
```

Architecture notes and rules for changing the code are in [CLAUDE.md](../CLAUDE.md).
