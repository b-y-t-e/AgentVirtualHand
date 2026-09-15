namespace AgentVirtualHand.Server;

/// <summary>
/// Polecenie gotowe do uruchomienia w konkretnej powloce. Dla skryptu trzyma plik tymczasowy
/// z jego trescia i kasuje go w <see cref="Dispose"/>.
/// </summary>
public sealed class PreparedCommand : IDisposable
{
    private readonly string? _scriptFile;

    private PreparedCommand(ShellDialect shell, string command, string auditText, string? scriptFile)
    {
        Shell = shell;
        Command = command;
        AuditText = auditText;
        _scriptFile = scriptFile;
    }

    public ShellDialect Shell { get; }

    public string Command { get; }

    /// <summary>To, co faktycznie sie wykona - dla skryptu jego tresc, nie sciezka pliku tymczasowego.</summary>
    public string AuditText { get; }

    public static PreparedCommand Inline(string command, string? shell)
        => new(ShellDialect.Resolve(shell), command, command, scriptFile: null);

    /// <summary>
    /// Zapisuje skrypt do pliku tymczasowego, zeby klient mogl przeslac go wprost (pole "script"),
    /// bez podwajania backslashy i cudzyslowow w zagniezdzonym poleceniu powloki.
    /// </summary>
    public static PreparedCommand FromScript(string content, string? shell)
    {
        var dialect = ShellDialect.Resolve(shell);
        var body = WithoutLeadingByteOrderMark(content);
        var file = Path.Combine(PrivateScriptDirectory(), Guid.NewGuid().ToString("n") + dialect.ScriptExtension);
        using (var writer = new StreamWriter(new FileStream(file, FileMode.CreateNew, FileAccess.Write), dialect.ScriptEncoding))
            writer.Write(dialect.ScriptBody(body));

        return new(dialect, dialect.InvokeScript(file), "[script] " + body, file);
    }

    private const char ByteOrderMarkChar = '\uFEFF';

    /// <summary>
    /// Plik zapisany jako UTF-8 z BOM i przeslany w 'scriptBase64' zaczyna sie od U+FEFF - w .sh/.cmd
    /// psuje to pierwsza komende, a w .ps1 dawaloby podwojny BOM.
    /// </summary>
    private static string WithoutLeadingByteOrderMark(string content) => content.TrimStart(ByteOrderMarkChar);

    private static readonly object ScriptDirectoryLock = new();
    private static string? _scriptDirectory;

    /// <summary>
    /// Katalog na skrypty z losowa nazwa. Na Unix CreateTempSubdirectory daje uprawnienia 0700 (mkdtemp),
    /// wiec inny lokalny uzytkownik nie podlozy katalogu w /tmp ani nie przeczyta/podmieni skryptu; na Windows
    /// %TEMP% jest i tak per uzytkownik. Gdy ktos wyczysci temp w trakcie pracy hosta, tworzymy nowy.
    /// </summary>
    private static string PrivateScriptDirectory()
    {
        lock (ScriptDirectoryLock)
        {
            if (_scriptDirectory is null || !Directory.Exists(_scriptDirectory))
                _scriptDirectory = Directory.CreateTempSubdirectory("avh-").FullName;
            return _scriptDirectory;
        }
    }

    /// <summary>Usuwa katalog skryptow razem z zawartoscia - wolane przy zamykaniu hosta, zeby w temp nie zostawaly kolejne katalogi.</summary>
    public static void DeleteScriptDirectory()
    {
        lock (ScriptDirectoryLock)
        {
            if (_scriptDirectory is null) return;
            try { Directory.Delete(_scriptDirectory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* plik trzymany - system sprzatnie temp */ }
            _scriptDirectory = null;
        }
    }

    public void Dispose()
    {
        if (_scriptFile is null) return;
        try { File.Delete(_scriptFile); }
        catch { /* plik w katalogu temp - system i tak go w koncu sprzatnie */ }
    }
}
