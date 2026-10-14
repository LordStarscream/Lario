using Lario.Domain;
using Lario.Domain.Journal;

namespace Lario.Application.Journal;

/// <summary>
/// Anwendungsfall „Erstattung" (Schritt 6, Rechenvertrag §4/§5.3, Design
/// Schritt 6 §2.3/§3.2):
/// - eine Erstattung ist eine normale Buchung vom Typ
///   <see cref="BookingType.Refund"/>: Konto-Posting + Gesamtbetrag
///   (Gutschrift) und Pool-Postings + Zeilenbetrag je Ursprungs-Pool
///   (eine Erstattungslinie pro Pool), verknüpft über
///   <c>RefundsExpenseId</c> — sie ist vom Storno strikt getrennt
///   (Plan-Abnahme B2: keine Erstattungs-Mechanik im Storno, kein
///   Storno-Verhalten bei Erstattungen);
/// - kein Revisionsimpuls: die Erstattung ändert das Original nicht
///   (kein <c>ExpectedBaseRevision</c>) — sie ist eine neue, eigenständige
///   Buchung (Rechenvertrag §5.3, Design Schritt 6 §3.2);
/// - Deckelung pro Pool: bereits gewährte Erstattungen (aus dem
///   Journal) plus neue Linie dürfen die Split-Position des Originals
///   je Pool nicht überschreiten (Rechenvertrag §5.3, Design Schritt 6
///   §3.2) — Überschreitung ist sichtbar als <see cref="LarioDomainException"/>;
/// - eine Erstattung zu einer stornierten Ausgabe ist ausgeschlossen
///   (es gibt nichts zu erstatten) — B2;
/// - die Erstattungs-Deltas können offene Begleichungspositionen
///   (Teil-)begleichen (z. B. Erstattung auf dem Original-Pool) — die
///   Begleichungspositionen werden atomar mit der Erstattung
///   nachgeführt (Design Schritt 6 §3.3);
/// - Idempotenz zuerst: gleiche Id mit gleichem Inhalt wird nur
///   quittiert, gleiche Id mit anderem Inhalt oder Typ ist ein
///   Konflikt.
/// </summary>
public sealed class RefundService(
    IBookingRepository bookingRepository,
    ISettlementPositionRepository settlementPositionRepository,
    IMasterDataRepository masterDataRepository)
{
    /// <summary>
    /// Ergebnis einer Erstattung: <see cref="Created"/> ist true, wenn die
    /// Erstattung als neue Buchung angelegt wurde; false bei
    /// Idempotenz-Treffer (die vorhandene Buchung wird zurückgegeben).
    /// </summary>
    public sealed record Result(bool Created, Booking Booking);

    public async Task<Result> RegisterAsync(
        RefundSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // Idempotenz zuerst.
        var existing = await bookingRepository.FindByIdAsync(spec.Id, cancellationToken);
        if (existing is not null)
        {
            if (existing.Type != BookingType.Refund)
            {
                throw new DomainConflictException(
                    $"Buchungs-Id '{spec.Id}' ist bereits vergeben (Typ {existing.Type}) — " +
                    "Erstattungen müssen eine freie Id verwenden.");
            }

            if (!IsSameContent(existing, spec))
            {
                throw new DomainConflictException(
                    $"Für die Buchung '{spec.Id}' existiert bereits eine Buchung mit " +
                    "anderem Inhalt. Gleiches Id, anderer Inhalt ist ein Konflikt — die " +
                    "vorhandene Buchung bleibt unverändert.");
            }

            return new Result(false, existing);
        }

        // Original-Ausgabe laden (fehlend → klarer Fehler).
        var original = await bookingRepository.FindByIdAsync(spec.OriginalExpenseId, cancellationToken)
            ?? throw new InvalidReferenceException(
                $"Erstattung '{spec.Id}': die Ausgabe '{spec.OriginalExpenseId}' existiert nicht.");

        var journal = await bookingRepository.GetAllAsync(cancellationToken);

        // Eine Erstattung setzt eine gültige Ausgabe voraus — eine
        // stornierte Ausgabe hat nichts zu erstatten (B2).
        if (journal.Any(b => b.Type == BookingType.Reversal && b.ReversesBookingId == original.Id))
        {
            throw new LarioDomainException(
                $"Erstattung '{spec.Id}': die Ausgabe '{original.Id}' ist storniert — " +
                "es gibt nichts zu erstatten. Widerrufen Sie das Storno zuerst (Storno des " +
                "Stornos), dann ist die Erstattung möglich.");
        }

        // Validierung wie Neuanlage (Katalog, Typ, Zeilen gegen
        // Ursprungs-Pools) — Design Schritt 6 §2.3.
        var catalog = await masterDataRepository.LoadCatalogAsync(cancellationToken);
        spec.Validate(catalog, original);

        // Deckelung pro Pool: bereits gewährte Erstattungen (Journal)
        // plus neue Linie <= Split-Position des Originals (Rechenvertrag
        // §5.3, Design Schritt 6 §3.2).
        foreach (var line in spec.Lines)
        {
            var granted = journal
                .Where(b => b.Type == BookingType.Refund && b.RefundsExpenseId == original.Id)
                .SelectMany(b => b.PoolPostings)
                .Where(p => p.PoolId == line.PoolId)
                .Aggregate(Money.Zero, (sum, p) => sum.Add(p.Amount));

            var splitAmount = SplitAmountFor(original, line.PoolId);
            if (granted.Add(line.Amount) > splitAmount)
            {
                throw new LarioDomainException(
                    $"Erstattung '{spec.Id}': die Pool-Erstattung {line.Amount.ToEuro():0.00} € " +
                    $"für Pool '{line.PoolId}' übersteigt die Split-Position der Ausgabe " +
                    $"({splitAmount.ToEuro():0.00} € aufgeteilt, davon {granted.ToEuro():0.00} € " +
                    "bereits erstattet). Die Deckelung gilt pro Pool (Rechenvertrag §5.3).");
            }
        }

        var booking = spec.ToBooking(original);

        // Begleichungspositionen atomar mit der Erstattung nachführen
        // (die Deltas können offene Positionen (Teil-)begleichen).
        var persistedPositions = await settlementPositionRepository.GetAllAsync(cancellationToken);
        var changes = SettlementPositionSync.Plan(
            journal, booking, persistedPositions,
            () => $"set-{Guid.NewGuid():N}");

        // Normale neue Buchung — KEIN Revisionsimpuls auf das Original.
        await bookingRepository.AddAsync(
            booking, changes.Select(c => c.Row).ToList(), cancellationToken);
        return new Result(true, booking);
    }

    /// <summary>
    /// Split-Position des Originals für einen Pool: der Betrag, den die
    /// Ausgabe aus dem Pool gezahlt hat (negatives Pool-Posting wird als
    /// positiver Betrag ausgegeben). Ohne Pool-Posting: 0 (kann nicht
    /// vorkommen — Validate prüft Pools ⊆ Ursprungs-Pools).
    /// </summary>
    private static Money SplitAmountFor(Booking expense, string poolId) =>
        expense.PoolPostings
            .FirstOrDefault(p => p.PoolId == poolId)?.Amount.Negate()
            ?? Money.Zero;

    /// <summary>
    /// Inhaltliche Gleichheit einer vorhandenen Erstattung mit der
    /// Spezifikation (Idempotenz-Abgleich: gleiche Id, gleicher Inhalt →
    /// Quittierung). Der Gesamtbetrag ist die Summe der Zeilen.
    /// </summary>
    private static bool IsSameContent(Booking stored, RefundSpec spec)
    {
        if (stored.RefundsExpenseId != spec.OriginalExpenseId
            || stored.Date != spec.Date
            || stored.Note != spec.Note)
        {
            return false;
        }

        var total = spec.Lines
            .Aggregate(Money.Zero, (sum, line) => sum.Add(line.Amount));

        // Ein Konto-Posting (Gutschrift): Konto und Gesamtbetrag.
        var accountPostings = stored.AccountPostings.ToList();
        if (accountPostings.Count != 1
            || accountPostings[0].AccountId != spec.AccountId
            || accountPostings[0].Amount != total)
        {
            return false;
        }

        // Pool-Postings 1:1 mit den Erstattungslinien.
        var storedPools = stored.PoolPostings.ToDictionary(p => p.PoolId, p => p.Amount);
        if (storedPools.Count != spec.Lines.Count)
        {
            return false;
        }

        return spec.Lines.All(l => storedPools.TryGetValue(l.PoolId, out var amount) && amount == l.Amount);
    }
}
