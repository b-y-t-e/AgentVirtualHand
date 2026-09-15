namespace AgentVirtualHand.Server;

/// <summary>
/// HTTP API reference returned from /api/help. The model working through the Hub reads it;
/// the avh-link client wraps the same endpoints in its own commands (avh-link --help).
/// </summary>
public static class HelpText
{
    public static string Markdown() => $$"""
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
        { "command": "dotnet --info", "shell": "powershell", "cwd": "C:\\\\Work", "timeoutSeconds": {{ExecLimits.MaxSyncExecSeconds}} }
        ```
        Response: `exitCode`, `stdout`, `stderr`, `timedOut`, `durationMs`.
        `shell`: `powershell` | `pwsh` | `cmd` | `bash` | `sh` (defaults to the OS).

        A plain `exec` must finish within {{ExecLimits.MaxSyncExecSeconds}} s: the link's request round-trip is capped, so
        `timeoutSeconds` is clamped to 1-{{ExecLimits.MaxSyncExecSeconds}} (default {{ExecLimits.MaxSyncExecSeconds}}) and a slower command is killed with
        `timedOut: true`. Anything that may run longer than that -
        installs, builds, restores, long scripts - MUST go through `/api/exec/start` (below).

        Multi-line scripts: instead of `command`, send the whole body as `script` (lines separated
        by `\n`). It is saved to a temp file and run as one script, so no nested shell quoting:
        ```json
        { "script": "Get-ChildItem C:\\Temp | Format-Table\nWrite-Host done", "shell": "powershell" }
        ```
        `command` and `script` are both ordinary JSON strings: every backslash must still be doubled
        (`SERVER\\INSTANCE`, `C:\\Temp`, or use `/`), and `"` must be written as `\"`. A single `\`
        breaks the JSON and you get a parse error.
        To avoid JSON escaping entirely, send `scriptBase64` - the UTF-8 script body encoded as base64.
        UTF-16/UTF-32 bodies need a BOM; other bytes (e.g. ANSI code pages) are rejected with 400.

        Long operations (installs, builds, running services):
        - `POST /api/exec/start` - same fields (`command` or `script`), returns `{ "id": "..." }`
        - `GET  /api/exec/{id}?outOffset=0&errOffset=0&wait={{ExecLimits.MaxResponseHoldSeconds}}` - incremental output; pass the returned
          `outOffset`/`errOffset` on the next call, `running` tells whether the process is alive.
          `wait=N` (1-{{ExecLimits.MaxResponseHoldSeconds}} s) holds the response until the process exits or N seconds pass, so you poll
          far less - just call again with the new offsets while `running` is true.
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
