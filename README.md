# AVH

An emergency "remote hand" for a machine: an Avalonia app (Windows + Linux) exposes a machine
over **Tailcat.Link**, and a paired client (for example Claude Code on another computer) can run
shell commands, install software, write and compile code, and manage files.

The connection does not use an IP address: there is no host, port, or firewall rule. The two
machines meet over the link, and the only thing that passes through a human is a single-use
invite code.

Access is **granted for a set time**, **expires on its own**, and is **fully visible** in the
app's log.

## How it works

1. On the controlled machine: **Start link**, pick **how long to let a machine in** (15 min, 1 h,
   4 h or 8 h), and click **Invite machine**. The app shows a code valid for 15 minutes.
2. Pass the code to where the client will run: `avh-link join <code>`, or paste it in the
   **avh-hub** window. The code lets **one** machine in and **disappears once used**.
3. The machine that used it works immediately - it appears in the list with a countdown. Passing
   the code is already the decision to let it in, so there is no separate confirmation.
4. When the time runs out, the machine loses access and needs a new code. Meanwhile you can
   **Extend** each machine (a menu picks how much time to add) or **Cut off** its access,
   **Delete** unpairs it for good, and **Cut off all** ends work for everyone.

Several machines can work at once, each with its own time window and its own token.

The first command brings the link up in the background (a dozen or so seconds), later ones run in
about 0.7 s. `avh-link down` closes the background link, `avh-link up` brings it back.

## Password lock

Both windows - the controlled machine and the hub - are password protected. First launch
**forces** you to set one; there is no default password, because anyone who downloaded the file
would know it. After **30 seconds** without mouse or keyboard the window content disappears and
returns only after the password is entered. A wrong attempt costs a **10-second** countdown
during which the button is disabled. A countdown in the status bar shows the time left before the
window locks. The lock can be turned off entirely with the **auto-lock** switch in the status bar;
with it off the window opens straight to its content and never locks, and the choice is remembered.

Only a PBKDF2-SHA256 hash (210k iterations, random salt) is kept, in `lock.json` next to the
app settings. A forgotten password cannot be recovered - delete that file and set a new one on
the next start.

## Security

What is in place:

- window locked by password after 30 s idle (can be turned off), with a 10 s penalty for a wrong attempt;
- the HTTP server is only an internal bus, listens **on 127.0.0.1 only** and on a
  system-assigned port - there is nothing to scan from the network, no port is exposed;
- transport between machines is set up by Tailcat.Link (encrypted, with its own node identity);
- single-use invite code valid for 15 minutes; **New code** invalidates the previous one;
- the code grants access only for the set time: once it passes, the machine can do nothing
  without another code, even though the pairing remains;
- the session token never leaves the controlled machine - the client neither knows nor forwards it;
- a hard session time limit; **Cut off** kills the session and every running process;
- closing the app window ends the session and stops the link;
- every command and file operation goes to the live log in the GUI, each with a one-line
  plain-language note from the client saying why it ran (the `X-AVH-Note` header).

What is **not** there, and what to keep in mind:

- a paired client has the full rights of the account the app runs under - there is no sandbox
  and no allow-list of commands;
- pairing is persistent (trust on first use): after the first `join` the other machine comes
  back without a code. Remove it with `avh-link forget` (or **Remove** in the hub) on the client
  side, and with the **Delete** action next to the machine name on the host side;
- the invite code is **single-use**: it lets one machine in and expires after use. Another
  machine needs a new code (**New code**);
- several clients can work on one machine at once and **see each other's changes** - it is the
  same machine, not separate sandboxes;
- this is an emergency tool. Turn it on when you need help, turn it off when the problem is solved.

## Running

```bash
dotnet run --project src/AgentVirtualHand          # controlled machine's GUI
dotnet build -c Release AgentVirtualHand.slnx      # build everything
```

Headless mode (server, SSH) - the invite code lands on the console, and once used the next one
is printed:

