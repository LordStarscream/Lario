namespace Lario.Domain.Journal;

/// <summary>
/// Status einer offenen internen Ausgleichsposition (Rechenvertrag §3.5).
/// </summary>
public enum SettlementStatus
{
    /// <summary>Position ist offen; der Restbetrag wird noch nicht getragen.</summary>
    Offen = 1,

    /// <summary>Position ist vollständig beglichen (Restbetrag 0).</summary>
    Beglichen = 2,
}

/// <summary>
/// Offene interne Ausgleichsposition (Rechenvertrag §3.5): entsteht, wenn
/// ein Konto für Pools „vorfinanziert" hat — die Konto-Pool-Zuordnung
/// des Paares (Konto, Pool) ist negativ.
///
/// Invariante (verbindlich): Je offener Position gilt
/// <see cref="RestAmount"/> = Betrag, den das <see cref="SourceAccountId"/>
/// Konto dem <see cref="TargetPoolId"/> Pool schuldet; die Summe der
/// Restbeträge der offenen Positionen einer Zelle entspricht exakt
/// max(0, −Zuordnung) — die negative Zuordnung wird nicht als vorhandenes
/// Geld angezeigt (§3.4).
///
/// Teilbegleichung: <see cref="RestAmount"/> sinkt um den Teilbetrag und
/// bleibt offen, bis es 0 erreicht (dann <see cref="SettlementStatus.Beglichen"/>,
/// <see cref="SettledByBookingId"/> trägt die buchende Buchung). Eine
/// Buchung, die die negative Zuordnung vertieft, erhöht den Restbetrag
/// (und <see cref="Amount"/> als Summe der Episode).
/// </summary>
public sealed class SettlementPosition
{
    /// <summary>Stabile Identität der Position (eigene Id, keine Buchung-Id).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Das vorfinanzierende Konto: es hat Ausgaben für
    /// <see cref="TargetPoolId"/> über sich hinaus bezahlt.
    /// </summary>
    public string SourceAccountId { get; init; } = string.Empty;

    /// <summary>Der vorfinanzierte Pool (der „geldgebende" Zweck).</summary>
    public string TargetPoolId { get; init; } = string.Empty;

    /// <summary>
    /// Gesamtbetrag der Position: Summe aller negativen Zuordnungen der
    /// Episode (wächst bei Vertiefung, nicht bei Begleichung).
    /// </summary>
    public Money Amount { get; set; }

    /// <summary>
    /// Noch offener Restbetrag; sinkt bei Teilbegleichung, 0 = beglichen.
    /// </summary>
    public Money RestAmount { get; set; }

    /// <summary>Lebenszyklus-Status (offen/beglichen).</summary>
    public SettlementStatus Status { get; set; } = SettlementStatus.Offen;

    /// <summary>Buchung, die die Position zuerst entstanden ließ.</summary>
    public string CreatedByBookingId { get; init; } = string.Empty;

    /// <summary>Fachdatum der erstellenden Buchung.</summary>
    public DateOnly CreatedDate { get; init; }

    /// <summary>
    /// Buchung, die den Restbetrag zuletzt auf 0 gebracht hat
    /// (nur bei <see cref="SettlementStatus.Beglichen"/>).
    /// </summary>
    public string? SettledByBookingId { get; set; }

    /// <summary>Fachdatum der Begleichung (nur bei <see cref="SettlementStatus.Beglichen"/>).</summary>
    public DateOnly? SettledDate { get; set; }

    /// <summary>Freitext, optional (z. B. „Vorfinanzierung: Freizeit über Hauptkonto bezahlt").</summary>
    public string? Note { get; init; }
}
