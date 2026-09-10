# AgentVirtualHand

Awaryjna "zdalna reka" na maszynie: aplikacja Avalonia (Windows + Linux) wystawia maszyne
przez **Tailcat.Link**, a sparowany klient (np. Claude Code na drugim komputerze) moze wykonywac
polecenia powloki, instalowac oprogramowanie, pisac i kompilowac kod oraz zarzadzac plikami.

Polaczenie nie idzie po adresie IP: nie ma hosta, portu ani reguly w firewallu. Obie maszyny
spotykaja sie przez link, a jedyne, co przechodzi przez czlowieka, to jednorazowy kod zaproszenia.

Dostep jest **osobno otwierany**, **czasowy** i **w pelni widoczny** w logu aplikacji.

## Jak to dziala

1. Na maszynie sterowanej: **Uruchom link**, ustaw suwakiem **na ile wpuszczasz** (5 min - 8 h)
   i kliknij **Zapros maszyne**. Aplikacja pokazuje kod wazny 15 minut.
2. Przekazujesz kod tam, gdzie ma dzialac klient: `avh-link join <kod>` albo wklejenie w oknie
   **avh-hub**. Kod wpuszcza **jedna** maszyne i po uzyciu **znika z okna**.
3. Maszyna, ktora go uzyla, od razu pracuje - pojawia sie na liscie z odliczaniem. Przekazanie
   kodu jest juz decyzja o wpuszczeniu, wiec nie ma osobnego potwierdzania.
4. Gdy czas minie, maszyna traci dostep i potrzebuje nowego kodu. W trakcie mozna **Przedluzyc**
   albo **Odetnac** kazda maszyne osobno; **Odetnij wszystkie** konczy prace wszystkim naraz.

Kilka maszyn moze pracowac naraz, kazda z wlasnym oknem czasowym i wlasnym tokenem.

Pierwsze polecenie zestawia link w tle (kilkanascie sekund), kolejne ida w okolo 0,7 s.
`avh-link down` zamyka polaczenie w tle, `avh-link up` podnosi je z powrotem.

## Blokada okna haslem

Oba okna - maszyny sterowanej i huba - sa chronione haslem. Pierwsze uruchomienie **wymusza**
jego ustawienie; nie ma hasla domyslnego, bo znalby je kazdy, kto pobierze plik.
Po **30 sekundach** bez ruchu myszy i klawiatury tresc okna znika i wraca dopiero po podaniu
hasla. Bledna proba kosztuje **10 sekund** odliczania, w trakcie ktorego przycisk nie dziala.

Trzymany jest wylacznie skrot PBKDF2-SHA256 (210 tys. iteracji, losowa sol) w pliku
`lock.json` obok ustawien aplikacji. Zapomnianego hasla nie da sie odzyskac - kasuje sie
ten plik i ustawia nowe przy kolejnym starcie.

## Bezpieczenstwo

Co jest zrobione:

- okno zablokowane haslem po 30 s bezczynnosci, z kara 10 s za bledna probe;

- serwer HTTP sluzy tylko za wewnetrzna szyne, sluchaa **wylacznie na 127.0.0.1** i na porcie
  przydzielonym przez system - z sieci nie ma czego skanowac, zaden port nie jest wystawiony;
- transport miedzy maszynami zestawia Tailcat.Link (szyfrowany, z wlasna tozsamoscia wezla);
- kod zaproszenia jednorazowy, wazny 15 minut; **Nowy kod** uniewaznia poprzedni;
- kod daje dostep tylko na ustawiony czas: po jego uplywie maszyna nie zrobi juz nic
  bez kolejnego kodu, mimo ze sparowanie zostaje;
- token sesji nigdy nie opuszcza maszyny sterowanej - klient go nie zna i nie przekazuje;
- twardy limit czasu sesji, przycisk **Odetnij dostep** ubija sesje i wszystkie uruchomione procesy;
- zamkniecie okna aplikacji konczy sesje i zatrzymuje link;
- kazde polecenie i operacja na plikach trafia do logu na zywo w GUI.

Czego **nie** ma i o czym trzeba pamietac:

- sparowany klient ma pelne uprawnienia konta, na ktorym dziala aplikacja - nie ma piaskownicy
  ani listy dozwolonych polecen;
- sparowanie jest trwale (trust on first use): po pierwszym `join` druga maszyna wraca bez kodu.
  Odbiera sie je przez `avh-link forget` (albo **Usun** w hubie) po stronie klienta,
  a po stronie hosta przyciskiem **Odepnij** przy nazwie maszyny;
- kod zaproszenia jest **jednorazowy**: wpuszcza jedna maszyne i traci waznosc po uzyciu.
  Kolejna maszyna wymaga nowego kodu (**Nowy kod**);
- kilku klientow moze pracowac na jednej maszynie naraz i **widza nawzajem swoje zmiany** -
  to ta sama maszyna, nie osobne piaskownice;
- to narzedzie awaryjne. Wlaczasz je, gdy potrzebujesz pomocy, i wylaczasz, gdy problem jest rozwiazany.

## Uruchomienie

```bash
dotnet run --project src/AgentVirtualHand          # GUI maszyny sterowanej
dotnet build -c Release AgentVirtualHand.slnx      # kompilacja calosci
```

