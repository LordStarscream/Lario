using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;

namespace Lario.Application.Journal;

/// <summary>
/// Anwendungsfall „Umwidmung erfassen" (Schritt 5, Rechenvertrag §3.4):
/// - verlangt eine stabile Wiederholungsidentität (Id);
/// - Idempotenz zuerst wie bei den anderen Vorgangstypen;
/// - bei Neuanlage: Katalog-Validierung (aktive Pools, gleicher Betrag).
///   Die Umwidmung ist reine Zweckänderung — kein Konto ist beteiligt,
///   keine Kontobewegung (Rechenvertrag §3.1). Es wird bewusst nicht
///   geprüft, dass der Quellpool den Betrag trägt: dadurch kann eine
///   Poolzuweisung sichtbar negativ werden, und genau das begründet eine
///   interne Ausgleichsposition (Rechenvertrag §3.5). Negative Stände
///   bleiben sichtbar und werden nicht versteckt.
/// - die Kontoverteilung der umgewidmeten Mittel ist Teil der Buchung
///   (Rechenvertrag §3.4): explizit angegeben oder — wenn null — implizit
///   aus dem Journal aufgelöst (der Quellpool wird eindeutig von genau
///   einem Konto getragen, das den Betrag trägt; sonst ist die
///   Verteilung mehrdeutig und muss explizit angegeben werden);
/// - Buchung und Ausgleichseffekte werden atomar gespeichert.
/// </summary>
public sealed class ReassignmentService(
    IMasterDataRepository masterDataRepository,
    IBookingRepository bookingRepository,
    ISettlementPositionRepository settlementPositionRepository)
{
    /// <summary>Ergebnis einer Anlage: <see cref="Created"/> ist false bei Idempotenz-Treffer.</summary>
    public sealed record Result(bool Created, Booking Booking);

    public async Task<Result> CreateAsync(
        ReassignmentSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (string.IsNullOrWhiteSpace(spec.Id))
        {
            throw new LarioDomainException(
                "Umwidmung ohne Id: eine stabile Wiederholungsidentität ist erforderlich.");
        }

        var catalog = await masterDataRepository.LoadCatalogAsync(cancellationToken);

        var existing = await bookingRepository.FindByIdAsync(spec.Id, cancellationToken);
        if (existing is not null)
        {
            if (existing.Type != BookingType.Reassignment)
            {
                throw new DomainConflictException(
                    $"Die Id '{spec.Id}' wird bereits von einer Buchung des Typs " +
                    $"{existing.Type} verwendet.");
            }

            if (!SpecMatchesBooking(spec, existing, catalog))
            {
                throw new DomainConflictException(
                    $"Für die Umwidmung '{spec.Id}' existiert bereits eine Buchung " +
                    "mit anderem Inhalt. Gleiches Id, anderer Inhalt ist ein Konflikt — " +
                    "die vorhandene Buchung bleibt unverändert.");
            }

            return new Result(false, existing);
        }

        // Tatsächliche Neuanlage: aktive Stammdaten.
        spec.Validate(catalog);

        var bookings = await bookingRepository.GetAllAsync(cancellationToken);
        var persistedPositions = await settlementPositionRepository.GetAllAsync(cancellationToken);
        var journal = new BookingJournal(bookings);

        // Explizite Kontoverteilung wurde bereits in spec.Validate(catalog)
        // geprüft (Splitregel, aktive Konten); implizit wird sie aus dem
        // Journal aufgelöst (Rechenvertrag §5.2).
        var accounts = spec.Accounts is null
            ? ResolveImplicitAccounts(spec, journal)
            : spec.Accounts.ToList();

        var booking = spec.ToBooking(accounts);

        var changes = SettlementPositionSync.Plan(
            bookings, booking, persistedPositions,
            () => $"set-{Guid.NewGuid():N}");

        await bookingRepository.AddAsync(
            booking, changes.Select(c => c.Row).ToList(), cancellationToken);
        return new Result(true, booking);
    }

    /// <summary>
    /// Implizite Kontoverteilung (Rechenvertrag §5.2): die umgewidmeten
    /// Mittel liegen physisch auf den Konten, die den Quellpool tragen.
    /// Die Verteilung ist eindeutig, wenn genau ein Konto eine positive
    /// Zelle für den Quellpool hat und diese den Betrag trägt. Mehr als
    /// ein Trägerkonto oder ein Träger, der den Betrag nicht trägt,
    /// ist mehrdeutig — der Aufrufer muss die Konten explizit angeben.
    /// </summary>
    private static List<AccountAllocation> ResolveImplicitAccounts(
        ReassignmentSpec spec, BookingJournal journal)
    {
        var carriers = journal.Allocations()
            .Where(c => c.PoolId == spec.FromPoolId && c.Amount.Cents > 0)
            .ToList();

        if (carriers.Count != 1 || carriers[0].Amount.Cents < spec.Amount.Cents)
        {
            throw new LarioDomainException(
                $"Umwidmung '{spec.Id}' mit impliziter Kontoverteilung ist mehrdeutig: " +
                $"der Quellpool '{spec.FromPoolId}' wird nicht genau von einem Konto getragen, " +
                "das den Betrag trägt. Bitte die Kontoverteilung explizit angeben.");
        }

        return [new AccountAllocation(carriers[0].AccountId, spec.Amount)];
    }

    /// <summary>
    /// Prüft, ob die Vorgabe exakt den Inhalt der vorhandenen Buchung
    /// hat: Fachdatum, Quell- und Zielpool, Betrag, Notiz und die
    /// Kontoverteilung. Die Kontoverteilung wird wie folgt verglichen:
    /// - explizite Verteilung der Vorgabe: 1:1 als Multiset (Konto, Betrag);
    /// - implizite Vorgabe (keine Zeilen): die gespeicherte Verteilung wird
    ///   akzeptiert, wenn sie zur Buchung passt (Summe = Betrag, aktive
    ///   Konten). Die exakte implizite Ableitung ist im Wiederholungszeitpunkt
    ///   nicht rekonstruierbar, weil spätere Buchungen die Zellaufteilung
    ///   verändert haben können — die gespeicherten Zeilen sind die
    ///   materialisierte Auflösung der Erstanlage.
    /// </summary>
    private static bool SpecMatchesBooking(ReassignmentSpec spec, Booking booking, MasterDataCatalog catalog)
    {
        if (booking.Date != spec.Date || booking.Note != spec.Note)
        {
            return false;
        }

        if (booking.AccountPostings.Count != 0 || booking.PoolPostings.Count != 2)
        {
            return false;
        }

        var outflow = booking.PoolPostings.FirstOrDefault(p => p.Amount.Cents < 0);
        var inflow = booking.PoolPostings.FirstOrDefault(p => p.Amount.Cents > 0);

        if (outflow is null || inflow is null
            || outflow.Amount != spec.Amount.Negate()
            || inflow.Amount != spec.Amount
            || outflow.PoolId != spec.FromPoolId
            || inflow.PoolId != spec.ToPoolId)
        {
            return false;
        }

        var storedAccounts = booking.ReassignmentAccounts;
        if (storedAccounts.Count == 0)
        {
            return false;
        }

        if (spec.Accounts is null)
        {
            // Implizite Wiederholung: die gespeicherte Verteilung muss zur
            // Buchung passen (Summe, aktive Konten).
            var storedSum = Money.Zero;
            foreach (var stored in storedAccounts)
            {
                storedSum = storedSum.Add(stored.Amount);
            }

            return storedSum == spec.Amount
                && storedAccounts.All(a =>
                    catalog.FindAccount(a.AccountId)?.Status == EntityStatus.Active);
        }

        // Explizite Verteilung: 1:1 als Multiset (Konto, Betrag).
        if (storedAccounts.Count != spec.Accounts.Count)
        {
            return false;
        }

        var remaining = storedAccounts.ToList();
        foreach (var line in spec.Accounts)
        {
            var index = remaining.FindIndex(p =>
                p.AccountId == line.AccountId && p.Amount == line.Amount);
            if (index < 0)
            {
                return false;
            }

            remaining.RemoveAt(index);
        }

        return true;
    }
}
