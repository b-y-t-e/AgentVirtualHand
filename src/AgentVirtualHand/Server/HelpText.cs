namespace AgentVirtualHand.Server;

/// <summary>
/// HTTP API reference returned from /api/help. The model working through the Hub reads it;
/// the avh-link client wraps the same endpoints in its own commands (avh-link --help).
/// </summary>
public static class HelpText
{
    public static string Markdown() => """
        # AVH - remote control of a machine over Tailcat.Link

        Traffic goes over the Tailcat link, but the API is plain HTTP exposed locally by the
        app you connect through. The address and token come in the prompt - they lead only to
        this one machine.

        Access expires at the time set by the machine owner. After that every request returns
        `401` and you have to ask the owner to grant access again in their window.
        `502` means the link is briefly down, not that you lack permission.

        On EVERY request add a header `X-AVH-Note: <one short sentence>` in plain language saying
        why you run it. It shows up in the owner's live log next to the raw command, so they can
        follow what you are doing. Keep it to a single readable sentence.

        Several clients can work on one machine at once, each with its own access window.
        `GET /api/session` reports how many others are connected in `otherMachinesConnected` -
        expect that someone may be changing the same files at the same time.

        ## Session
        - `GET  /api/session`      - time left, who is connected
        - `POST /api/session/end`  - finish the session voluntarily (clean exit)
        - `GET  /api/system`       - host, user, OS, drives, working directory

        ## Running commands
        `POST /api/exec`
        ```json
        { "command": "dotnet --info", "shell": "powershell", "cwd": "C:\\\\Work", "timeoutSeconds": 120 }
        ```
        Response: `exitCode`, `stdout`, `stderr`, `timedOut`, `durationMs`.
        `shell`: `powershell` | `pwsh` | `cmd` | `bash` | `sh` (defaults to the OS).

        IMPORTANT: `command` is a JSON string, so every backslash must be doubled.
        Instance name `SERVER\INSTANCE`, path `C:\Temp` - in JSON write `SERVER\\INSTANCE`
        and `C:\\Temp`, or use `/`. A single `\` breaks the JSON and you get a parse error.

        Long operations (installs, builds, running services):
        - `POST /api/exec/start` - same fields, returns `{ "id": "..." }`
        - `GET  /api/exec/{id}?outOffset=0&errOffset=0` - incremental output; pass the returned
          `outOffset`/`errOffset` on the next call, `running` tells whether the process is alive
        - `POST /api/exec/{id}/stdin` - body = text sent to the process stdin
        - `POST /api/exec/{id}/kill` - kill the process with its child tree

        ## Files
        - `GET  /api/fs/list?path=C:/Work`
        - `GET  /api/fs/read?path=...&maxBytes=1000000`
        - `GET  /api/fs/download?path=...` - raw bytes
        - `POST /api/fs/write` - `{ "path": "...", "content": "..." }` or `contentBase64`, optional `append`
        - `POST /api/fs/upload?path=...` - body = raw file bytes
        - `POST /api/fs/mkdir` - `{ "path": "..." }`
        - `POST /api/fs/delete` - `{ "path": "...", "recursive": true }`
        - `POST /api/fs/move` - `{ "from": "...", "to": "..." }`

        ## Notes
        - write Windows paths with a `/` or a doubled backslash;
        - run long operations through `/api/exec/start`, not plain `exec`;
        - confirm destructive operations (delete, overwrite, install) with the machine owner.

        """;
}