Tryb bez pulpitu (serwer, SSH) - kod zaproszenia ladzie na konsoli, a po jego uzyciu
wypisywany jest kolejny:

```bash
avh --headless --minutes 60
```

Wydanie samodzielne (jeden plik na aplikacje, bez zainstalowanego .NET na maszynie docelowej):

```bash
python build.py                     # win-x64 -> publish/win-x64
python build.py --rid linux-x64     # wydanie dla Linuksa
python build.py --only hub --clean  # tylko wybrana aplikacja, z czyszczeniem katalogu
```

Skrypt sklada trzy pliki: `avh` (maszyna sterowana), `avh-hub` i `avh-link`
(dwa rodzaje klienta). Runtime .NET i biblioteki natywne siedza w srodku, wiec na maszynie
docelowej wystarczy skopiowac jeden plik.

## Dwa rodzaje klienta

Do maszyny sterowanej mozna podlaczyc sie na dwa sposoby - obie strony rozmawiaja tym samym
protokolem, wiec host nie widzi roznicy.

**avh-hub** - aplikacja okienkowa z lista komputerow. Kazde polaczenie dostaje **wlasny port
na 127.0.0.1 i wlasny token**, a "Kopiuj prompt" generuje instrukcje dla modelu tylko do tej
jednej maszyny. To jest sedno: model dostaje adres i token jednego komputera i nie ma czym
siegnac do pozostalych. Kazde polaczenie mozna wylaczyc bez ruszania reszty, a "Nowy token"
uniewaznia wszystkie wczesniej skopiowane prompty tej maszyny.

```bash
avh-hub                                  # okno z lista komputerow
avh-hub --pair <kod> --name labsvcn      # dodanie maszyny z wiersza polecen
```

Lista i tokeny leza w `%APPDATA%\AgentVirtualHand.Hub\connections.json`, a sparowanie kazdej
maszyny w osobnym katalogu `links/<id>` - stad bierze sie izolacja polaczen.

**avh-link** - klient tekstowy dla jednej maszyny, opisany nizej.

## Polecenia klienta

| Polecenie | Opis |
| --- | --- |
| `avh-link join <kod>` | sparowanie, kod jednorazowy z okna aplikacji |
| `avh-link up` \| `down` | zestawia albo zamyka link trzymany w tle |
| `avh-link forget` | usuwa sparowanie z tej maszyny |
| `avh-link system` | host, uzytkownik, OS, powloka, dyski |
| `avh-link session` \| `session end` | pozostaly czas / koniec pracy |
| `avh-link exec "<polecenie>" [--cwd <sciezka>] [--timeout <s>]` | polecenie powloki |
| `avh-link bg start "<polecenie>"` | dluga operacja w tle, zwraca `id` |
| `avh-link bg out <id> [--out-offset N] [--err-offset N]` | przyrostowy output, `running`, `exitCode` |
| `avh-link bg stdin <id> "<tekst>"` \| `bg kill <id>` | stdin procesu / ubicie drzewa procesow |
| `avh-link fs list \| read \| write \| download \| upload \| mkdir \| delete \| move` | operacje na plikach |
| `avh-link api` | dokumentacja API maszyny zdalnej |
| `avh-link --help` | instrukcja samego klienta |
| `--store <katalog>` | inne miejsce na sparowanie (albo `AVH_LINK_STORE`) |

Przyklad:

```bash
avh-link join tco2FwWCBNh-cOZl4meH0AA3DXgL1BNLQyisn3_T7hLFbOY5...
avh-link exec "winget install --id Git.Git -e --silent" --timeout 600
avh-link fs download C:/Work/log.txt ./log.txt
```

W sciezkach Windows uzywaj `/` albo podwojnych backslashy.

## Struktura

```
src/AgentVirtualHand/
  Program.cs                 start GUI + tryb --headless
  Server/SessionManager.cs   okno dostepu, token, czas zycia sesji
  Server/RemoteHttpServer.cs Kestrel na 127.0.0.1 + wszystkie endpointy
  Server/LinkHost.cs         Tailcat.Link -> przekazanie zadania na loopback
  Server/LinkProtocol.cs     koperta wymieniana z klientem (wspolna z avh-link)
  Server/ShellRunner.cs      uruchamianie polecen i procesow w tle
  Server/HelpText.cs         instrukcja zwracana przez avh-link help
  Services/                  ustawienia, blokada okna haslem

src/AgentVirtualHand.Link/
  Program.cs                 klient avh-link: polecenia -> koperty
  LinkDaemon.cs              link trzymany w tle + nazwany potok dla polecen

src/AgentVirtualHand.Hub/
  Services/LinkConnection.cs jedno polaczenie: link + wlasny port i token na 127.0.0.1
  Services/ConnectionStore.cs lista maszyn, tokeny, katalogi sparowan
  Services/PromptBuilder.cs  prompt dla modelu - do jednej maszyny
  ViewModels/HubViewModel.cs lista polaczen, dodawanie, wlaczanie, usuwanie
  ViewModels/MainViewModel.cs stan aplikacji i logika GUI
  Views/MainWindow.axaml     interfejs
```
