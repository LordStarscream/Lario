using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Application;
using Microsoft.EntityFrameworkCore;

namespace Lario.Infrastructure;

/// <summary>
/// EF-Core/SQLite-Implementierung des Buchungsjournals (Schritt 3).
/// <see cref="AddAsync"/> speichert Buchung und alle zusammengehörigen
/// Postings in einer Transaktion — eine Teilübernahme ist unmöglich
/// (Rechenvertrag §3.2).
/// </summary>
public sealed class BookingRepository(LarioDbContext db) : IBookingRepository
{
    public async Task<Booking?> FindByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var booking = await db.Bookings
            .Include(b => b.AccountPostings)
            .Include(b => b.PoolPostings)
            .Include(b => b.Splits)
            .ThenInclude(s => s.Tax)
            .Include(b => b.TransferPurposes)
            .Include(b => b.ReassignmentAccounts)
            .FirstOrDefaultAsync(b => b.Id == id, cancellationToken);

        if (booking is null)
        {
            return null;
        }

        await LoadReversalDeltasAsync([booking], cancellationToken);
        return booking;
    }

    public async Task<IReadOnlyList<Booking>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var bookings = await db.Bookings
            .Include(b => b.AccountPostings)
            .Include(b => b.PoolPostings)
            .Include(b => b.Splits)
            .ThenInclude(s => s.Tax)
            .Include(b => b.TransferPurposes)
            .Include(b => b.ReassignmentAccounts)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        await LoadReversalDeltasAsync(bookings, cancellationToken);
        return bookings;
    }

    public async Task AddAsync(Booking booking, CancellationToken cancellationToken = default)
    {
        var exists = await db.Bookings.AnyAsync(b => b.Id == booking.Id, cancellationToken);
        if (exists)
        {
            throw new InvalidOperationException(
                $"Buchung mit der Id '{booking.Id}' existiert bereits. " +
                "Die Idempotenzprüfung obliegt dem Anwendungsfall (OpeningBalanceService).");
        }

        db.Bookings.Add(booking);
        AddReversalDeltaRows(booking);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddAsync(
        Booking booking,
        IReadOnlyCollection<SettlementPosition> settlements,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settlements);

        var exists = await db.Bookings.AnyAsync(b => b.Id == booking.Id, cancellationToken);
        if (exists)
        {
            throw new InvalidOperationException(
                $"Buchung mit der Id '{booking.Id}' existiert bereits. " +
                "Die Idempotenzprüfung obliegt dem Anwendungsfall.");
        }

        db.Bookings.Add(booking);
        AddReversalDeltaRows(booking);
        await ApplySettlementChangesAsync(settlements, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Booking> AddWithOriginalRevisionAsync(
        Booking booking,
        Booking original,
        IReadOnlyCollection<SettlementPosition> settlements,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(settlements);

        if (string.IsNullOrWhiteSpace(original.Id))
        {
            throw new InvalidOperationException(
                "Revisionserhöhung ohne Original-Id — das verknüpfte Original fehlt.");
        }

        if (booking.Id == original.Id)
        {
            throw new InvalidOperationException(
                $"Die neue Buchung '{booking.Id}' darf nicht mit dem Original " +
                "identisch sein — Storno und Original sind zwei Buchungen.");
        }

        var exists = await db.Bookings.AnyAsync(b => b.Id == booking.Id, cancellationToken);
        if (exists)
        {
            throw new InvalidOperationException(
                $"Buchung mit der Id '{booking.Id}' existiert bereits. " +
                "Die Idempotenzprüfung obliegt dem Anwendungsfall.");
        }

        // Das Original wird in derselben Transaktion erhöht: über den
        // Identitätsabgleich ist dies die vom Anwendungsfall geladene (und
        // bereits revisiongeprüfte) Instanz — andernfalls die (neu
        // geladene) Instanz aus dem Kontext.
        var trackedOriginal = await db.Bookings
            .FirstOrDefaultAsync(b => b.Id == original.Id, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Das Original '{original.Id}' existiert nicht mehr — die Revisionserhöhung " +
                "kann nicht erfolgen.");

        // Der Storno wird erst wirksam, wenn das Original seine neue
        // Revision trägt — beide Änderungen commiten gemeinsam (Design
        // Schritt 6 §2.5/§3.2). Die Revision ist ein Concurrency-Token
        // (Review R1 P1): commitet eine parallele Operation in der
        // Zwischenzeit, schlägt das UPDATE fehl und der gesamte
        // Vorgang (Buchung + Historie + Positionen) wird
        // zurückgerollt — als Revisionskonflikt sichtbar.
        // Review R2 P1 (B2): ein Storno verändert den wirksamen Status der
        // gesamten Kette über seinem Original — ein Widerruf reaktiviert
        // den Ast darüber. "Wirksamer Status + Revision" bilden daher ein
        // konsistentes Aggregat: alle Kettenvorfahren (das Original und,
        // wenn es selbst ein Storno ist, dessen Original, … bis zum
        // Wurzeloriginal) werden in derselben Transaktion um 1 erhöht —
        // jedes mit seinem eigenen Concurrency-Token. Jeder andere Kontext,
        // der die Revision eines Kettenglieds vor diesem Commit gelesen
        // hat, erhält beim eigenen Commit einen Revisionskonflikt und
        // damit auch den veralteten wirksamen Status abgelehnt.
        var chain = new List<Booking> { trackedOriginal };
        for (var current = trackedOriginal;
             current.Type == BookingType.Reversal && current.ReversesBookingId is { } parentId;)
        {
            var parent = await db.Bookings
                .FirstOrDefaultAsync(b => b.Id == parentId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Die Storno-Kette ist unterbrochen: '{current.Id}' verweist auf " +
                    $"die unbekannte Buchung '{parentId}' — die Revisionserhöhung " +
                    "kann nicht erfolgen.");
            chain.Add(parent);
            current = parent;
        }

        // Die erwarteten Revisionen aller Kettenglieder werden VOR der
        // Mutation erfasst: wenn ein Kettenglied dieselbe Instanz wie die
        // vom Anwendungsfall geladene Buchung ist, würde die Zuweisung
        // BaseRevision mitverändern und der Konfliktbericht die
        // inkorrekte (bereits erhöhte) Revision melden (Review R1 P1 /
        // B2: zwei Kontexte, genau einer gewinnt).
        var expectedRevisions = chain.ToDictionary(b => b.Id, b => b.BaseRevision);
        foreach (var ancestor in chain)
        {
            ancestor.BaseRevision = expectedRevisions[ancestor.Id] + 1;
        }

        db.Bookings.Add(booking);
        AddReversalDeltaRows(booking);
        await ApplySettlementChangesAsync(settlements, cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw await RevisionConflictAsync(chain, expectedRevisions, cancellationToken);
        }

        return await LoadFullAsync(booking.Id, cancellationToken);
    }

    public async Task<BookingCorrection?> FindCorrectionAsync(
        string operationId, CancellationToken cancellationToken = default)
    {
        return await db.BookingCorrections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.OperationId == operationId, cancellationToken);
    }

    public async Task<Booking> UpdateAsync(
        Booking updated,
        BookingCorrection correction,
        IReadOnlyCollection<SettlementPosition> settlements,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settlements);
        ArgumentNullException.ThrowIfNull(updated);
        ArgumentNullException.ThrowIfNull(correction);

        var tracked = await db.Bookings
            .Include(b => b.AccountPostings)
            .Include(b => b.PoolPostings)
            .Include(b => b.Splits)
            .Include(b => b.TransferPurposes)
            .Include(b => b.ReassignmentAccounts)
            .FirstOrDefaultAsync(b => b.Id == updated.Id, cancellationToken)
            ?? throw new InvalidOperationException(
                $"In-place-Korrektur für unbekannte Buchung '{updated.Id}' — " +
                "das Original existiert nicht.");

        // Der Typ bleibt; die übrigen skalaren Felder übernehmen den
        // geprüften Ersatzinhalt (Revisionserhöhung inklusive — sie kommt
        // aus dem Anwendungsfall).
        tracked.Date = updated.Date;
        tracked.Note = updated.Note;
        tracked.FundingMonth = updated.FundingMonth;
        tracked.BaseRevision = updated.BaseRevision;
        tracked.ReversesBookingId = updated.ReversesBookingId;
        tracked.RefundsExpenseId = updated.RefundsExpenseId;

        ReplaceChildren(tracked, updated);
        db.BookingCorrections.Add(correction);
        await ApplySettlementChangesAsync(settlements, cancellationToken);

        // Concurrency-Token (Review R1 P1): commitet eine parallele
        // Operation (Storno, Erstattung, zweite Korrektur) in der
        // Zwischenzeit, schlägt das UPDATE auf der Revision fehl und
        // Buchung, Historie und Positionen werden gemeinsam
        // zurückgerollt — als Revisionskonflikt sichtbar.
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw await RevisionConflictAsync(
                [tracked],
                new Dictionary<string, int> { [tracked.Id] = tracked.BaseRevision - 1 },
                cancellationToken);
        }

        return await LoadFullAsync(updated.Id, cancellationToken);
    }

    /// <summary>
    /// Übersetzt eine Concurrency-Abweisung (Review R1 P1 / R2 B2) in einen
    /// fachlichen Revisionskonflikt mit erwartetem und Ist-Wert — die
    /// Transaktion (Buchung, Historie, Positionen) wurde bereits von
    /// SQLite zurückgerollt; nur der aktuelle Ist-Zustand wird für die
    /// Fehlermeldung nachgeladen. Gemeldet wird das Kettenglied, dessen
    /// CAS tatsächlich fehlgeschlagen ist (in Kettentiefe: das direkte
    /// Original zuerst, dann die Vorfahren bis zum Wurzeloriginal).
    /// </summary>
    private async Task<DomainConflictException> RevisionConflictAsync(
        IReadOnlyList<Booking> chain,
        IReadOnlyDictionary<string, int> expectedRevisions,
        CancellationToken cancellationToken)
    {
        var failedId = chain[0].Id;
        var expected = expectedRevisions[failedId];
        var actual = expected;

        for (var i = 0; i < chain.Count; i++)
        {
            var current = await db.Bookings
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == chain[i].Id, cancellationToken);
            if (current is not null && current.BaseRevision != expectedRevisions[chain[i].Id])
            {
                failedId = chain[i].Id;
                expected = expectedRevisions[chain[i].Id];
                actual = current.BaseRevision;
                break;
            }
        }

        return new DomainConflictException(
            $"Buchung '{failedId}': erwartete Basisrevision {expected} weicht von der " +
            $"Ist-Revision {actual} ab (Revisionskonflikt durch parallele Operation). " +
            "Der gesamte Vorgang wurde zurückgerollt — die Buchung bleibt im letzten " +
            "konsistenten Zustand.")
        {
            ExpectedBaseRevision = expected,
            ActualBaseRevision = actual,
        };
    }

    /// <summary>
    /// Wendet die geplanten Änderungen an den internen Ausgleichspositionen
    /// an (gleiche Semantik für Anlage und In-place-Korrektur): neue Zeilen
    /// werden angelegt, bestehende (per stabiler Id) in Restzustand, Status
    /// und Begleichungsreferenz nachgeführt — in derselben Transaktion wie
    /// die Buchung (Rechenvertrag §3.2/§3.5).
    /// </summary>
    private async Task ApplySettlementChangesAsync(
        IReadOnlyCollection<SettlementPosition> settlements,
        CancellationToken cancellationToken)
    {
        foreach (var position in settlements)
        {
            var tracked = await db.SettlementPositions
                .FirstOrDefaultAsync(p => p.Id == position.Id, cancellationToken);

            if (tracked is null)
            {
                // Neue Position (Episode wurde durch diese Buchung begründet
                // oder eine vermisste Position wird nachgeführt).
                db.SettlementPositions.Add(position);
                continue;
            }

            // Bestehende Position: Restzustand, Status und
            // Begleichungsreferenz aktualisieren.
            tracked.Amount = position.Amount;
            tracked.RestAmount = position.RestAmount;
            tracked.Status = position.Status;
            tracked.SettledByBookingId = position.SettledByBookingId;
            tracked.SettledDate = position.SettledDate;
        }
    }

    /// <summary>
    /// Ersetzt die Kindzeilen einer Buchung: alte Zeilen werden gelöscht,
    /// neue werden wie im <see cref="AddAsync"/>-Muster angelegt
    /// (Design Schritt 6: In-place-Korrektur ersetzt Postings, Teilposten
    /// und Verteilungszeilen — eine Teilübernahme ist ausgeschlossen).
    /// </summary>
    private void ReplaceChildren(Booking tracked, Booking updated)
    {
        ReplaceChildCollection(db.AccountPostings, tracked.AccountPostings, updated.AccountPostings);
        ReplaceChildCollection(db.PoolPostings, tracked.PoolPostings, updated.PoolPostings);
        ReplaceChildCollection(db.ExpenseSplits, tracked.Splits, updated.Splits);
        ReplaceChildCollection(db.TransferPurposes, tracked.TransferPurposes, updated.TransferPurposes);
        ReplaceChildCollection(db.ReassignmentAccounts, tracked.ReassignmentAccounts, updated.ReassignmentAccounts);
    }

    /// <summary>
    /// Zeilen-Ersatz einer Kindmenge: Zeilen, die der Ersatzinhalt nicht
    /// mit derselben Instanz weiterführt, werden gelöscht; neue (abgelöste)
    /// Zeilen werden angelegt. Weitergeführte Instanzen (z. B. Teilposten
    /// einer Korrektur ohne neue Splits) bleiben unverändert — sie werden
    /// weder gelöscht noch neu eingefügt.
    /// </summary>
    private static void ReplaceChildCollection<T>(
        DbSet<T> set, List<T> current, IEnumerable<T> replacement)
        where T : class
    {
        var kept = new HashSet<object>(replacement);
        foreach (var child in current.Where(c => !kept.Contains(c)).ToList())
        {
            set.Remove(child);
        }
        current.Clear();
        foreach (var child in replacement)
        {
            current.Add(child);
        }
    }

    /// <summary>
    /// Lädt eine Buchung frisch aus der Datenbank, inkl. aller Postings,
    /// Teilposten, Verteilungszeilen und Storno-Deltas — die Antwort der
    /// Schreiboperationen trägt den tatsächlichen persistierten Zustand.
    /// </summary>
    private async Task<Booking> LoadFullAsync(string id, CancellationToken cancellationToken)
    {
        var booking = await db.Bookings
            .Include(b => b.AccountPostings)
            .Include(b => b.PoolPostings)
            .Include(b => b.Splits)
            .ThenInclude(s => s.Tax)
            .Include(b => b.TransferPurposes)
            .Include(b => b.ReassignmentAccounts)
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == id, cancellationToken);

        if (booking is null)
        {
            throw new InvalidOperationException(
                $"Buchung '{id}' konnte nach dem Speichern nicht nachgeladen werden.");
        }

        await LoadReversalDeltasAsync([booking], cancellationToken);
        return booking;
    }

    /// <summary>
    /// Persistiert die negierten Deltas eines Stornos als eigene Zeilen
    /// (Design Schritt 6 §2.2) — in derselben Transaktion wie die Buchung
    /// selbst. Buchungen ohne Deltas (alle übrigen Typen) erzeugen keine
    /// Zeilen.
    /// </summary>
    private void AddReversalDeltaRows(Booking booking)
    {
        foreach (var delta in booking.ReversalDeltas)
        {
            db.ReversalDeltas.Add(new ReversalDeltaRow(
                booking.Id, delta.AccountId, delta.PoolId, delta.Amount.Cents));
        }
    }

    /// <summary>
    /// Bestückt die geladenen Buchungen mit den negierten Storno-Deltas
    /// (Design Schritt 6 §2.2) — eine Query für alle Buchungen über
    /// <c>ids.Contains</c>. Buchungen ohne Delta-Zeilen behalten den
    /// Default (leere Liste).
    /// </summary>
    private async Task LoadReversalDeltasAsync(
        IEnumerable<Booking> bookings,
        CancellationToken cancellationToken)
    {
        var ids = bookings.Select(b => b.Id).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var rows = await db.ReversalDeltas
            .Where(r => ids.Contains(r.BookingId))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        foreach (var group in rows.GroupBy(r => r.BookingId))
        {
            var booking = bookings.FirstOrDefault(b => b.Id == group.Key);
            if (booking is null)
            {
                continue;
            }

            booking.ReversalDeltas = group
                .Select(r => new AllocationDeltaEntry(r.AccountId, r.PoolId, Money.FromCents(r.Amount)))
                .ToList();
        }
    }
}
