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
        NormalizeOrder(booking);
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
        foreach (var booking in bookings)
        {
            NormalizeOrder(booking);
        }
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

    /// <summary>
    /// Review R1 (P1) / R2 (B2) / R3 (P1): Die Revisionserhöhung der
    /// gesamten Original-Kette und die Einfügung der Buchung laufen in
    /// derselben Transaktion. Die Concurrency-Token der Kettenglieder
    /// werden mit den übergebenen Snapshot-Revisionen belegt; EF Core
    /// erzeugt daraus je ein WHERE BaseRevision = … — eine parallele
    /// Commit verschiebt eine Revision, das UPDATE betrifft 0 Zeilen,
    /// und der gesamte Transaktionsstand (Buchung + Kette +
    /// Ausgleichspositionen) wird zurückgerollt. Eine Buchung auf Basis
    /// eines veralteten Lesezustands (Doppelstorno, parallele Korrektur,
    /// neues direktes Storno) kann so nicht mehr committen.
    /// </summary>
    public async Task<Booking> AddWithOriginalRevisionAsync(
        Booking booking,
        IReadOnlyList<Booking> originalChain,
        IReadOnlyCollection<SettlementPosition> settlements,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(originalChain);
        ArgumentNullException.ThrowIfNull(settlements);

        if (originalChain.Count == 0)
        {
            throw new InvalidOperationException(
                "AddWithOriginalRevisionAsync erfordert mindestens ein Kettenglied " +
                "(das direkte Original).");
        }

        // Review R3 P1: Die Concurrency-Basis sind die Revisionen der
        // übergebenen Kettenglieder aus dem geprüften Snapshot — nicht
        // neu gelesene aktuelle Werte. Zwischen der Prüfung im
        // Anwendungsfall und diesem Schreiben kann eine parallele
        // Commit eine Kettenglieder-Revision verschieben (Korrektur der
        // Wurzel, neues direktes Storno der Wurzel). Würde der Repository
        // die aktuellen Werte als Basis übernehmen, würde das geprüfte
        // Ergebnis auf veraltetem Stand committen; gegen die
        // Snapshot-Revision schlägt das UPDATE fehl und der vollständige
        // Rollback macht Prüfung und Schreiben atomar.
        var expectedRevisions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var link in originalChain)
        {
            if (string.IsNullOrWhiteSpace(link.Id))
            {
                throw new InvalidOperationException(
                    "AddWithOriginalRevisionAsync: Kettenglied ohne Id.");
            }

            if (link.Id == booking.Id)
            {
                throw new InvalidOperationException(
                    $"AddWithOriginalRevisionAsync: Die neue Buchung '{booking.Id}' " +
                    "darf nicht Teil ihrer eigenen Original-Kette sein.");
            }

            if (!expectedRevisions.TryAdd(link.Id, link.BaseRevision))
            {
                throw new InvalidOperationException(
                    $"AddWithOriginalRevisionAsync: Kettenglied '{link.Id}' kommt " +
                    "mehrfach vor — die Original-Kette muss linear sein.");
            }
        }

        var exists = await db.Bookings.AnyAsync(b => b.Id == booking.Id, cancellationToken);
        if (exists)
        {
            throw new InvalidOperationException(
                $"Buchung mit der Id '{booking.Id}' existiert bereits. " +
                "Die Idempotenzprüfung obliegt dem Anwendungsfall.");
        }

        // Review R2 (B2): Die Revisionserhöhung umfasst alle Kettenglieder
        // — jedes mit seinem eigenen Concurrency-Token. Review R3 P1:
        // Die Concurrency-Basis ist die Snapshot-Revision des
        // Kettenglieds, nicht der neu gelesene aktuelle Wert.
        var tracked = new List<Booking>(originalChain.Count);
        foreach (var snapshot in originalChain)
        {
            var instance = await db.Bookings
                .FirstOrDefaultAsync(b => b.Id == snapshot.Id, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"AddWithOriginalRevisionAsync: Kettenglied '{snapshot.Id}' " +
                    "existiert nicht — die Original-Kette ist unterbrochen.");

            // Concurrency-Token: OriginalValue auf die Snapshot-Revision
            // setzen (Review R3 P1). EF Core erzeugt daraus das
            // WHERE BaseRevision = … des UPDATE. Ein zwischenzeitlich
            // verschiebendes paralleles Commit betrifft 0 Zeilen →
            // DbUpdateConcurrencyException → vollständiger Rollback.
            db.Entry(instance)
                .Property(nameof(Booking.BaseRevision))
                .OriginalValue = snapshot.BaseRevision;
            instance.BaseRevision = snapshot.BaseRevision + 1;
            tracked.Add(instance);
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
            throw await RevisionConflictAsync(tracked, expectedRevisions, cancellationToken);
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

    /// <summary>
    /// Review 7a, Befund 3 (Race B): Ein paralleler Beleg-Upload kann
    /// zwischen der Attachment-Abfrage in <c>ReplaceSplits</c> und dem
    /// Commit dieser Korrektur einen Beleg an einen entfallenden
    /// Teilposten legen — das RESTRICT-FK (Attachment → ExpenseSplit)
    /// blockt dann die Teilposten-Löschung. Erwartbarer, kontrollierbarer
    /// Konflikt: frischen Lesezustand laden und die Update-Befehle erneut
    /// aufbauen. Der frische Lesezustand sieht den Beleg, und
    /// <c>ReplaceSplits</c> hängt ihn an die Gesamtbuchung um
    /// (Erhaltungsregel, Design D1b) — der Versuch committet dann sauber.
    /// Nicht-FK-Fehler und Revisionskonflikte
    /// (<see cref="DbUpdateConcurrencyException"/>, eigener Unterfall von
    /// <see cref="DbUpdateException"/>) laufen unverändert weiter.
    ///
    /// Review 7a-2, Befund 4 (P1): Zusätzlich prüft jeder Versuch
    /// (einschließlich Retries mit frischem Lesezustand) exakt die
    /// erwartete Basisrevision und pinnen den Concurrency-Token darauf
    /// — eine Revisionsänderung, die zwischen Laden und Commit commitet
    /// wird, scheitert als sichtbarer Revisionskonflikt (409) statt
    /// stillschweigend übersteuert zu werden.
    /// </summary>
    public async Task<Booking> UpdateAsync(
        Booking updated,
        BookingCorrection correction,
        IReadOnlyCollection<SettlementPosition> settlements,
        CancellationToken cancellationToken = default)
    {
        // Review 7a-2, Befund 4 (P1): Der Aufrufer (CorrectionService)
        // garantiert updated.BaseRevision = original.BaseRevision + 1 —
        // die erwartete Basisrevision für die Revisionsprüfung und den
        // Concurrency-Token (CAS). Sie wird vor dem Retry-Loop abgeleitet
        // und jedem Versuch mitgegeben; ein Retry lädt zwar frischen
        // Lesezustand, darf aber die erwartete Basisrevision nicht
        // annehmen (sonst würde er eine zwischenzeitlich commitete
        // Revisionsänderung stillschweigend übersteuern — lost update).
        var expectedRevision = updated.BaseRevision - 1;
        const int maxAttempts = 3;
        DbUpdateException? lastConflict = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await ApplyUpdateAsync(
                    updated, correction, settlements, expectedRevision, cancellationToken);
            }
            catch (DbUpdateException ex) when (SqliteConstraintErrors.IsForeignKeyViolation(ex))
            {
                // SQLite hat die Transaktion bereits zurückgerollt; den
                // Change-Tracker leeren, damit der nächste Versuch einen
                // frischen Lesezustand lädt (sonst würde er die stale
                // Attachment-Liste aus dem ersten Versuch wiederverwenden).
                lastConflict = ex;
                if (attempt < maxAttempts)
                {
                    db.ChangeTracker.Clear();
                }
            }
        }

        throw new DomainConflictException(
            $"Korrektur der Buchung '{updated.Id}': sie kollidierte mit einer " +
            "parallelen Änderung (z. B. wurde während der Korrektur ein Beleg " +
            "an einen entfallenden Teilposten angehängt). Die Buchung bleibt " +
            $"im letzten konsistenten Zustand (SQLite: {lastConflict!.GetBaseException()?.Message}) — bitte erneut versuchen.");
    }

    private async Task<Booking> ApplyUpdateAsync(
        Booking updated,
        BookingCorrection correction,
        IReadOnlyCollection<SettlementPosition> settlements,
        int expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settlements);
        ArgumentNullException.ThrowIfNull(updated);
        ArgumentNullException.ThrowIfNull(correction);

        var tracked = await db.Bookings
            .Include(b => b.AccountPostings)
            .Include(b => b.PoolPostings)
            .Include(b => b.Splits)
            .ThenInclude(s => s.Tax)
            .Include(b => b.TransferPurposes)
            .Include(b => b.ReassignmentAccounts)
            .FirstOrDefaultAsync(b => b.Id == updated.Id, cancellationToken)
            ?? throw new InvalidOperationException(
                $"In-place-Korrektur für unbekannte Buchung '{updated.Id}' — " +
                "das Original existiert nicht.");

        // Review 7a-2, Befund 4 (P1): Die Basisrevision des geladenen
        // Zustands muss die erwartete Basisrevision der Fassung sein —
        // auch nach einem Retry mit neuem Ladevorgang. Der automatisch
        // generierte Concurrency-Token würde eine Revisionsänderung,
        // die zwischenzeitlich commitet wurde, stillschweigend
        // übersteuern (der CAS bezieht sich auf den gerade geladenen
        // Zustand, und die Fassung schreibt denselben Revisionswert
        // zurück). Exakte Abweichung → sichtbarer Revisionskonflikt
        // (409), die Fassung wird nicht angefasst.
        if (tracked.BaseRevision != expectedRevision)
        {
            throw await RevisionConflictAsync(
                [tracked],
                new Dictionary<string, int> { [updated.Id] = expectedRevision },
                cancellationToken);
        }

        // Der Typ bleibt; die übrigen skalaren Felder übernehmen den
        // geprüften Ersatzinhalt (Revisionserhöhung inklusive — sie kommt
        // aus dem Anwendungsfall).
        tracked.Date = updated.Date;
        tracked.Note = updated.Note;
        tracked.FundingMonth = updated.FundingMonth;
        tracked.BaseRevision = updated.BaseRevision;
        tracked.ReversesBookingId = updated.ReversesBookingId;
        tracked.RefundsExpenseId = updated.RefundsExpenseId;
        // Gesichertes Anfangsbestandskonto (Zwischenstep 7.5): folgt dem
        // wirksamen Konto der korrigierten Fassung (Konto-Wechsel bewegt
        // die Zeile; das partielle eindeutige Index erzwingt die
        // Einmaligkeitsregel atomar).
        tracked.OpeningBalanceAccountId = updated.OpeningBalanceAccountId;

        // Concurrency-Token (Review 7a-2, Befund 4): OriginalValue
        // explizit auf die erwartete Basisrevision pinnen — selbst eine
        // Änderung, die zwischen der Prüfung oben und dem Commit
        // commitet wird, lässt das UPDATE auf 0 Zeilen scheitern
        // (DbUpdateConcurrencyException → 409) statt zu commiten.
        db.Entry(tracked)
            .Property(nameof(Booking.BaseRevision))
            .OriginalValue = expectedRevision;

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
                new Dictionary<string, int> { [updated.Id] = expectedRevision },
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
        ReplaceSplits(tracked, updated);
        ReplaceChildCollection(db.TransferPurposes, tracked.TransferPurposes, updated.TransferPurposes);
        ReplaceChildCollection(db.ReassignmentAccounts, tracked.ReassignmentAccounts, updated.ReassignmentAccounts);
    }

    /// <summary>
    /// Zeilen-Ersatz der Teilposten mit In-Place-Update: Zeilen mit
    /// bestehender Teilposten-Id werden in der vorhandenen Zeile
    /// geändert, neue Zeilen werden angelegt, entfallene gelöscht.
    ///
    /// Das generische <see cref="ReplaceChildCollection"/> (löschen +
    /// neu anlegen) ist für Teilposten ungeeignet: die 1:1-
    /// Steuerkennzeichnung-Zeile würde per Datenbank-Cascade mit der
    /// gelöschten Teilposten-Zeile verschwinden, während die
    /// weitergeführte Kennzeichnungs-Instanz (Schlüssel bereits durch
    /// das Laden gesetzt) vom Kontext als „geändert“ statt „neu“
    /// erkannt und als UPDATE geschrieben wird — die Kennzeichnung
    /// verschwände dabei still (Schritt-7-Review R3 P2: In-place-
    /// Korrektur einer Ausgabe mit Steuerkennzeichnung).
    /// </summary>
    private void ReplaceSplits(Booking tracked, Booking updated)
    {
        var current = tracked.Splits;
        var byId = current.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var incoming in updated.Splits)
        {
            seen.Add(incoming.Id);

            if (!byId.TryGetValue(incoming.Id, out var existing))
            {
                // Neuer Teilposten: die Zeile wird vollständig angelegt;
                // eine mitgebrachte Kennzeichnung ist dabei stets eine
                // neue Instanz ohne Schlüssel (Service-Grenze).
                current.Add(incoming);
                continue;
            }

            // Vorhandener Teilposten: die Zeile wird in-place aktualisiert.
            existing.Amount = incoming.Amount;
            existing.PoolId = incoming.PoolId;
            existing.CategoryId = incoming.CategoryId;
            existing.Note = incoming.Note;
            ReconcileSplitTax(existing, incoming.Tax);
        }

        // Entfallene Teilposten (mit Kennzeichnung) werden gelöscht.
        foreach (var stale in current.Where(s => !seen.Contains(s.Id)).ToList())
        {
            // Beleg-Umlageung (Design 7a D1b, Review P1): Belege, die den
            // entfallenden Split belegen, wandern in derselben Transaktion
            // auf die Gesamtbuchung — sonst würde der FK (Restrict) das
            // Löschen blockieren, und ohne Umlageung würden Metadaten auf
            // einen gelöschten Split zeigen. Die alte Id bleibt in
            // OriginalSplitId nachvollziehbar; Datei und Buchung bleiben
            // unverändert.
            // BookingId ist eine Schatten-Spalte des Komposit-Keys —
            // über die Buchung wird gefiltert (alle Splits von 'tracked').
            var staleId = stale.Id;
            var bookingId = tracked.Id;
            foreach (var attachment in db.Attachments
                .Where(a => a.BookingId == bookingId && a.SplitId == staleId)
                .ToList())
            {
                attachment.SplitId = null;
                attachment.OriginalSplitId = staleId;
            }
            db.Remove(stale);
        }
    }

    /// <summary>
    /// Steuerkennzeichnung eines in-place aktualisierten Teilpostens:
    /// vorhanden → die vorhandene Zeile wird in-place aktualisiert, neu
    /// → die Zeile wird angelegt, entfällt → die Zeile wird gelöscht.
    /// </summary>
    private void ReconcileSplitTax(ExpenseSplit split, TaxMarking? incoming)
    {
        if (split.Tax is null)
        {
            // null oder neue Instanz ohne Schlüssel — beides korrekt.
            split.Tax = incoming;
            return;
        }

        if (incoming is null)
        {
            db.Remove(split.Tax);
            split.Tax = null;
            return;
        }

        split.Tax.Relevant = incoming.Relevant;
        split.Tax.TaxCategoryId = incoming.TaxCategoryId;
        split.Tax.MarkedAmount = incoming.MarkedAmount;
        split.Tax.Note = incoming.Note;
        split.Tax.InheritedFromPreset = incoming.InheritedFromPreset;
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
        NormalizeOrder(booking);
        return booking;
    }

    /// <summary>
    /// Deterministische Reihenfolge der Kindkollektionen geladener
    /// Buchungen. SQLite garantiert keine Zeilenreihenfolge bei
    /// Sammlungsbeziehungen — ohne Normalisierung wären die Reihenfolge
    /// der Postings/Teilposten und damit die API-Antworten instabil
    /// (beobachteter Flaky: Detail-Ausgabe vertauschte die Pool-Posten).
    /// Kanonische Ordnung: Postings nach Konto-/Pool-Id, Teilposten nach
    /// Teilposten-Id, Verteilungszeilen nach Pool-/Konto-Id (Ordinal);
    /// dieselbe Id-Sortierung nutzt die Idempotenzprüfung bereits
    /// (RefundService). Die Fachsemantik ist reihenfolgenunabhängig
    /// (Aggregation und Referenzsuche), die Normalisierung berührt keine
    /// Invariante.
    /// </summary>
    private static void NormalizeOrder(Booking booking)
    {
        booking.AccountPostings.Sort((a, b) => string.CompareOrdinal(a.AccountId, b.AccountId));
        booking.PoolPostings.Sort((a, b) => string.CompareOrdinal(a.PoolId, b.PoolId));
        booking.Splits.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        booking.TransferPurposes.Sort((a, b) => string.CompareOrdinal(a.PoolId, b.PoolId));
        booking.ReassignmentAccounts.Sort((a, b) => string.CompareOrdinal(a.AccountId, b.AccountId));
        booking.ReversalDeltas = booking.ReversalDeltas
            .OrderBy(d => d.AccountId, StringComparer.Ordinal)
            .ThenBy(d => d.PoolId, StringComparer.Ordinal)
            .ToList();
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
