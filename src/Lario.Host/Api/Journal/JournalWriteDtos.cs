namespace Lario.Host.Api.Journal;

/// <summary>
/// Schreib-Command-DTOs der Journal-Endpunkte (Schritt 7, T4; Design
/// §2.4). Die Host-DTOs tragen nur die Transportform und primitive
/// Typen (Money-Cent als <c>long</c>), keine Fachlogik: das
/// Command→Spec-Mapping und die fachliche Validierung liegen in
/// Domain/Application (bestehend).
///
/// Datumsfelder kommen als <c>string</c> (Format <c>yyyy-MM-dd</c>,
/// <c>fundingMonth</c> als <c>yyyy-MM</c>): ungültige Formate werden
/// als 400 validation_error gemeldet statt als 500 durch den
/// Modellbinder. Beträge sind Ganzzahl-Cent (D6).
///
/// Jede Schreibantwort ist eine <see cref="BookingCreatedDto"/>:
/// dasselbe <see cref="BookingDetailDto"/> wie <c>GET /bookings/{id}</c>
/// plus dem Idempotenz-Flag (true = identischer Vorgang mit gleicher
/// Id wurde wiederholt, es wurde keine zweite Buchung angelegt).
/// </summary>

// ---------- Steuer-Überschreibung ----------

/// <summary>
/// Steuerkennzeichnung eines Ausgabe-Splits. <c>tax</c> null = keine
/// explizite Angabe (die wirksame Kategorie-Voreinstellung greift,
/// Materialisierung serverseitig).
/// </summary>
public sealed record ExpenseSplitTaxDto(
    bool Relevant,
    string? TaxCategoryId,
    long? MarkedAmount,
    string? Note);

// ---------- Ausgabe ----------

/// <summary>Ein Teilposten (Split) einer Ausgabe.</summary>
public sealed record ExpenseSplitCommand(
    string Id,
    long Amount,
    string PoolId,
    string CategoryId,
    string? Note,
    ExpenseSplitTaxDto? Tax);

/// <summary>
/// Ausgabe (Kernfunktion 3): Gesamtbetrag (positiv, Cent) und
/// vollständige Teilung in Splits (Summe = Gesamtbetrag — Prüfung in
/// der Domain).
/// </summary>
public sealed record ExpenseCommand(
    string Id,
    string Date,
    long TotalAmount,
    string AccountId,
    string? Note,
    IReadOnlyList<ExpenseSplitCommand> Splits,
    int? ExpectedBaseRevision);

// ---------- Einnahme ----------

/// <summary>Ein Pool-Zufluss einer Einnahme.</summary>
public sealed record IncomeAllocationCommand(string PoolId, long Amount);

/// <summary>
/// Einnahme (Kernfunktion 3): positiver Betrag, vollständige
/// Verteilung auf Pools (Summe = Betrag — Prüfung in der Domain).
/// <c>FundingMonth</c> null = Standardregel „Gehalt am Monatsende
/// finanziert den Folgemonat".
/// </summary>
public sealed record IncomeCommand(
    string Id,
    string Date,
    long Amount,
    string AccountId,
    string? Note,
    string? FundingMonth,
    IReadOnlyList<IncomeAllocationCommand> Allocations,
    int? ExpectedBaseRevision);

// ---------- Anfangsbestand ----------

/// <summary>Ein Pool-Anteil eines Anfangsbestands.</summary>
public sealed record PoolAllocationCommand(string PoolId, long Amount);

/// <summary>
/// Anfangsbestand (Kernfunktion 5): Gesamtbetrag am Stichtag auf dem
/// Konto und optionale Verteilung auf Pools; der Rest bleibt sichtbar
/// „nicht zugeordnet" (abgeleitet, keine eigene Buchung).
/// </summary>
public sealed record OpeningBalanceCommand(
    string Id,
    string Date,
    long Total,
    string AccountId,
    IReadOnlyList<PoolAllocationCommand> Distribution,
    string? Note);

// ---------- Transfers / Umverteilungen ----------

/// <summary>
/// Transfer zwischen Konten (Kernfunktion 4); optionale zweckgebundene
/// Verteilung auf Pools und Verknüpfung zu einer offenen
/// Ausgleichsposition.
/// </summary>
public sealed record TransferCommand(
    string Id,
    string Date,
    string FromAccountId,
    string ToAccountId,
    long Amount,
    IReadOnlyList<PoolAllocationCommand>? Purposes,
    string? RelatedSettlementId,
    string? Note);

/// <summary>Ein Konten-Anteil einer Umverteilung.</summary>
public sealed record AccountAllocationCommand(string AccountId, long Amount);

/// <summary>
/// Umverteilung innerhalb des Poolsystems (Kernfunktion 4); optionale
/// Konten-Zuordnung je Anteil.
/// </summary>
public sealed record ReassignmentCommand(
    string Id,
    string Date,
    string FromPoolId,
    string ToPoolId,
    long Amount,
    IReadOnlyList<AccountAllocationCommand>? Accounts,
    string? Note);

/// <summary>
/// Transfer-Umverteilung (Kernfunktion 4): Umverteilung, die zugleich
/// Konten bewegt.
/// </summary>
public sealed record TransferReassignmentCommand(
    string Id,
    string Date,
    string FromAccountId,
    string ToAccountId,
    string FromPoolId,
    string ToPoolId,
    long Amount,
    string? Note);

// ---------- Korrektur / Storno / Erstattung ----------

/// <summary>
/// Korrektur einer Ausgabe/Einnahme (Kernfunktion 6): nur gesetzte
/// Felder ändern sich (null = unverändert); mindestens ein Feld ist
/// Pflicht (Prüfung in der Domain). <c>ExpectedBaseRevision</c> ist
/// Pflicht (Rechenvertrag §5.5).
/// </summary>
public sealed record CorrectionCommand(
    string OperationId,
    int ExpectedBaseRevision,
    string? Date,
    string? Note,
    string? AccountId,
    long? TotalAmount,
    IReadOnlyList<ExpenseSplitCommand>? Splits,
    IReadOnlyList<IncomeAllocationCommand>? Allocations);

/// <summary>
/// Storno (Kernfunktion 6): Path-Id = Original, <c>Id</c> = stabile
/// Identität des Stornos, <c>ExpectedBaseRevision</c> des Originals.
/// </summary>
public sealed record ReversalCommand(
    string Id,
    int ExpectedBaseRevision,
    string Date,
    string? Note);

/// <summary>Ein Rückfluss-Zeile an einen Ursprungspool.</summary>
public sealed record RefundLineCommand(string PoolId, long Amount);

/// <summary>
/// Erstattung (Kernfunktion 6): Path-Id = Ursprungs-Ausgabe;
/// Gutschriftskonto darf anders sein als das Originalkonto; die
/// Zeilen fließen an die Ursprungspools (Deckelung serverseitig).
/// </summary>
public sealed record RefundCommand(
    string Id,
    string AccountId,
    string Date,
    IReadOnlyList<RefundLineCommand> Lines,
    string? Note);

/// <summary>
/// Kontostandskorrektur (Kernfunktion 6): dokumentierte <b>Differenz</b>
/// (signiert, ≠ 0), keine Saldo-Übernahme.
/// </summary>
public sealed record BalanceCorrectionCommand(
    string Id,
    string AccountId,
    long Amount,
    string Date,
    string Note);

// ---------- Antwort ----------

/// <summary>
/// Antwort aller Journal-Schreibendpunkte: Buchungsdetail plus
/// Idempotenz-Flag.
/// </summary>
public sealed record BookingCreatedDto(BookingDetailDto Booking, bool Idempotent);
