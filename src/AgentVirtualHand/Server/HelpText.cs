namespace AgentVirtualHand.Server;

/// <summary>Instrukcja zwracana przez "avh-link help" - to samo trafia do schowka.</summary>
public static class HelpText
{
    public static string Markdown() => """
        # AgentVirtualHand - zdalne sterowanie maszyną przez Tailcat.Link

        Połączenie idzie linkiem, nie po adresie IP: nie ma hosta, portu ani tokenu do przekazywania.
        Kod zaproszenia podaje się raz (`avh-link join <kod>`), potem sparowanie jest pamiętane.

        Dostęp wygasa o godzinie ustawionej przez właściciela maszyny. Po wygaśnięciu każde
        polecenie kończy się `HTTP 401` i trzeba poprosić o ponowne otwarcie dostępu w oknie aplikacji.

        Pierwsze polecenie zestawia link w tle (kilkanaście sekund), kolejne idą już od razu.

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

        ## Uwagi
        - ścieżki Windows pisz z ukośnikiem `/` albo podwójnym backslashem;
        - link trzyma się w tle; `avh-link down` go zamyka, `avh-link up` podnosi z powrotem;
        - operacje destrukcyjne (kasowanie, nadpisywanie, instalacje) potwierdź z właścicielem maszyny.

        """;
}
