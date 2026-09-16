# Security

AVH gives another machine control over yours. Read this before using it.

## What protects you

- **One-time code** - valid 15 minutes, lets in one machine. **New code** cancels the old one.
- **Time limit** - access ends at the chosen time. After that the machine can do nothing until you
  let it in again, even though it stays paired.
- **No network exposure** - the internal HTTP API listens only on `127.0.0.1`, on a random port.
- **Encrypted link** - the connection between machines is set up and encrypted by Tailcat.Link.
- **Token stays home** - the session token never leaves the controlled machine.
- **Transfers stop with access** - a file upload or download stops as soon as access ends.
- **Closing `avh`** cuts everyone off, kills their processes and stops the link.
- **Log** - commands, scripts, file writes, uploads, deletes and moves are shown, with a short note
  from the client explaining why.

## What does not protect you

- **No sandbox.** The helper has the full rights of the account `avh` runs under. It can install
  something or start a process that keeps running after its access ends.
- **Cut off for one machine does not kill its processes.** Use **Cut off all** or **Stop link**.
- **The log is incomplete.** File reads, directory listings, `mkdir` and text sent to a job's stdin
  are not shown. The explanation note is written by the client, not verified.
- **Pairing is remembered.** Remove it with **Delete** on the host, or `avh-link forget` /
  **Remove computer** on the client.
- **Machines share one computer.** Machines working at the same time see each other's changes and
  background jobs.
- **Hub tokens in plain text.** `avh-hub` keeps tokens in `connections.json`, and **Copy prompt**
  puts a token into the AI model's context.
- **The password lock only hides the window.** Anyone with access to the files can remove it.

## Recommendations

- Turn AVH on only when you need help, and off when the problem is solved.
- Choose the shortest time that is enough.
- Watch the log while someone is working.
- After the session, check for new scheduled tasks, services and startup entries.
