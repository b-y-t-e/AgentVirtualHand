# AgentVirtualHand

Awaryjna "zdalna reka" na maszynie: aplikacja Avalonia (Windows + Linux) stawia lokalny serwer HTTP,
przez ktory sparowany klient (np. Claude Code na drugim komputerze) moze wykonywac polecenia powloki,
instalowac oprogramowanie, pisac i kompilowac kod, zarzadzac plikami i uslugami.

Dostep jest **jednorazowo parowany**, **czasowy** i **w pelni widoczny** w logu aplikacji.

## Jak to dziala

1. Uruchamiasz aplikacje na maszynie, ktora ma byc sterowana, i klikasz **Uruchom serwer**.
2. Klikasz **Paruj** - aplikacja generuje jednorazowy kod (wazny 5 minut), pokazuje adres IP,
   port i kod QR.
3. **Kopiuj dane + instrukcje** wrzuca do schowka gotowy blok tekstu z adresem, kodem i opisem API.
   Wklejasz go w Claude Code na drugim komputerze.
4. Klient paruje sie (`POST /api/pair`) i dostaje token. W tej samej chwili **okno parowania zamyka
   sie na stale** - nikt inny juz sie nie podlaczy.
5. Token dziala przez czas ustawiony suwakiem w GUI (5 min - 8 h). Po uplywie czasu kazde zadanie
   dostaje 401.

Kod QR prowadzi do strony `/pair?code=...` - mozna sparowac sie tez z telefonu i przeczytac token.

## Bezpieczenstwo

Co jest zrobione:

- kod parowania jednorazowy, wazny 5 minut, z alfabetu bez mylacych znakow;
- 5 blednych prob zamyka parowanie;
- porownania kodu i tokenu odporne na atak czasowy (`CryptographicOperations.FixedTimeEquals`);
- token 256-bitowy, trzymany wylacznie w pamieci - restart aplikacji odcina dostep;
- twardy limit czasu sesji, przycisk **Odetnij dostep** ubija sesje i wszystkie uruchomione procesy;
- zamkniecie okna aplikacji konczy sesje i zatrzymuje serwer;
- kazde polecenie i operacja na plikach trafia do logu na zywo w GUI;
- przelacznik widocznosci: tylko `127.0.0.1` albo cala siec lokalna.

Czego **nie** ma i o czym trzeba pamietac:

- transport to czyste HTTP, bez TLS - uzywaj tylko w zaufanej sieci lokalnej albo przez tunel
  (`ssh -L 8787:127.0.0.1:8787 user@host`) z opcja "tylko 127.0.0.1";
- sparowany klient ma pelne uprawnienia konta, na ktorym dziala aplikacja - nie ma piaskownicy
  ani listy dozwolonych polecen;
- nie wystawiaj tego portu na publiczny internet ani nie zostawiaj aplikacji uruchomionej "na wszelki wypadek".

To narzedzie awaryjne. Wlaczasz je, gdy potrzebujesz pomocy, i wylaczasz, gdy problem jest rozwiazany.

## Uruchomienie

```bash
dotnet run --project src/AgentVirtualHand          # GUI
dotnet build -c Release src/AgentVirtualHand       # kompilacja
```

Tryb bez pulpitu (serwer, SSH) - kod parowania ladzie na konsoli:

```bash
AgentVirtualHand --headless --port 8787 --minutes 60        # widoczny w LAN
AgentVirtualHand --headless --local                          # tylko 127.0.0.1
```

Wydanie samodzielne (jeden plik, bez zainstalowanego .NET na maszynie docelowej):

```bash
dotnet publish src/AgentVirtualHand -c Release -r win-x64   --self-contained -p:PublishSingleFile=true
dotnet publish src/AgentVirtualHand -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
```

## Firewall

Jesli klient nie moze sie polaczyc mimo dzialajacego serwera, przycisk **Odblokuj port w firewallu**
dodaje regule dla wybranego portu (Windows: `netsh advfirewall`, profil prywatny i domenowy, z pytaniem
UAC; Linux: `ufw`, `firewalld` albo `iptables` przez `pkexec`/`sudo`). Przycisk **Cofnij** usuwa regule.

## API w skrocie

Wszystko poza `/api/status` i `/api/pair` wymaga naglowka `Authorization: Bearer <token>`.
Pelna instrukcja: `GET /api/help`.

| Endpoint | Opis |
| --- | --- |
| `POST /api/pair` | `{"code":"XXXX-XXXX","client":"claude-code"}` - zwraca token |
| `GET /api/system` | host, uzytkownik, OS, powloka, dyski |
| `GET /api/session` | ile czasu zostalo do konca sesji |
| `POST /api/session/end` | dobrowolne zakonczenie sesji |
| `POST /api/exec` | `{"command":"...","shell":"powershell","cwd":"...","timeoutSeconds":120}` |
| `POST /api/exec/start` | start dlugiej operacji, zwraca `id` |
| `GET /api/exec/{id}` | przyrostowy output (`outOffset`, `errOffset`), `running`, `exitCode` |
| `POST /api/exec/{id}/stdin` | body = tekst na stdin procesu |
| `POST /api/exec/{id}/kill` | ubicie procesu wraz z potomkami |
| `GET /api/fs/list?path=` | zawartosc katalogu |
| `GET /api/fs/read?path=` | tresc pliku (limit `maxBytes`) |
| `GET /api/fs/download?path=` | surowe bajty pliku |
| `POST /api/fs/write` | `{"path":"...","content":"..."}` lub `contentBase64`, opcjonalnie `append` |
| `POST /api/fs/upload?path=` | body = surowe bajty |
| `POST /api/fs/mkdir` \| `delete` \| `move` | operacje na sciezkach |

Przyklad:

```bash
TOKEN=$(curl -s -X POST http://192.168.1.20:8787/api/pair \
  -H "Content-Type: application/json" \
  -d '{"code":"KSP4-55EL","client":"claude-code"}' | jq -r .token)

curl -s http://192.168.1.20:8787/api/exec \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"command":"winget install --id Git.Git -e --silent","timeoutSeconds":600}'
```

W sciezkach Windows w JSON pamietaj o podwojnych backslashach (`C:\\Work`) albo uzywaj `/`.

## Struktura

```
src/AgentVirtualHand/
  Program.cs                 start GUI + tryb --headless
  Server/SessionManager.cs   parowanie, token, czas zycia sesji
  Server/RemoteHttpServer.cs Kestrel + wszystkie endpointy
  Server/ShellRunner.cs      uruchamianie polecen i procesow w tle
  Server/HtmlPages.cs        strona parowania dla przegladarki/telefonu
  Server/HelpText.cs         instrukcja API zwracana pod /api/help
  Services/                  QR, adresy IP, firewall
  ViewModels/MainViewModel.cs stan aplikacji i logika GUI
  Views/MainWindow.axaml     interfejs
```
