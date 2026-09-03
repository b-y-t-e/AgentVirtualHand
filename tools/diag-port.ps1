<#
    Diagnostyka bledu WSAEACCES (10013) przy starcie serwera AgentVirtualHand.
    Uruchom w PowerShellu JAKO ADMINISTRATOR:
        powershell -ExecutionPolicy Bypass -File .\diag-port.ps1
    Wynik ladunie w pliku diag-port-WYNIK.txt obok skryptu (i na ekranie).
#>

$ErrorActionPreference = 'Continue'
$ports = @(8787, 31314, 45999)
$out   = Join-Path $PSScriptRoot 'diag-port-WYNIK.txt'
if (-not $PSScriptRoot) { $out = Join-Path (Get-Location) 'diag-port-WYNIK.txt' }

$lines = New-Object System.Collections.Generic.List[string]
function W([string]$t = '') { $lines.Add($t); Write-Host $t }
function Section([string]$t) { W ''; W ('=' * 70); W "== $t"; W ('=' * 70) }

Section 'SRODOWISKO'
W "Data:        $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
W "Komputer:    $env:COMPUTERNAME"
W "Uzytkownik:  $env:USERDOMAIN\$env:USERNAME"
W "OS:          $((Get-CimInstance Win32_OperatingSystem).Caption) $((Get-CimInstance Win32_OperatingSystem).Version)"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
W "Admin:       $isAdmin"
if (-not $isAdmin) { W '!! UWAGA: skrypt NIE dziala jako administrator - czesc testow bedzie niepelna.' }

Section '1. TEST BINDOWANIA (czysty socket, bez aplikacji)'
foreach ($p in $ports) {
    foreach ($pair in @(@('0.0.0.0', [System.Net.IPAddress]::Any), @('127.0.0.1', [System.Net.IPAddress]::Loopback))) {
        $label = $pair[0]; $addr = $pair[1]
        try {
            $l = [System.Net.Sockets.TcpListener]::new($addr, $p)
            $l.Start(); $l.Stop()
            W ("  {0,-10} port {1,-6} -> OK" -f $label, $p)
        } catch {
            $sock = $_.Exception.InnerException
            $code = if ($sock -is [System.Net.Sockets.SocketException]) { "$($sock.SocketErrorCode) ($($sock.ErrorCode))" } else { '-' }
            W ("  {0,-10} port {1,-6} -> FAIL  {2}  :: {3}" -f $label, $p, $code, $_.Exception.Message)
        }
    }
}

Section '2. ZAREZERWOWANE ZAKRESY PORTOW'
W (netsh int ipv4 show excludedportrange protocol=tcp | Out-String).Trim()
W (netsh int ipv4 show dynamicport tcp | Out-String).Trim()

Section '3. HTTP.SYS (IIS / inne uslugi trzymajace port na wylacznosc)'
$svcState = netsh http show servicestate view=requestq 2>&1 | Out-String
foreach ($p in $ports) {
    $hit = ($svcState -split "`r?`n") | Where-Object { $_ -match ":$p\b" }
    if ($hit) { W "  PORT $p ZNALEZIONY w HTTP.sys:"; $hit | ForEach-Object { W "    $_" } }
    else      { W "  port $p - brak w HTTP.sys" }
}
W ''
W '-- urlacl (rezerwacje URL) --'
$urlacl = netsh http show urlacl 2>&1 | Out-String
foreach ($p in $ports) {
    $hit = ($urlacl -split "`r?`n") | Where-Object { $_ -match ":$p[/:]" }
    if ($hit) { $hit | ForEach-Object { W "    $_" } } else { W "    port $p - brak rezerwacji" }
}
W ''
W '-- iplisten (HTTP.sys nasluchuje tylko na tych IP; puste = wszystkie) --'
W (netsh http show iplisten 2>&1 | Out-String).Trim()

Section '4. CO NASLUCHUJE NA TYCH PORTACH'
$any = $false
foreach ($p in $ports) {
    $conns = Get-NetTCPConnection -LocalPort $p -ErrorAction SilentlyContinue
    foreach ($c in $conns) {
        $any = $true
        $proc = Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue
        W ("  port {0}  {1}  {2}  PID={3}  proces={4}" -f $p, $c.LocalAddress, $c.State, $c.OwningProcess, $(if ($proc) { $proc.ProcessName } else { '?' }))
    }
}
if (-not $any) { W '  nic nie nasluchuje na 8787 / 31314 / 45999' }

Section '5. USLUGI SIECIOWE / FILTRUJACE'
Get-Service BFE, MpsSvc, SharedAccess, hns, vmcompute, WinNat, W3SVC, WAS, HTTP -ErrorAction SilentlyContinue |
    Sort-Object Name | Format-Table Name, Status, StartType -AutoSize | Out-String | ForEach-Object { W $_.Trim() }

