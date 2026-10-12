namespace Lario.Domain;

/// <summary>
/// Kalendermonat als (Jahr, Monat)-Wert (Rechenvertrag §2.2: fachliche
/// Monatsgrenzen Europe/Berlin; Einnahmen können zusätzlich einen
/// Finanzierungsmonat tragen). Kein UTC-Zeitpunkt, keine Tageskomponente.
///
/// Invariante: Monat 1–12 und Jahr 1–9999 (Jahr 0 wäre mit
/// <see cref="DateOnly"/>/<see cref="FirstDay"/> inkompatibel). Der
/// Konstrutor erzwingt die Invariante für jeden direkt erzeugten Wert;
/// die einzige Ausnahme ist <c>default(YearMonth)</c> (0000-00), der den
/// Konstrutor umgeht. Domäneneingänge (z. B. <c>IncomeSpec.FundingMonth</c>)
/// prüfen daher <see cref="IsValid"/>, bevor ein Wert gespeichert wird.
/// </summary>
public readonly record struct YearMonth : IComparable<YearMonth>
{
    /// <summary>Jahr (1–9999).</summary>
    public int Year { get; }

    /// <summary>Monat (1–12).</summary>
    public int Month { get; }

    /// <summary>
    /// Erzeugt einen Kalendermonat; Monat muss 1–12 sein, das Jahr muss
    /// 1–9999 sein. Ungültige Werte sind ein Fehler, keine stille
    /// Korrektur.
    /// </summary>
    public YearMonth(int year, int month)
    {
        Validate(year, month);
        Year = year;
        Month = month;
    }

    /// <summary>
    /// True, wenn die Instanz einen gültigen Kalendermonat trägt
    /// (Monat 1–12, Jahr 1–9999). <c>default(YearMonth)</c> (0000-00)
    /// hat den Konstrutor umgangen und ist ungültig.
    /// </summary>
    public bool IsValid => Year is >= 1 and <= 9999 && Month is >= 1 and <= 12;

    private static void Validate(int year, int month)
    {
        if (month < 1 || month > 12)
        {
            throw new ArgumentException(
                $"Monat {month} ist ungültig (erwartet 1–12).", nameof(month));
        }

        if (year < 1 || year > 9999)
        {
            throw new ArgumentException(
                $"Jahr {year} ist ungültig (erwartet 1–9999).", nameof(year));
        }
    }

    /// <summary>Erzeugt einen Kalendermonat (Factory-Alias für den Konstrutor).</summary>
    public static YearMonth Create(int year, int month) => new(year, month);

    /// <summary>
    /// Erzeugt den Kalendermonat eines Fachdatums (z. B. des Buchungsdatums
    /// einer Einnahme).
    /// </summary>
    public static YearMonth FromDate(DateOnly date) => new(date.Year, date.Month);

    /// <summary>
    /// Parse eines ISO-Monatsschlüssels „YYYY-MM“ (Rechenvertrags-Fixtures,
    /// z. B. „2026-11“). Kein anderes Format wird still akzeptiert.
    /// </summary>
    public static YearMonth Parse(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso))
        {
            throw new ArgumentException("Monatsschlüssel fehlt.", nameof(iso));
        }

        var parts = iso.Split('-');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var year)
            || !int.TryParse(parts[1], out var month))
        {
            throw new FormatException($"Ungültiger Monatsschlüssel '{iso}' (erwartet 'YYYY-MM').");
        }

        if (parts[0].Length != 4 || parts[1].Length != 2)
        {
            throw new FormatException($"Ungültiger Monatsschlüssel '{iso}' (erwartet 'YYYY-MM').");
        }

        return Create(year, month);
    }

    /// <summary>Erster Tag des Monats (nur zur Anzeige/Abgrenzung, kein Fachdatum).</summary>
    public DateOnly FirstDay => new(Year, Month, 1);

    /// <summary>Nächster Kalendermonat (Standardregel Finanzierungsmonat).</summary>
    public YearMonth Next() => Month == 12
        ? Create(Year + 1, 1)
        : Create(Year, Month + 1);

    /// <summary>ISO-Monatsschlüssel „YYYY-MM“.</summary>
    public override string ToString() => $"{Year:0000}-{Month:00}";

    public int CompareTo(YearMonth other) =>
        (Year, Month).CompareTo((other.Year, other.Month));
}
