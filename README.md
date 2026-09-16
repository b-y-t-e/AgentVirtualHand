# AVH

Emergency remote access to a computer. Someone (or an AI model like Claude Code) on another
machine can run commands and manage files on your machine - for a time you choose.

> **Warning:** a connected machine can do everything your user account can. Only let in machines
> you trust, and turn AVH off when you are done.

## How it works

```
 your machine                                    helping machine
┌────────────┐     encrypted Tailcat.Link      ┌──────────────────────┐
│    avh     │ <─────────────────────────────> │ avh-hub or avh-link  │
└────────────┘   no IP, no open port           └──────────────────────┘
```

You start `avh` and create a one-time code. The helping machine uses the code to connect. From
then on it sends commands and `avh` runs them, until the time runs out.

## Step by step

1. **Start** - run `avh`, click **Start link**.
2. **Choose time** - 15 min, 1 h, 4 h or 8 h.
3. **Invite** - click **Invite machine** and send the code to the helper (valid 15 min, works once).
4. **Connect** - the helper runs `avh-link join <code>` or pastes the code in `avh-hub`.
5. **Work** - the machine appears in your list with a countdown; every command shows in the log.
6. **Finish** - access ends by itself, or click **Cut off** / **Cut off all**. Close `avh` when done.

## Which client?

- **avh-hub** - a window with a list of machines. **Copy prompt** gives an AI model everything it
  needs to work on one machine. Best for large files.
- **avh-link** - a command-line client, for a terminal or scripts:

  ```bash
  avh-link join <code>
  avh-link exec "hostname"
  ```

## Security in short

- The code works once and expires after 15 minutes. Access expires at the chosen time.
- Nothing listens on the network - there is no port to scan.
- No sandbox: the helper has your account's rights and can leave things behind.
- The log does not show everything (for example file reads).

## More

- [Usage](docs/usage.md) - all commands, headless mode, password lock
- [Security](SECURITY.md) - what is protected and what is not
- [Development](docs/development.md) - building and project layout
