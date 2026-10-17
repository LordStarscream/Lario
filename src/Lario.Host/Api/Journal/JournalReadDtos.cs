namespace Lario.Host.Api.Journal;

/// <summary>
/// Journal-Lese-DTOs (Schritt 7, T3): reine Transportformen für
/// <c>/api/v1/bookings</c> (Liste + Detail), <c>/api/v1/balances</c> und
/// <c>/api/v1/settlements</c> (Design §2.2/§2.3).
///
/// Geldbeträge sind <b>long in Cent</b> (Rechenvertrag D6: integer-Cent
/// über JSON, keine Decimal/Float). Die Fachlogik (Filterung,
/// effectivelyReversed-Ableitung, Abgleich) liegt in der Domain /
/// Application-Schicht; hier wird nur übertragen.
///
/// Buchungs-Typen als stabile Strings (Enum-Namen):
/// <c>Income, Expense, OpeningBalance, AccountTransfer, TransferReassignment,
/// Reassignment, Reversal, Refund, BalanceCorrection</c>.
/// </summary>

/// <summary>
/// Liste-Eintrag (Design §3: Datum, Gesamtbetrag, Konto, Pools/Kategorien
/// der Splits, Steuerkennzeichnung, Status). Sortiert nach (Datum, Id)
/// absteigend. <see cref="TotalAmount"/>: Betrag der Buchung in Cent —
/// bei Income/Einnahme-arten positiv, bei Korrekturen/Stornos
/// vorzeichenbehaftet (Storno: negiert, BalanceCorrection: signiert).
/// </summary>
public sealed record BookingListItemDto(
    string Id,
    string Type,
    string Date,
    string? Note,
    long TotalAmount,
    string? AccountId,
    string? AccountName,
    IReadOnlyList<BookingSplitSummaryDto> Splits,
    IReadOnlyList<string> PoolNames,
    int BaseRevision,
    bool EffectivelyReversed);

/// <summary>
/// Split-Zusammenfassung für die Liste: Pool-/Kategorie-Namen und
/// Steuerkennzeichnung. <see cref="TaxRelevant"/> null = nicht markiert;
/// <see cref="TaxCategoryName"/> null = keine Steuerkategorie gewählt.
/// </summary>
public sealed record BookingSplitSummaryDto(
    long Amount,
    string? PoolName,
    string? CategoryName,
    bool? TaxRelevant,
    string? TaxCategoryName);

/// <summary>
/// Buchungsdetail (Design §3: Splits mit Betrag, Pool, Kategorie und
/// Tax-Kennzeichnung, Konten-/Poolposten, Transfer-Zwecke,
/// Revisionsstatus). Revisionsstatus (Kette Original/Korrektur/Storno):
/// <see cref="BaseRevision"/> &gt; 1 = die Buchung wurde in-place
/// korrigiert (Revisionsgeschichte vorhanden);
/// <see cref="ReversesBookingId"/> = diese Buchung ist ein Storno/Rückgängig
/// für diese Buchung; <see cref="ReversalBookingIds"/> = direkt verknüpfte
/// Storno-/Rückgängig-Buchungen; <see cref="EffectivelyReversed"/> = die
/// Buchung ist wirksam storniert; <see cref="RefundsExpenseId"/> = diese
/// Buchung ist eine Erstattung für diese Ausgabe.
///
/// <see cref="RefundedByPool"/> (Review R3 P2): bereits gewährte
/// Erstattungsbeträge pro Pool (Summe aller Erstattungs-Buchungen zu
/// dieser Ausgabe) — daraus rechnet der Client den verbleibenden
/// erstattungsfähigen Deckel (Split-Position − bereits erstattet),
/// statt den vollen Splitbetrag erneut als Deckel vorzusetzen.
/// </summary>
public sealed record BookingDetailDto(
    string Id,
    string Type,
    string Date,
    string? Note,
    int BaseRevision,
    bool EffectivelyReversed,
    string? FundingMonth,
    string? ReversesBookingId,
    IReadOnlyList<string> ReversalBookingIds,
    string? RefundsExpenseId,
    string? RelatedSettlementId,
    IReadOnlyList<AccountPostingDto> AccountPostings,
    IReadOnlyList<PoolPostingDto> PoolPostings,
    IReadOnlyList<ExpenseSplitDto> Splits,
    IReadOnlyList<PoolAllocationDto> TransferPurposes,
    IReadOnlyList<AccountAllocationDto> ReassignmentAccounts,
    long TotalAmount,
    IReadOnlyList<RefundedPoolDto> RefundedByPool);

