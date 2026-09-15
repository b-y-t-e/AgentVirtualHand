using AgentVirtualHand.Server;

namespace AgentVirtualHand.Hub.Services;

/// <summary>
/// Buduje instrukcję dla modelu na drugiej stronie. Prompt dotyczy jednej maszyny:
/// zawiera adres i token wyłącznie tego połączenia, więc nie otwiera drogi do pozostałych.
/// </summary>
public static class PromptBuilder
{
    public static string ForConnection(LinkConnection connection) => $$"""
        Shell + files on machine "{{connection.Entry.Name}}" - the only machine you can reach.
        You act as its owner; confirm destructive actions. Paths: use / or double every backslash ("C:/Temp" or "C:\\Temp").
        All calls: {{connection.BaseUrl}}<path> -H "Authorization: Bearer {{connection.Token}}"
        On EVERY call also add -H "X-AVH-Note: <one short sentence, plain language, why you run this>" - the owner reads it in the machine's log.
        system  GET /api/system  |  session  GET /api/session  |  end  POST /api/session/end  |  full ref  GET /api/help
        exec    POST /api/exec {"command"|"script"|"scriptBase64","shell"?,"cwd","timeoutSeconds":{{ExecLimits.MaxSyncExecSeconds}}} -> exitCode/stdout/stderr/timedOut ; killed after {{ExecLimits.MaxSyncExecSeconds}} s max, longer work use long
        script  multi-line body in "script" (lines joined by \n) or "scriptBase64" (UTF-8 body as base64, no JSON escaping)
        long    POST /api/exec/start (same fields) -> {"id"} ; GET /api/exec/<id>?outOffset=N&errOffset=N&wait={{ExecLimits.MaxResponseHoldSeconds}} (holds until exit or {{ExecLimits.MaxResponseHoldSeconds}} s; repeat with new offsets while running) ;POST /api/exec/<id>/stdin (text) ; POST /api/exec/<id>/kill
        files   GET /api/fs/list|read|download ?path=  ;  POST /api/fs/write {"path","content"|"contentBase64","append"?} ; /upload?path= (bytes) ; /mkdir ; /delete {"path","recursive"?} ; /move {"from","to"}
        502 = link briefly down, retry. 401 = access closed by owner, ask to reopen.
        """;
}
