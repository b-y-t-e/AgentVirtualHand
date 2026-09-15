using System.Diagnostics;
using System.Text;

namespace AgentVirtualHand.Server;

/// <summary>
/// Jak konkretna powloka uruchamia polecenie i plik skryptu. Jedyne miejsce, ktore mapuje nazwe powloki
/// z zadania na interpreter - nowa powloka to nowa klasa i wpis w <see cref="Resolve"/>.
/// </summary>
public abstract class ShellDialect
{
    protected static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly Dictionary<string, Func<ShellDialect>> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cmd"] = () => new CmdDialect(),
        ["powershell"] = () => new PowerShellDialect("powershell.exe"),
        ["pwsh"] = () => new PowerShellDialect("pwsh"),
        ["sh"] = () => new PosixDialect("/bin/sh", loginShell: false),
        ["bash"] = () => new PosixDialect(OperatingSystem.IsWindows() ? "bash" : "/bin/bash", loginShell: true),
    };

    public static string DefaultShellName => OperatingSystem.IsWindows() ? "powershell" : "bash";

    /// <summary>Powloka o podanej nazwie; pusta albo nieznana nazwa daje domyslna powloke systemu.</summary>
    public static ShellDialect Resolve(string? shell)
        => ByName.TryGetValue(shell?.Trim() ?? "", out var create) ? create() : ByName[DefaultShellName]();

    public abstract string ScriptExtension { get; }

    public virtual Encoding ScriptEncoding => Utf8NoBom;

    /// <summary>Tresc zapisywana do pliku skryptu - powloka moze dopisac to, czego potrzebuje do poprawnego kodu wyjscia.</summary>
    public virtual string ScriptBody(string content) => content;

    /// <summary>Polecenie, ktore w tej powloce uruchamia plik skryptu.</summary>
    public abstract string InvokeScript(string scriptPath);

    /// <summary>Ustawia interpreter i argumenty tak, zeby wykonal polecenie z wyjsciem w UTF-8.</summary>
    public abstract void Configure(ProcessStartInfo startInfo, string command);
}

internal sealed class CmdDialect : ShellDialect
{
    public override string ScriptExtension => ".cmd";

    // cmd.exe w pliku z samymi LF gubi etykiety ("goto :label", "call :sub") - zapisujemy CRLF.
    public override string ScriptBody(string content) => content.ReplaceLineEndings("\r\n");

    public override string InvokeScript(string scriptPath) => $"\"{scriptPath}\"";

    public override void Configure(ProcessStartInfo startInfo, string command)
    {
        startInfo.FileName = "cmd.exe";
        // chcp 65001 przelacza konsole na UTF-8, zeby natywne procesy potomne nie oddawaly
        // tekstu w domyslnym kodowaniu OEM (stad "odczyt?w" po stronie klienta).
        startInfo.Arguments = "/d /c chcp 65001>nul & " + command;
    }
}

internal sealed class PowerShellDialect(string fileName) : ShellDialect
{
    // Wymusza UTF-8 na wyjsciu, na potoku do natywnych komend ($OutputEncoding) i na kodowych stronach
    // konsoli (setter [Console]::Input/OutputEncoding) - inaczej procesy-wnuki oddaja polskie znaki w OEM/ANSI.
    // Bez BOM: UTF8Encoding($false) zamiast [Text.Encoding]::UTF8 i bez "chcp 65001" - po obu PowerShell 5.1
    // dokleja BOM na poczatek kazdego wejscia przekazywanego potokiem do natywnej komendy.
    private const string Utf8Prefix =
        "$OutputEncoding=[Console]::InputEncoding=[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false); ";

    /// <summary>
    /// Dopisywane na koncu skryptu: $? odnosi sie tu do ostatniej instrukcji skryptu, wiec nieudana natywna
    /// komenda ("dotnet build") oddaje swoj kod, a wczesniejszy nieaktualny $LASTEXITCODE (robocopy = 1
    /// przy sukcesie, potem Write-Host) nie psuje wyniku udanego skryptu.
    /// </summary>
    private const string ExitWithLastStatementResult =
        "\nif ($?) { exit 0 }; if ($LASTEXITCODE) { exit $LASTEXITCODE }; exit 1";

    // .ps1 czyta Windows PowerShell 5.1 domyslnie jako ANSI, gdy nie ma BOM - polskie znaki
    // w samym skrypcie by sie sypaly, wiec tu piszemy UTF-8 z BOM.
    public override Encoding ScriptEncoding => Encoding.UTF8;

    public override string ScriptExtension => ".ps1";

    public override string ScriptBody(string content) => content + ExitWithLastStatementResult;

    /// <summary>
    /// "exit N" w skrypcie (takze z dopisanego zakonczenia) ustawia $LASTEXITCODE i $? = false. Wyjatek
    /// konczacy skrypt daje $? = false przy wyzerowanym $LASTEXITCODE, wiec wtedy zwracamy 1.
    /// </summary>
    public override string InvokeScript(string scriptPath)
        => $"$global:LASTEXITCODE=0; & {Quote(scriptPath)}; if (-not $?) {{ if ($LASTEXITCODE) {{ exit $LASTEXITCODE }}; exit 1 }}";

    public override void Configure(ProcessStartInfo startInfo, string command)
    {
        startInfo.FileName = fileName;
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(Utf8Prefix + command);
    }

    /// <summary>Literal w apostrofach dla PowerShell - apostrof w sciezce (np. C:\Users\O'Brien) podwajamy.</summary>
    private static string Quote(string text) => "'" + text.Replace("'", "''") + "'";
}

internal sealed class PosixDialect(string fileName, bool loginShell) : ShellDialect
{
    public override string ScriptExtension => ".sh";

    // Skrypt z pliku Windows ma CRLF - bash czytalby '\r' jako czesc polecenia ("$'\r': command not found").
    public override string ScriptBody(string content) => content.ReplaceLineEndings("\n");

    public override string InvokeScript(string scriptPath)
        => OperatingSystem.IsWindows()
            ? $"{ScriptPathVariable}={Quote(scriptPath)}; {TranslateForWsl}; {Quote(fileName)} \"${ScriptPathVariable}\""
            : $"{Quote(fileName)} {Quote(scriptPath)}";

    private const string ScriptPathVariable = "avh_script";

    /// <summary>
    /// "bash" na Windows to zwykle WSL, ktory nie zna sciezek C:\ - tlumaczymy je przez wslpath.
    /// Git Bash nie ma wslpath i sam rozumie sciezki Windows, wiec tam sciezka zostaje bez zmian.
    /// </summary>
    private const string TranslateForWsl =
        $"if command -v wslpath >/dev/null 2>&1; then {ScriptPathVariable}=$(wslpath -u \"${ScriptPathVariable}\"); fi";

    public override void Configure(ProcessStartInfo startInfo, string command)
    {
        startInfo.FileName = fileName;
        startInfo.ArgumentList.Add(loginShell ? "-lc" : "-c");
        startInfo.ArgumentList.Add(command);
    }

    /// <summary>Literal w apostrofach dla sh/bash - apostrof zamykamy, wstawiamy \' i otwieramy ponownie.</summary>
    private static string Quote(string text) => "'" + text.Replace("'", "'\\''") + "'";
}