Section '6. ZAPORA - STAN PROFILI I REGULY AGENTVIRTUALHAND'
Get-NetFirewallProfile -ErrorAction SilentlyContinue |
    Format-Table Name, Enabled, DefaultInboundAction, DefaultOutboundAction -AutoSize | Out-String | ForEach-Object { W $_.Trim() }
W ''
$rules = Get-NetFirewallRule -DisplayName '*AgentVirtualHand*' -ErrorAction SilentlyContinue
if ($rules) {
    foreach ($r in $rules) {
        $pf = $r | Get-NetFirewallPortFilter -ErrorAction SilentlyContinue
        W ("  [{0}] {1} | dir={2} akcja={3} profil={4} porty={5}" -f $r.Enabled, $r.DisplayName, $r.Direction, $r.Action, $r.Profile, $pf.LocalPort)
    }
} else { W '  brak regul AgentVirtualHand' }

Section '7. OPROGRAMOWANIE OCHRONNE (AV / EDR)'
try {
    Get-CimInstance -Namespace root\SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction Stop |
        Format-Table displayName, productState -AutoSize | Out-String | ForEach-Object { W $_.Trim() }
} catch { W "  (nie udalo sie odczytac SecurityCenter2: $($_.Exception.Message))" }
W ''
W '-- uslugi wygladajace na AV/EDR --'
$avPattern = 'crowdstrike|sentinel|carbonblack|cylance|sophos|eset|kaspersky|mcafee|trellix|symantec|sepmaster|trend|bitdefender|f-secure|withsecure|paloalto|cortex|tanium|qualys|nessus|checkpoint|forcepoint|deepinstinct|elastic-endpoint'
$avs = Get-Service -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $avPattern -or $_.DisplayName -match $avPattern }
if ($avs) { $avs | Format-Table Name, DisplayName, Status -AutoSize | Out-String | ForEach-Object { W $_.Trim() } }
else { W '  nie wykryto typowych uslug AV/EDR (poza Defenderem)' }
W ''
W '-- sterowniki sieciowe innych producentow (mozliwe filtry) --'
try {
    $sys = [regex]::Escape("$env:SystemRoot\System32\drivers\\")
    $drv = Get-CimInstance Win32_SystemDriver -ErrorAction SilentlyContinue |
        Where-Object { $_.State -eq 'Running' -and ($_.Name -match 'flt|net|ndis|tdi|wfp|fw|eamon|ehdrv|epfw') } |
        Where-Object { $_.Name -notmatch '^(tcpip|netbt|afd|http|mpsdrv|wfplwfs|ndis|netbios|npsvctrig|nsiproxy|tdx|netio)$' }
    if ($drv) { $drv | Select-Object Name, DisplayName | Sort-Object Name | Format-Table -AutoSize | Out-String | ForEach-Object { W $_.Trim() } }
    else { W '  brak nietypowych sterownikow' }
} catch { W "  (pominieto: $($_.Exception.Message))" }

Section '7b. ESET - stan zapory i ochrony sieci (jesli zainstalowany)'
$eset = Get-Service -Name 'ekrn','ekrnEpfw','efwd' -ErrorAction SilentlyContinue
if ($eset) {
    $eset | Format-Table Name, DisplayName, Status -AutoSize | Out-String | ForEach-Object { W $_.Trim() }
    W ''
    W '  ESET jest obecny. To najczestszy sprawca bledu 10013 przy bind() -'
    W '  modul Network Protection / Firewall blokuje nasluch nieznanej aplikacji.'
    W '  Sprawdz w konsoli ESET: Ustawienia > Ochrona sieci > Zapora > Reguly'
    W '  oraz dziennik ESET (Narzedzia > Pliki dziennika > Zapora sieciowa) z godziny bledu.'
    $log = Join-Path $env:ProgramData 'ESET\ESET Security\Logs'
    if (Test-Path $log) { W "  Katalog logow ESET: $log" }
} else { W '  ESET niezainstalowany' }

Section '8. ZDARZENIA SYSTEMOWE Z OSTATNIEJ GODZINY (filtrowanie/zapora)'
try {
    $since = (Get-Date).AddHours(-2)
    $ev = Get-WinEvent -FilterHashtable @{ LogName = 'System'; StartTime = $since } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -match 'Filtering|Firewall|Tcpip|Winsock|BFE' } | Select-Object -First 20
    if ($ev) { $ev | Format-Table TimeCreated, ProviderName, Id, LevelDisplayName -AutoSize | Out-String | ForEach-Object { W $_.Trim() } }
    else { W '  brak istotnych zdarzen' }
} catch { W "  (blad odczytu dziennika: $($_.Exception.Message))" }

Section 'KONIEC'
$lines | Set-Content -Path $out -Encoding UTF8
Write-Host ''
Write-Host "Wynik zapisany do: $out" -ForegroundColor Green
