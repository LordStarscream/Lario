namespace Lario.Domain;

/// <summary>
/// Kalendermonat als (Jahr, Monat)-Wert (Rechenvertrag §2.2: fachliche
/// Monatsgrenzen Europe/Berlin; Einnahmen können zusätzlich einen
/// Finanzierungsmonat tragen). Kein UTC-Zeitpunkt, keine Tageskomponente.
/// </summary>
public readonly record struct YearMonth(int Year, int Month) : IComparable<YearMonth>
{
    /// <summary>
    /// Erzeugt einen Kalendermonat; Monat muss 1–12 sein, das Jahr muss
    /// plausibel sein (0–9999). Ungültige Werte sind ein Fehler, keine
    /// stille Korrektur.
    /// </summary>
    public static YearMonth Create(int year, int month)
    {
        if (month < 1 || month > 12)
        {
            throw new ArgumentException(
                $"Monat {month} ist ungültig (erwartet 1–12).", nameof(month));
        }

        if (year < 0 || year > 9999)
        {
            throw new ArgumentException(
                $"Jahr {year} ist ungültig (erwartet 0–9999).", nameof(year));
        }

        return new YearMonth(year, month);
    }

    /// <summary>
    /// Erzeugt den Kalendermonat eines Fachdatums (z. B. des Buchungsdats
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
