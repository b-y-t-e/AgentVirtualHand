namespace AgentVirtualHand.Hub.Services;

/// <summary>
/// Buduje instrukcję dla modelu na drugiej stronie. Prompt dotyczy jednej maszyny:
/// zawiera adres i token wyłącznie tego połączenia, więc nie otwiera drogi do pozostałych.
/// </summary>
public static class PromptBuilder
{
    public static string ForConnection(LinkConnection connection) => $$"""
        AVH {{connection.BaseUrl}} = zdalna powloka+pliki na maszynie "{{connection.Entry.Name}}".
        To jedyna maszyna, do ktorej masz dostep - ten adres i token nie prowadza do zadnej innej.
        Dzialasz na koncie wlasciciela tamtej maszyny; operacje destrukcyjne najpierw potwierdz.
        Do kazdego zadania dodaj naglowek: -H "Authorization: Bearer {{connection.Token}}"
        API (prefix {{connection.BaseUrl}}): GET /api/help (pelna instrukcja) | GET /api/system (os, shell, dyski, home) | GET /api/session (pozostaly czas)
        EXEC: POST /api/exec {"command":"...","cwd":"...","timeoutSeconds":120} -> exitCode/stdout/stderr/timedOut
        BG: POST /api/exec/start (te same pola) -> {"id"} | GET /api/exec/<id>?outOffset=N&errOffset=N (output przyrostowo; running, exitCode) | POST /api/exec/<id>/stdin (body=tekst) | POST /api/exec/<id>/kill
        FS: GET /api/fs/list?path= | GET /api/fs/read?path=&maxBytes= | GET /api/fs/download?path= | POST /api/fs/write {"path","content"|"contentBase64","append"?} | POST /api/fs/upload?path= (body=bajty) | POST /api/fs/mkdir {"path"} | POST /api/fs/delete {"path","recursive"?} | POST /api/fs/move {"from","to"}
        NOTY: sciezki Windows w JSON z podwojnym backslashem lub /; dlugie operacje (instalacje, kompilacje) przez BG;
        polaczenie idzie przez lokalny port aplikacji AgentVirtualHand Hub - jesli dostaniesz 502, link jest chwilowo zerwany,
        a 401 oznacza zamkniety dostep po stronie wlasciciela (popros o ponowne otwarcie); koniec pracy: POST /api/session/end.
        """;
}
