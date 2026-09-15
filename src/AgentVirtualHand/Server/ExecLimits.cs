namespace AgentVirtualHand.Server;

/// <summary>
/// Limity czasu exec wspolne dla hosta, dokumentacji i klientow - jedno zrodlo, zeby opis i domyslne
/// wartosci klientow nie rozjechaly sie z tym, co host faktycznie wymusza.
/// </summary>
public static class ExecLimits
{
    /// <summary>Najdluzej trzymana odpowiedz - pod ~30 s progiem rundy transportu, wiec zawsze zdazy wrocic.</summary>
    public const int MaxResponseHoldSeconds = 25;

    /// <summary>Ile najdluzej czekamy na potoki po zakonczeniu procesu - wydluza czas odpowiedzi exec.</summary>
    public const int OutputDrainGraceSeconds = 3;

    /// <summary>Limit zwyklego exec: po zabiciu procesu dochodzi jeszcze czekanie na potoki, a calosc musi zmiescic sie w oknie odpowiedzi.</summary>
    public const int MaxSyncExecSeconds = MaxResponseHoldSeconds - OutputDrainGraceSeconds;
}
