namespace Lario.Domain;

/// <summary>
/// EUR-Betrag als ganzzahliger Centwert (long).
/// Verbindliche Regel aus dem Rechenvertrag (docs/step-0/rechenvertrag.md):
/// kein float/double für Geld; Prozentrechnung später mit decimal und
/// expliziter Rundung. Splitbeträge müssen sich exakt zum Gesamtbetrag
/// summieren — mit ganzen Cents ist das ohne Rundungsverlust gegeben.
/// </summary>
public readonly record struct Money(long Cents)
{
    /// <summary>0,00 EUR.</summary>
    public static Money Zero { get; } = new(0);

    /// <summary>1,00 EUR.</summary>
    public static Money OneEuro { get; } = new(100);

    /// <summary>Erzeugt einen Betrag aus einem exakten Centwert.</summary>
    public static Money FromCents(long cents) => new(cents);

    /// <summary>
    /// Erzeugt einen Betrag aus einem decimal-Eurobetrag.
    /// Rundung ist explizit: halbe Cent immer von null weg
    /// (MidpointRounding.AwayFromZero), z. B. 1,005 EUR -> 101 Cent.
    /// </summary>
    public static Money FromEuro(decimal euro) =>
        new((long)Math.Round(euro * 100m, MidpointRounding.AwayFromZero));

    /// <summary>
    /// Exakte Addition. Wirft <see cref="OverflowException"/>,
    /// wenn das long-Cent-Intervall verlassen würde.
    /// </summary>
    public Money Add(Money other) => new(Checked(Cents, other.Cents, 1));

    /// <summary>
    /// Exakte Subtraktion. Wirft <see cref="OverflowException"/>,
    /// wenn das long-Cent-Intervall verlassen würde.
    /// </summary>
    public Money Subtract(Money other) => new(Checked(Cents, other.Cents, -1));

    /// <summary>
    /// Vorzeichenumkehr (z. B. für Korrekturen).
    /// long.MinValue ist nicht negierbar — wird abgelehnt.
    /// </summary>
    public Money Negate() => Cents == long.MinValue
        ? throw new OverflowException("Money: Negation von long.MinValue Cent ist nicht darstellbar.")
        : new(-Cents);

    /// <summary>Exakter Betrag in decimal-EUR (für Anzeige und Prozentrechnung).</summary>
    public decimal ToEuro() => Cents / 100m;

    private static long Checked(long a, long b, int sign)
    {
        // Vorzeichenkontrolle ohne checked-Block: a + sign*b darf nicht überlaufen.
        if (sign > 0)
        {
            if (b > 0 && a > long.MaxValue - b)
                throw new OverflowException("Money: Addition übersteigt long.MaxValue Cent.");
            if (b < 0 && a < long.MinValue - b)
                throw new OverflowException("Money: Addition übersteigt long.MinValue Cent.");
            return a + b;
        }

        if (b > 0 && a < long.MinValue + b)
            throw new OverflowException("Money: Subtraktion übersteigt long.MinValue Cent.");
        if (b < 0 && a > long.MaxValue + b)
            throw new OverflowException("Money: Subtraktion übersteigt long.MaxValue Cent.");
        return a - b;
    }

    public static Money operator +(Money left, Money right) => left.Add(right);
    public static Money operator -(Money left, Money right) => left.Subtract(right);

    // Vergleichsoperatoren werden von record struct nicht synthetisiert.
    public static bool operator <(Money left, Money right) => left.Cents < right.Cents;
    public static bool operator <=(Money left, Money right) => left.Cents <= right.Cents;
    public static bool operator >(Money left, Money right) => left.Cents > right.Cents;
    public static bool operator >=(Money left, Money right) => left.Cents >= right.Cents;
}
