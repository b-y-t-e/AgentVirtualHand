# AVH

An emergency "remote hand" for a machine. You run **`avh`** on the machine that needs help, and a
paired client (for example Claude Code on another computer) can run shell commands, install
software, and manage files there.

The machines connect over **Tailcat.Link**: no IP address, no open port, no firewall rule. The
only thing a person passes between them is a single-use invite code. Access is **granted for a
set time** and **expires on its own**.

> **Warning.** A connected client has the full rights of the account `avh` runs under. There is
> no sandbox and no command allow-list. Only let in machines you trust, and turn AVH off when you
> are done.

## Quick start

```bash
# 1. on the machine that needs help
avh                          # click Start link -> Invite machine -> Copy code

# 2. on the helping machine
avh-link join <code>
avh-link exec "hostname"
```

## How it works

1. On the controlled machine: **Start link**, pick **how long to let a machine in** (15 min, 1 h,
   4 h or 8 h), and click **Invite machine**. The code is valid for 15 minutes.
2. Use the code on the client: `avh-link join <code>`, or paste it in the **avh-hub** window.
   The code lets **one** machine in and **disappears once used**.
3. That machine can work right away. It shows up in the list with a countdown.
4. When the time runs out, the machine loses access. In the list you can:
   - **Extend** - add more time;
   - **Let in** - give an already paired machine access again, without a new code;
   - **Cut off** - end that machine's access; other machines keep working;
   - **Delete** - unpair it for good; it needs a new code to come back;
   - **Cut off all** - end access for everyone and kill all running processes.

Several machines can work at once, each with its own time window and its own token.

## Installation

Download or build three single-file programs (the .NET runtime is included):

| Program | Where it runs |
| --- | --- |
| `avh` | the machine being controlled (GUI, or `--headless` on a server) |
| `avh-hub` | the helping machine - a window with a list of machines |
| `avh-link` | the helping machine - a command-line client for one machine |

Build from source (needs the .NET 10 SDK and Python 3):

```bash
python build.py                     # win-x64 -> publish/win-x64
python build.py --rid linux-x64     # Linux
python build.py --only hub --clean  # a single app, cleaning the output folder first
```

For development: `dotnet build -c Release AgentVirtualHand.slnx`.

### Headless mode

On a server or over SSH, without a desktop:

```bash
avh --headless --minutes 60
```

The invite code is printed to the console. Every machine that joins gets access at once, and the
next code is printed. **Ctrl+C** cuts everyone off.

## Password lock

Both windows (`avh` and `avh-hub`) ask you to set a password on first start - there is no default
one. After **30 s** without mouse or keyboard the window hides its content until you type the
password. A wrong password blocks the button for **10 s**. The **auto-lock** switch in the status
bar turns the lock off.

Only a PBKDF2-SHA256 hash is stored, in `lock.json`. Forgot the password? Delete that file and set
a new one. The lock only hides the window - it does not stop someone who can reach the files.

## Clients

### avh-link (command line)

| Command | Description |
| --- | --- |
| `avh-link join <code>` | pair using the invite code |
| `avh-link up` \| `down` | start or stop the background link |
| `avh-link forget` | remove the pairing from this machine |
| `avh-link system` | host, user, OS, shell, drives |
| `avh-link session` \| `session end` | time left / end your access |
| `avh-link exec "<command>"` | run a command (max 22 s) |
| `avh-link exec --script-file <path>` | run a local multi-line script as-is, no escaping |
| `avh-link bg start "<command>"` \| `--script-file <path>` | long job in the background, returns `id` |
| `avh-link bg out <id> [--wait <s>]` | job output; `--wait` (1-25 s) waits for the job to finish |
| `avh-link bg stdin <id> "<text>"` \| `bg kill <id>` | send input / kill the job with its child processes |
| `avh-link fs list \| read \| write \| download \| upload \| mkdir \| delete \| move` | file operations |
| `avh-link api` | full API reference of the remote machine |
| `avh-link --help` | all options (`--shell`, `--cwd`, `--timeout`, `--store`, ...) |

The first command starts the link in the background (10-20 s); later commands take about 0.7 s.

```bash
avh-link join tco2FwWCBNh-cOZl4meH0AA3DXgL1BNLQyisn3_T7hLFbOY5...
avh-link bg start "winget install --id Git.Git -e --silent"
avh-link bg out <id> --wait 25
avh-link fs download C:/Work/log.txt ./log.txt
```

On Windows, write paths with `/` or double every backslash.

A plain `exec` is killed after **22 s**, because a single request over the link must come back
within about 30 s. Installs, builds, and anything slow go through `bg start` + `bg out --wait`.

`avh-link` keeps file transfers in memory - use `avh-hub` for large files.

### avh-hub (window)

A list of machines (**Add computer** takes an invite code). For each one, **Copy prompt** gives a
ready instruction for an AI model with that machine's local address (`http://127.0.0.1:<port>`)
and token. Each machine has its own port and token, so a model given one prompt cannot reach the
other machines. **New token** invalidates prompts copied earlier.

```bash
avh-hub                                  # the window
avh-hub --pair <code> --name labsvcn     # add a machine from the command line
```

The hub streams uploads and downloads of **any size**. Files never have to fit in memory, a
transfer resumes after a short link drop, and a half-finished upload never overwrites the target
file.

The list of machines and their tokens is kept in `%APPDATA%\avh-hub\connections.json`; each
machine's pairing is in its own `links/<id>` folder.

## Security

What protects you:

- the code is single-use and valid for 15 minutes; **New code** cancels the previous one;
- access ends when the chosen time runs out; after that the machine can do nothing until you let
  it in again, even though it stays paired;
- the HTTP API listens **only on 127.0.0.1**, on a random port - nothing is exposed to the network;
- the link between the machines is encrypted by Tailcat.Link;
- the session token never leaves the controlled machine;
- a file transfer stops as soon as that machine's access ends;
- closing the `avh` window cuts everyone off and stops the link;
- commands, scripts, file writes, uploads, deletes and moves are shown in the log, together with a
  short note from the client explaining why (`X-AVH-Note`).

What to keep in mind:

- **full rights, no sandbox** - a client can do anything the account can, including leaving
  something running or installed after its access ends;
- **Cut off** for a single machine ends its access but does **not** stop processes it already
  started. **Cut off all** or **Stop link** kills them;
- the log is not a complete record: file reads, listings, `mkdir` and text sent to a job's stdin
  are not shown, and the explanation note is written by the client itself;
- **pairing stays** until you **Delete** the machine on the host, or run `avh-link forget`
  (**Remove computer** in the hub) on the client;
- machines working at the same time see each other's changes and background jobs;
- the hub keeps tokens in plain text in `connections.json`, and **Copy prompt** puts the token
  into the AI model's context;
- this is an emergency tool: turn it on when you need help, and off when the problem is solved.

## Project layout

```
src/AgentVirtualHand/              avh - the controlled machine
  Program.cs                       GUI start + --headless mode
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
