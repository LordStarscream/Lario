namespace Lario.Domain.Journal;

/// <summary>
/// Teilposten (Split) einer Ausgabe (Rechenvertrag §3.5, Plan §9):
/// ein positiver Cent-Betrag, der einen Zweck (Pool) und eine
/// Alltagskategorie trägt, plus optional Notiz und die steuerliche
/// Kennzeichnung. Die Summe aller Teilposten einer Ausgabe muss exakt
/// den Gesamtbetrag der Buchung ergeben.
///
/// Die <see cref="Tax"/>-Kennzeichnung ist der bei Erstellung
/// materialisierte Snapshot (explizite Angabe oder wirksame
/// Kategorie-Voreinstellung); sie wird nicht abgeleitet und von späteren
/// Voreinstellungsänderungen nicht berührt.
/// </summary>
public sealed class ExpenseSplit
{
    /// <summary>Stabile Identität des Teilpostens (z. B. „sp-1“).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Positiver Betrag in Cent; muss zusammen mit den anderen Splits exakt den Gesamtbetrag ergeben.</summary>
    public Money Amount { get; init; }

    /// <summary>Zweck, den die Ausgabe finanziert (Pool−).</summary>
    public string PoolId { get; init; } = string.Empty;

    /// <summary>Alltagskategorie der Ausgabe (unabhängig von der Steuerkennzeichnung).</summary>
    public string CategoryId { get; init; } = string.Empty;

    /// <summary>Freitext, optional.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// Steuerliche Kennzeichnung (Snapshot aus der Erstellung); null =
    /// nicht steuerlich gekennzeichnet.
    /// </summary>
    public TaxMarking? Tax { get; init; }

    public override string ToString() =>
        $"{Id} ({Amount.ToEuro():0.00} € -> {PoolId}/{CategoryId}{(Tax is null ? "" : ", " + Tax)})";
}
