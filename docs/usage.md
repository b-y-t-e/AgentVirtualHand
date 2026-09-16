# Usage

## Programs

| Program | Where it runs |
| --- | --- |
| `avh` | the machine being controlled (window, or `--headless` on a server) |
| `avh-hub` | the helping machine - a window with a list of machines |
| `avh-link` | the helping machine - a command-line client for one machine |

Each is a single file with the .NET runtime inside - copy it and run it.

## The avh window

- **Start link** / **Stop link** - turn the machine on or off for remote access.
  **Stop link** also kills every process started by the helpers.
- **15 min / 1 h / 4 h / 8 h** - how long an invited machine gets access.
- **Invite machine** - creates a code. **Copy code** copies it, **New code** cancels the old one.

For each machine in the list:

| Button | What it does |
| --- | --- |
| **Extend** | add more time |
| **Let in** | give a paired machine access again, without a new code |
| **Cut off** | end this machine's access (other machines keep working) |
| **Delete** | unpair it for good - it needs a new code to come back |
| **Cut off all** | end access for everyone and kill all their processes |

## Headless mode

On a server or over SSH, without a desktop:

```bash
avh --headless --minutes 60
```

The code is printed to the console. Every machine that joins gets access at once, and the next
code is printed. **Ctrl+C** cuts everyone off.

## avh-link

| Command | Description |
| --- | --- |
| `avh-link join <code>` | pair using the invite code |
| `avh-link up` \| `down` | start or stop the background link |
| `avh-link forget` | remove the pairing from this machine |
| `avh-link system` | host, user, OS, shell, drives |
| `avh-link session` \| `session end` | time left / end your access |
| `avh-link exec "<command>"` | run a command (max 22 s) |
| `avh-link exec --script-file <path>` | run a local multi-line script as-is |
| `avh-link bg start "<command>"` \| `--script-file <path>` | long job in the background, returns `id` |
| `avh-link bg out <id> [--wait <s>]` | job output; `--wait` (1-25 s) waits for the job to finish |
| `avh-link bg stdin <id> "<text>"` \| `bg kill <id>` | send input / kill the job |
| `avh-link fs list \| read \| write \| download \| upload \| mkdir \| delete \| move` | files |
| `avh-link api` | full API reference of the remote machine |
| `avh-link --help` | all options (`--shell`, `--cwd`, `--timeout`, `--store`, ...) |

```bash
avh-link join tco2FwWCBNh-cOZl4meH0AA3DXgL1BNLQyisn3_T7hLFbOY5...
avh-link bg start "winget install --id Git.Git -e --silent"
avh-link bg out <id> --wait 25
avh-link fs download C:/Work/log.txt ./log.txt
```

Good to know:

- The first command starts the link in the background (10-20 s); later ones take about 0.7 s.
- A plain `exec` is stopped after **22 s**. Installs and builds go through `bg start` + `bg out --wait`.
- On Windows, write paths with `/` or double every backslash.
- `avh-link` keeps files in memory - use `avh-hub` for large files.

## avh-hub

- **Add computer** - paste an invite code.
- **Copy prompt** - instructions for an AI model with this machine's local address and token.
  Each machine has its own address and token, so the model cannot reach other machines.
- **Turn on / off** - per machine. **New token** invalidates prompts copied earlier.
- **Remove computer** - forget the machine and its pairing.

```bash
avh-hub                                  # the window
avh-hub --pair <code> --name labsvcn     # add a machine from the command line
```

Uploads and downloads can be any size, continue after a short link drop, and a broken upload
never overwrites the target file.

Data lives in `%APPDATA%\avh-hub`: `connections.json` (machines and tokens) and `links/<id>`
(pairing of each machine).

## Password lock

Both windows ask for a password on first start - there is no default one.

- After **30 s** without mouse or keyboard the window hides its content until the password is typed.
- A wrong password blocks the button for **10 s**.
- The **auto-lock** switch in the status bar turns the lock off.
- Forgot the password? Delete `lock.json` and set a new one.

Only a PBKDF2-SHA256 hash is stored. The lock hides the window; it does not protect the files.