```bash
avh --headless --minutes 60
```

Standalone release (one file per app, no .NET installed on the target machine):

```bash
python build.py                     # win-x64 -> publish/win-x64
python build.py --rid linux-x64     # Linux release
python build.py --only hub --clean  # a single app, cleaning the output folder
```

The script builds three files: `avh` (controlled machine), `avh-hub` and `avh-link`
(the two kinds of client). The .NET runtime and native libraries are inside, so on the target
machine you only copy one file.

## Two kinds of client

You can connect to the controlled machine two ways - both speak the same protocol, so the host
sees no difference.

**avh-hub** - a windowed app with a list of computers. Each connection gets its **own port on
127.0.0.1 and its own token**, and "Copy prompt" builds the model's instructions for that one
machine only. That is the point: the model gets one computer's address and token and has no way
to reach the others. Each connection can be turned off without touching the rest, and "New token"
invalidates every prompt copied earlier for that machine.

```bash
avh-hub                                  # window with the list of computers
avh-hub --pair <code> --name labsvcn     # add a machine from the command line
```

The list and tokens live in `%APPDATA%\avh-hub\connections.json`, and each machine's pairing in
a separate `links/<id>` folder - that is where connection isolation comes from.

**avh-link** - a text client for a single machine, described below.

## Client commands

| Command | Description |
| --- | --- |
| `avh-link join <code>` | pair using a single-use code from the app window |
| `avh-link up` \| `down` | bring up or close the background link |
| `avh-link forget` | remove the pairing from this machine |
| `avh-link system` | host, user, OS, shell, drives |
| `avh-link session` \| `session end` | remaining time / finish work |
| `avh-link exec "<command>" [--cwd <path>] [--timeout <s>]` | shell command |
| `avh-link bg start "<command>"` | long operation in the background, returns `id` |
| `avh-link bg out <id> [--out-offset N] [--err-offset N]` | incremental output, `running`, `exitCode` |
| `avh-link bg stdin <id> "<text>"` \| `bg kill <id>` | process stdin / kill the process tree |
| `avh-link fs list \| read \| write \| download \| upload \| mkdir \| delete \| move` | file operations |
| `avh-link api` | remote machine's API reference |
| `avh-link --help` | the client's own usage |
| `--store <dir>` | different place for the pairing (or `AVH_LINK_STORE`) |

Example:

```bash
avh-link join tco2FwWCBNh-cOZl4meH0AA3DXgL1BNLQyisn3_T7hLFbOY5...
avh-link exec "winget install --id Git.Git -e --silent" --timeout 600
avh-link fs download C:/Work/log.txt ./log.txt
```

In Windows paths use `/` or doubled backslashes.

## Layout

```
src/AgentVirtualHand/
  Program.cs                 GUI start + --headless mode
  Server/SessionManager.cs   access window, token, session lifetime
  Server/RemoteHttpServer.cs Kestrel on 127.0.0.1 + all endpoints
  Server/LinkHost.cs         Tailcat.Link -> forwarding requests to loopback
  Server/HiddenLinkStore.cs  pairing state under a neutral file name
  Server/LinkProtocol.cs     envelope exchanged with the client (shared with avh-link)
  Server/ShellRunner.cs      running commands and background processes
  Server/HelpText.cs         reference returned by /api/help
  Services/                  settings, app-data paths, password lock

src/AgentVirtualHand.Link/
  Program.cs                 avh-link client: commands -> envelopes
  LinkDaemon.cs              background link + named pipe for commands

src/AgentVirtualHand.Hub/
  Services/LinkConnection.cs one connection: link + own port and token on 127.0.0.1
  Services/ConnectionStore.cs list of machines, tokens, pairing folders
  Services/PromptBuilder.cs  the model's prompt - for one machine
  ViewModels/HubViewModel.cs connection list: add, turn on/off, remove
  ViewModels/MainViewModel.cs app state and GUI logic
  Views/MainWindow.axaml     interface
```
