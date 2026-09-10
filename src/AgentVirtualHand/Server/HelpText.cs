namespace AgentVirtualHand.Server;

/// <summary>
/// Dokumentacja API HTTP zwracana pod /api/help. Czyta ją model pracujacy przez aplikacje Hub;
/// klient avh-link opakowuje te same endpointy we wlasne polecenia (avh-link --help).
/// </summary>
public static class HelpText
{
    public static string Markdown() => """
        # AgentVirtualHand - zdalne sterowanie maszyną przez Tailcat.Link

        Ruch idzie linkiem Tailcata, ale API jest zwykłym HTTP wystawionym lokalnie przez
        aplikację, przez którą się łączysz. Adres i token dostajesz w promptcie - prowadzą
        wyłącznie do tej jednej maszyny.

        Dostęp wygasa o godzinie ustawionej przez właściciela maszyny. Po wygaśnięciu każde
        żądanie kończy się `401` i trzeba poprosić o ponowne otwarcie dostępu w jego oknie.
        `502` oznacza chwilowo zerwany link, nie brak uprawnień.

        Na jednej maszynie może pracować kilku klientów naraz, każdy z własnym oknem dostępu.
        `GET /api/session` pokazuje w polu `otherMachinesConnected`, ilu jeszcze pracuje obok -
        licz się z tym, że ktoś może w tym czasie zmieniać te same pliki.

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
        - długie operacje puszczaj przez `/api/exec/start`, nie przez zwykły `exec`;
        - operacje destrukcyjne (kasowanie, nadpisywanie, instalacje) potwierdź z właścicielem maszyny.

        """;
}
