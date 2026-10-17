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
    public Money Amount { get; set; }

    /// <summary>Zweck, den die Ausgabe finanziert (Pool−).</summary>
    public string PoolId { get; set; } = string.Empty;

    /// <summary>Alltagskategorie der Ausgabe (unabhängig von der Steuerkennzeichnung).</summary>
    public string CategoryId { get; set; } = string.Empty;

    /// <summary>Freitext, optional.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// Steuerliche Kennzeichnung (Snapshot aus der Erstellung); null =
    /// nicht steuerlich gekennzeichnet.
    /// </summary>
    /// <summary>
    /// Setbar, damit das Repository die Kennzeichnung einer In-place-
    /// Korrektur pflegen kann (vorhanden → aktualisieren, neu → anlegen,
    /// entfällt → löschen); die fachlichen Invarianten gelten an der
    /// Service-Grenze, nicht in der Eigenschaft.
    /// </summary>
    public TaxMarking? Tax { get; set; }

    public override string ToString() =>
        $"{Id} ({Amount.ToEuro():0.00} € -> {PoolId}/{CategoryId}{(Tax is null ? "" : ", " + Tax)})";
}
