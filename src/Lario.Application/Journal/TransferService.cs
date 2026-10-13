using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;

namespace Lario.Application.Journal;

/// <summary>
/// Anwendungsfall „Transfer zwischen Konten erfassen" (Schritt 5,
/// Rechenvertrag §3.4):
/// - verlangt eine stabile Wiederholungsidentität (Id);
/// - Idempotenz zuerst: dieselbe Id mit gleichem Inhalt wird nicht erneut
///   gespeichert, sondern die vorhandene Buchung zurückgegeben — auch
///   wenn die beteiligten Konten/Pools inzwischen archiviert wurden;
///   gleicher Id mit anderem Inhalt ist ein Konflikt; eine Id, die
///   bereits eine Buchung eines anderen Typs trägt, ebenfalls;
/// - bei Neuanlage: Katalog-Validierung, dann Journalprüfungen:
///   der Quellkontostand trägt den Betrag; die Zweckzuordnung ist
///   entweder explizit (Summe = Betrag, aktive Pools) oder implizit aus
///   der Zellaufteilung des Quellkontos ableitbar (exakt eine positive
///   Zelle, die den Betrag trägt) — mehrdeutig ist ein Fehler;
/// - optionale Begleichung eines offenen internen Ausgleichs
///   (relatedSettlementId): die Position muss offen sein; das Geld muss
///   in das Konto der Position fließen (Zielkonto des Transfers), und die
///   Zweckverteilung muss den Pool der Position enthalten — so steigt
///   genau die Schuldzelle (Referenzfall 4: Freizeit → Haupt, Zweck
///   pool-freizeit);
/// - Buchung und Ausgleichseffekte werden atomar gespeichert
///   (Rechenvertrag §3.2/§3.5).
/// </summary>
public sealed class TransferService(
    IMasterDataRepository masterDataRepository,
    IBookingRepository bookingRepository,
    ISettlementPositionRepository settlementPositionRepository)
{
    /// <summary>Ergebnis einer Anlage: <see cref="Created"/> ist false bei Idempotenz-Treffer.</summary>
    public sealed record Result(bool Created, Booking Booking);

    public async Task<Result> CreateAsync(
        TransferSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (string.IsNullOrWhiteSpace(spec.Id))
        {
            throw new LarioDomainException(
                "Transfer ohne Id: eine stabile Wiederholungsidentität ist erforderlich.");
        }

        var catalog = await masterDataRepository.LoadCatalogAsync(cancellationToken);

        var existing = await bookingRepository.FindByIdAsync(spec.Id, cancellationToken);
        if (existing is not null)
        {
            if (existing.Type != BookingType.AccountTransfer)
            {
                throw new DomainConflictException(
                    $"Die Id '{spec.Id}' wird bereits von einer Buchung des Typs " +
                    $"{existing.Type} verwendet.");
            }

            if (!SpecMatchesBooking(spec, existing, catalog))
            {
                throw new DomainConflictException(
                    $"Für den Transfer '{spec.Id}' existiert bereits eine Buchung " +
                    "mit anderem Inhalt. Gleiches Id, anderer Inhalt ist ein Konflikt — " +
                    "die vorhandene Buchung bleibt unverändert.");
            }

            return new Result(false, existing);
        }

        // Tatsächliche Neuanlage: aktive Stammdaten und Journalprüfungen.
        spec.Validate(catalog);

        var bookings = await bookingRepository.GetAllAsync(cancellationToken);
        var persistedPositions = await settlementPositionRepository.GetAllAsync(cancellationToken);
        var journal = new BookingJournal(bookings);

        // Der Quellkontostand muss den Betrag tragen — interne Transfers
        // sind keine Einkommen und vergrößern nichts.
        var sourceBalance = journal.AccountBalance(spec.FromAccountId);
        if (sourceBalance.Cents < spec.Amount.Cents)
        {
            throw new LarioDomainException(
                $"Transfer '{spec.Id}': der Stand des Quellkontos " +
                $"'{spec.FromAccountId}' ({sourceBalance.ToEuro():0.00} €) trägt den Betrag " +
                $"({spec.Amount.ToEuro():0.00} €) nicht.");
        }

        // Explizite Zweckverteilung wurde bereits in spec.Validate(catalog)
        // geprüft (Splitregel, aktive Pools); implizit wird sie aus dem
        // Journal aufgelöst.
        var purposes = spec.Purposes is null
            ? ResolveImplicitPurposes(spec, journal)
            : spec.Purposes.ToList();

        if (spec.RelatedSettlementId is not null)
        {
            ValidateSettlementLink(spec, purposes, journal, persistedPositions);
        }

        var booking = spec.ToBooking(purposes);

        var changes = SettlementPositionSync.Plan(
            bookings, booking, persistedPositions,
            () => $"set-{Guid.NewGuid():N}");

        await bookingRepository.AddAsync(
            booking, changes.Select(c => c.Row).ToList(), cancellationToken);
        return new Result(true, booking);
    }

    /// <summary>
    /// Prüft, ob die Vorgabe exakt den Inhalt der vorhandenen Buchung
    /// hat: Fachdatum, Konten, Betrag, Notiz und Begleichungsreferenz
    /// müssen stimmen. Die Zweckzuordnung wird wie folgt verglichen:
    /// - die Herkunft der Zuordnung (persistiert als
    ///   <c>AllocationExplicit</c>, analog <c>TaxMarking.InheritedFromPreset</c>)
    ///   muss zusammenpassen: gespeicherte explizite Herkunft bestätigt
    ///   nur eine explizite Wiederholung, gespeicherte implizite Herkunft
    ///   nur eine implizite — eine explizite Wiederholung einer implizit
    ///   aufgelösten Erstanlage ist ein Inhaltswechsel, auch wenn die
    ///   Zeilen der gespeicherten Auflösung entsprechen; fehlende Herkunft
    ///   (Daten dieser Version) → struktureller Vergleich ohne
    ///   Herkunftsprüfung (Upgrade-Kompatibilität);
    /// - explizite Zuordnung der Vorgabe: 1:1 mit den gespeicherten
    ///   Zeilen (Pool, Betrag);
    /// - implizite Vorgabe (keine Zeilen): die gespeicherte Zuordnung
    ///   wird akzeptiert, wenn sie zur Buchung passt (Summe = Betrag,
    ///   aktive Pools). Die exakte implizite Ableitung ist im
    ///   Wiederholungszeitpunkt nicht rekonstruierbar, weil spätere
    ///   Buchungen die Zellaufteilung des Quellkontos verändert haben
    ///   können — die gespeicherten Zeilen sind die materialisierte
    ///   Auflösung der Erstanlage.
    /// </summary>
    private static bool SpecMatchesBooking(
        TransferSpec spec, Booking booking, MasterDataCatalog catalog)
    {
        if (booking.Date != spec.Date || booking.Note != spec.Note)
        {
            return false;
        }

        if (booking.AccountPostings.Count != 2)
        {
            return false;
        }

        var outflow = booking.AccountPostings
            .FirstOrDefault(p => p.Amount.Cents < 0);
        var inflow = booking.AccountPostings
            .FirstOrDefault(p => p.Amount.Cents > 0);

        if (outflow is null || inflow is null
            || outflow.Amount != spec.Amount.Negate()
            || inflow.Amount != spec.Amount
            || outflow.AccountId != spec.FromAccountId
            || inflow.AccountId != spec.ToAccountId)
        {
            return false;
        }

        if (!string.Equals(
            booking.RelatedSettlementId, spec.RelatedSettlementId,
            StringComparison.Ordinal))
        {
            return false;
        }

        var storedPurposes = booking.TransferPurposes;
        if (storedPurposes.Count == 0)
        {
            return false;
        }

        // Herkunft der Zuordnung (siehe Methodenkommentar).
        if (booking.AllocationExplicit is true && spec.Purposes is null)
        {
            return false;
        }
        if (booking.AllocationExplicit is false && spec.Purposes is not null)
        {
            return false;
        }

        if (spec.Purposes is null)
        {
            // Implizite Wiederholung: die gespeicherte Zuordnung muss zur
            // Buchung passen (Summe, aktive Pools).
            var storedSum = Money.Zero;
            foreach (var stored in storedPurposes)
            {
                storedSum = storedSum.Add(stored.Amount);
            }

            return storedSum == spec.Amount
                && storedPurposes.All(p =>
                    catalog.FindPool(p.PoolId)?.Status == EntityStatus.Active);
        }

        if (storedPurposes.Count != spec.Purposes.Count)
        {
            return false;
        }

        // Multiset-Vergleich: jede Vorgabezeile muss exakt eine
        // gespeicherte Zeile (Pool, Betrag) und umgekehrt abbilden.
        var remaining = storedPurposes.ToList();
        foreach (var line in spec.Purposes)
        {
            var index = remaining.FindIndex(p =>
                p.PoolId == line.PoolId && p.Amount == line.Amount);
            if (index < 0)
            {
                return false;
            }

            remaining.RemoveAt(index);
        }

        return remaining.Count == 0;
    }

    /// <summary>
    /// Implizite Zweckzuordnung (Rechenvertrag §3.4): die Zuordnung ist
    /// aus dem Quellkonto ableitbar, wenn es exakt eine positive
    /// Poolzuweisung trägt und diese den Transferbetrag trägt. Mehr als
    /// eine positive Zelle oder keine ausreichende Zelle ist
    /// mehrdeutig — der Aufrufer muss die Zwecke explizit angeben.
    /// </summary>
    private static List<PoolAllocation> ResolveImplicitPurposes(
        TransferSpec spec, BookingJournal journal)
    {
        var sourceCells = journal.Allocations()
            .Where(c => c.AccountId == spec.FromAccountId && c.Amount.Cents > 0)
            .ToList();

        if (sourceCells.Count != 1 || sourceCells[0].Amount.Cents < spec.Amount.Cents)
        {
            throw new LarioDomainException(
                $"Transfer '{spec.Id}' mit impliziter Zweckzuordnung ist mehrdeutig: " +
                $"das Quellkonto '{spec.FromAccountId}' trägt nicht genau eine positive " +
                "Poolzuweisung, die den Betrag trägt. Bitte die Zweckzuordnung " +
                "explizit angeben.");
        }

        return [new PoolAllocation(sourceCells[0].PoolId, spec.Amount)];
    }

    /// <summary>
    /// Prüft die Begleichungsreferenz (Rechenvertrag §3.5): die Position
    /// muss existieren und offen sein; das Geld muss in das Konto der
    /// Position fließen (Zielkonto des Transfers = Konto der Position),
    /// denn nur so steigt die Schuldzelle (Konto, Pool) und die Schuld
    /// schrumpft; die Zweckzuordnung muss den Pool der Position
    /// enthalten, denn nur mit diesem Zweck trägt der Transfer die
    /// Mittel dem Pool zu.
    /// </summary>
    private static void ValidateSettlementLink(
        TransferSpec spec,
        List<PoolAllocation> purposes,
        BookingJournal journal,
        IReadOnlyList<SettlementPosition> persistedPositions)
    {
        var position = persistedPositions
            .FirstOrDefault(p => p.Id == spec.RelatedSettlementId);

        if (position is null)
        {
            throw new InvalidReferenceException(
                $"Transfer '{spec.Id}' referenziert unbekannte Ausgleichsposition " +
                $"'{spec.RelatedSettlementId}'.");
        }

        if (position.Status != SettlementStatus.Offen)
        {
            throw new DomainConflictException(
                $"Transfer '{spec.Id}': die Ausgleichsposition '{position.Id}' ist " +
                "bereits beglichen und kann nicht erneut beglichen werden.");
        }

        if (position.SourceAccountId != spec.ToAccountId)
        {
            throw new LarioDomainException(
                $"Transfer '{spec.Id}': die Ausgleichsposition '{position.Id}' gehört " +
                $"zum Konto '{position.SourceAccountId}', der Transfer fließt aber " +
                $"in '{spec.ToAccountId}': nur ein Zufluss in das konto der Position " +
                "begleicht die Vorfinanzierung.");
        }

        if (!purposes.Any(p => p.PoolId == position.TargetPoolId))
        {
            throw new LarioDomainException(
                $"Transfer '{spec.Id}': die Zweckzuordnung enthält den Quellpool " +
                $"'{position.TargetPoolId}' der Ausgleichsposition '{position.Id}' nicht.");
        }

        // Konsistenz mit dem abgeleiteten Zustand: die persistierte
        // Resthöhe muss zur abgeleiteten offenen Episode der Zelle passen
        // (eine Zelle trägt zu einem Zeitpunkt maximal eine offene Position).
        var episode = journal.DeriveSettlementPositions()
            .FirstOrDefault(e => e.Status == SettlementStatus.Offen
                && e.SourceAccountId == position.SourceAccountId
                && e.TargetPoolId == position.TargetPoolId);

        if (episode is null || episode.RestAmount != position.RestAmount)
        {
            throw new LarioDomainException(
                $"Integritätsfehler: die Ausgleichsposition '{position.Id}' stimmt " +
                "nicht mit dem abgeleiteten Journalzustand überein.");
        }
    }
}
