namespace AgentVirtualHand.Server;

/// <summary>Instrukcja API zwracana pod /api/help - to samo trafia do schowka przy parowaniu.</summary>
public static class HelpText
{
    public static string Markdown() => """
        # AgentVirtualHand - zdalne sterowanie maszyną

        Każde żądanie (poza `/api/pair` i `/api/status`) wymaga nagłówka:

            Authorization: Bearer <TOKEN>

        Token wygasa o godzinie ustawionej przy parowaniu. Po wygaśnięciu wszystkie
        endpointy zwracają 401 i trzeba poprosic użytkownika o ponowne parowanie w GUI.

        ## Sesja
        - `GET  /api/session`      - ile czasu zostało, kto jest połączony
        - `POST /api/session/end`  - dobrowolne zakończenie sesji (kulturalne wyjście)
        - `GET  /api/system`       - host, user, OS, dyski, katalog roboczy

        ## Uruchamianie poleceń
        `POST /api/exec`
        ```json
        { "command": "dotnet --info", "shell": "powershell", "cwd": "C:\\\\Work", "timeoutSeconds": 120 }
        ```
        Odpowiedz: `exitCode`, `stdout`, `stderr`, `timedOut`, `durationMs`.
        `shell`: `powershell` | `pwsh` | `cmd` | `bash` | `sh` (domyślnie zależy od systemu).

        Długie operacje (instalacje, kompilacje, uruchomione serwisy):
        - `POST /api/exec/start` - te same pola, zwraca `{ "id": "..." }`
        - `GET  /api/exec/{id}?outOffset=0&errOffset=0` - przyrostowy output; przekaż zwrócone
          `outOffset`/`errOffset` w kolejnym zapytaniu, `running` mówi czy proces jeszcze żyje
        - `POST /api/exec/{id}/stdin` - body = tekst wysyłany na stdin procesu
        - `POST /api/exec/{id}/kill` - ubicie procesu wraz z drzewem potomnym

        ## Pliki
        - `GET  /api/fs/list?path=C:\Work`
        - `GET  /api/fs/read?path=...&maxBytes=1000000`
        - `GET  /api/fs/download?path=...` - surowe bajty
        - `POST /api/fs/write` - `{ "path": "...", "content": "..." }` lub `contentBase64`, opcjonalnie `append`
        - `POST /api/fs/upload?path=...` - body = surowe bajty pliku
        - `POST /api/fs/mkdir` - `{ "path": "..." }`
        - `POST /api/fs/delete` - `{ "path": "...", "recursive": true }`
        - `POST /api/fs/move` - `{ "from": "...", "to": "..." }`

        ## Przyklady curl
        ```bash
        curl -s -H "Authorization: Bearer $TOKEN" $BASE/api/system
        curl -s -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
             -d '{"command":"Get-ChildItem C:\\Work | Select-Object -First 10"}' $BASE/api/exec
        ```

        ## Dobre praktyki
        1. Zacznij od `GET /api/system`, żeby poznać system, powłokę i katalogi.
        2. Długie instalacje/kompilacje odpalaj przez `/api/exec/start` i odpytuj output,
           zamiast blokować się na `/api/exec` z dlugim timeoutem.
        3. Przed nadpisaniem pliku przeczytaj go (`/api/fs/read`).
        4. Pilnuj czasu z `GET /api/session` - po wygaśnięciu operacje w toku przepadają.
        5. Wszystkie polecenia są logowane w GUI na zdalnej maszynie i są widoczne dla właściciela.
        """;
}
