<#
    Test wariantow gniazda TCP - szuka roznicy miedzy TcpListener (dziala)
    a Kestrelem (WSAEACCES 10013).
    Uruchom JAKO ADMINISTRATOR, tak samo jak aplikacje:
        powershell -NoProfile -ExecutionPolicy Bypass -File .\diag-socket.ps1
#>

$ErrorActionPreference = 'Continue'
$ports = @(31314, 8787, 45999)
$out = Join-Path (Get-Location) 'diag-socket-WYNIK.txt'
$lines = New-Object System.Collections.Generic.List[string]
function W([string]$t = '') { $lines.Add($t); Write-Host $t }

function Try-Bind {
    param([string]$Name, [scriptblock]$Make, [int]$Port)
    $s = $null
    try {
        $s = & $Make $Port
        W ("  {0,-46} -> OK" -f $Name)
    } catch {
        $e = $_.Exception
        while ($e.InnerException) { $e = $e.InnerException }
        $code = if ($e -is [System.Net.Sockets.SocketException]) { "$($e.SocketErrorCode) ($($e.ErrorCode))" } else { $e.GetType().Name }
        W ("  {0,-46} -> FAIL  {1}" -f $Name, $code)
        W ("  {0,-46}    {1}" -f '', $e.Message)
    } finally {
        if ($s) {
            if ($s -is [System.Net.Sockets.TcpListener]) { try { $s.Stop() } catch {} }
            try { $s.Close() } catch {}
            try { $s.Dispose() } catch {}
        }
    }
}

W "Komputer: $env:COMPUTERNAME   Data: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
W "Admin: $isAdmin"
W "IPv6 DisabledComponents: $((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters' -Name DisabledComponents -ErrorAction SilentlyContinue).DisabledComponents)"

foreach ($p in $ports) {
    W ''
    W ("PORT $p " + ('-' * 55))

    Try-Bind 'TcpListener(Any) - jak IsPortFree w aplikacji' {
        param($port)
        $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Any, $port); $l.Start(); $l
    } $p

    Try-Bind 'Socket IPv4, domyslne opcje - jak Kestrel' {
        param($port)
        $s = [System.Net.Sockets.Socket]::new([System.Net.Sockets.AddressFamily]::InterNetwork,
             [System.Net.Sockets.SocketType]::Stream, [System.Net.Sockets.ProtocolType]::Tcp)
        $s.Bind([System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, $port)); $s.Listen(512); $s
    } $p

    Try-Bind 'Socket IPv4, ExclusiveAddressUse = $true' {
        param($port)
        $s = [System.Net.Sockets.Socket]::new([System.Net.Sockets.AddressFamily]::InterNetwork,
             [System.Net.Sockets.SocketType]::Stream, [System.Net.Sockets.ProtocolType]::Tcp)
        $s.ExclusiveAddressUse = $true
        $s.Bind([System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, $port)); $s.Listen(512); $s
    } $p

    Try-Bind 'Socket IPv4, SO_REUSEADDR' {
        param($port)
        $s = [System.Net.Sockets.Socket]::new([System.Net.Sockets.AddressFamily]::InterNetwork,
             [System.Net.Sockets.SocketType]::Stream, [System.Net.Sockets.ProtocolType]::Tcp)
        $s.SetSocketOption([System.Net.Sockets.SocketOptionLevel]::Socket, [System.Net.Sockets.SocketOptionName]::ReuseAddress, $true)
        $s.Bind([System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, $port)); $s.Listen(512); $s
    } $p

    Try-Bind 'Socket IPv6 (::) DualMode - jak ListenAnyIP' {
        param($port)
        $s = [System.Net.Sockets.Socket]::new([System.Net.Sockets.AddressFamily]::InterNetworkV6,
             [System.Net.Sockets.SocketType]::Stream, [System.Net.Sockets.ProtocolType]::Tcp)
        $s.DualMode = $true
        $s.Bind([System.Net.IPEndPoint]::new([System.Net.IPAddress]::IPv6Any, $port)); $s.Listen(512); $s
    } $p

    Try-Bind 'Socket IPv6 (::) bez DualMode' {
        param($port)
        $s = [System.Net.Sockets.Socket]::new([System.Net.Sockets.AddressFamily]::InterNetworkV6,
             [System.Net.Sockets.SocketType]::Stream, [System.Net.Sockets.ProtocolType]::Tcp)
        $s.Bind([System.Net.IPEndPoint]::new([System.Net.IPAddress]::IPv6Any, $port)); $s.Listen(512); $s
    } $p

    Try-Bind 'SEKWENCJA APLIKACJI: IsPortFree -> Stop -> bind Kestrel' {
        param($port)
        $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Any, $port)
        $probe.Start(); $probe.Stop()
        $s = [System.Net.Sockets.Socket]::new([System.Net.Sockets.AddressFamily]::InterNetwork,
             [System.Net.Sockets.SocketType]::Stream, [System.Net.Sockets.ProtocolType]::Tcp)
        $s.Bind([System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, $port)); $s.Listen(512); $s
    } $p

    Try-Bind 'Socket IPv4 na konkretnym IP (nie 0.0.0.0)' {
        param($port)
        $ip = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
               Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
               Select-Object -First 1).IPAddress
        if (-not $ip) { throw 'brak adresu IPv4' }
        $s = [System.Net.Sockets.Socket]::new([System.Net.Sockets.AddressFamily]::InterNetwork,
             [System.Net.Sockets.SocketType]::Stream, [System.Net.Sockets.ProtocolType]::Tcp)
        $s.Bind([System.Net.IPEndPoint]::new([System.Net.IPAddress]::Parse($ip), $port)); $s.Listen(512); $s
    } $p
}

W ''
W 'Legenda: jesli pada TYLKO wariant IPv6/DualMode - problem to IPv6 na serwerze.'
W '         Jesli pada wariant z domyslnymi opcjami, a TcpListener przechodzi -'
W '         port trzyma gniazdo zbindowane ale nienasluchujace (niewidoczne w netstat).'
$lines | Set-Content -Path $out -Encoding UTF8
Write-Host ''
Write-Host "Wynik zapisany do: $out" -ForegroundColor Green