/// <summary>
/// Bereits gewährte Erstattung eines Ursprungs-Pools (Review R3 P2):
/// <see cref="Amount"/> in Cent, Summe aller Erstattungs-Buchungen mit
/// <c>RefundsExpenseId</c> = Ursprungs-Ausgabe je Pool (Erstattungen sind
/// nicht stornierbar — die Summe ist damit ohne Kettengrundsatz).
/// </summary>
public sealed record RefundedPoolDto(string PoolId, long Amount);

/// <summary>Kontenposten: <see cref="Amount"/> in Cent (vorzeichenbehaftet).</summary>
public sealed record AccountPostingDto(string AccountId, string? AccountName, long Amount);

/// <summary>Poolposten: <see cref="Amount"/> in Cent (vorzeichenbehaftet).</summary>
public sealed record PoolPostingDto(string PoolId, string? PoolName, long Amount);

/// <summary>
/// Ausgabesplit (Detail): Betrag in Cent, Pool, Kategorie,
/// Steuerkennzeichnung. <see cref="Tax"/> null = keine Markierung.
/// </summary>
public sealed record ExpenseSplitDto(
    string Id,
    long Amount,
    string PoolId,
    string? PoolName,
    string CategoryId,
    string? CategoryName,
    TaxMarkingDto? Tax,
    string? Note);

/// <summary>
/// Steuerkennzeichnung eines Splits: <see cref="Relevant"/> true =
/// steuerlich relevant; <see cref="TaxCategoryId"/> null = keine gewählt;
/// <see cref="MarkedAmount"/> in Cent (null = nicht gesetzt, Display: „–").
/// </summary>
public sealed record TaxMarkingDto(
    bool Relevant,
    string? TaxCategoryId,
    string? TaxCategoryName,
    long? MarkedAmount,
    string? Note);

/// <summary>Transfer-Zweck (Pool-Position): <see cref="Amount"/> in Cent.</summary>
public sealed record PoolAllocationDto(string PoolId, string? PoolName, long Amount);

/// <summary>Konto-Position einer Umverteilung: <see cref="Amount"/> in Cent.</summary>
public sealed record AccountAllocationDto(string AccountId, string? AccountName, long Amount);

/// <summary>Kontensaldo (BalanceQueryService): <see cref="Balance"/> in Cent.</summary>
public sealed record AccountBalanceDto(
    string AccountId,
    string Name,
    string Status,
    long Balance);

/// <summary>Poolstand (BalanceQueryService): <see cref="Balance"/> in Cent.</summary>
public sealed record PoolBalanceDto(
    string PoolId,
    string Name,
    string Status,
    long Balance);

/// <summary>
/// Nicht zugeordneter Anfangsbestand (BalanceQueryService):
/// <see cref="Unallocated"/> in Cent (Restbestand ohne Poolzuordnung).
/// </summary>
public sealed record UnallocatedOpeningBalanceDto(
    string OpeningBalanceId,
    string Date,
    string AccountId,
    string? AccountName,
    long Unallocated);

/// <summary>
/// Kontostand (BalanceQueryService): Konten-, Pool- und Gesamtsalden in
/// Cent, <see cref="AccountPoolGap"/> = Kontensaldo − Poolsumme (0 =
/// korrekt abgegrenzt), offene nicht zugeordnete Anfangsbestände.
/// </summary>
public sealed record BalancesDto(
    IReadOnlyList<AccountBalanceDto> Accounts,
    IReadOnlyList<PoolBalanceDto> Pools,
    long TotalAccountBalance,
    long TotalPoolBalance,
    long TotalIncome,
    long AccountPoolGap,
    IReadOnlyList<UnallocatedOpeningBalanceDto> UnallocatedOpeningBalances);

/// <summary>
/// Ausgleichsposition (SettlementQueryService):
/// <see cref="Amount"/>/ <see cref="RestAmount"/> in Cent,
/// <see cref="Status"/> = <c>open|settled</c>.
/// </summary>
public sealed record SettlementDto(
    string Id,
    string SourceAccountId,
    string SourceAccountName,
    string TargetPoolId,
    string TargetPoolName,
    long Amount,
    long RestAmount,
    string Status,
    string CreatedByBookingId,
    string CreatedDate,
    string? SettledByBookingId,
    string? SettledDate);
