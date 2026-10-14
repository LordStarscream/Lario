using Lario.Domain;
using Lario.Domain.Journal;

namespace Lario.Application.Journal;

/// <summary>
/// Anwendungsfall „Buchung stornieren" (Schritt 6, Rechenvertrag
/// §5.1/§5.4/§5.5, Design Schritt 6 §2.5/§3.2):
/// - ein Storno ist eine neue Buchung vom Typ
///   <see cref="BookingType.Reversal"/> mit negierten Postings und
///   explizit gespeicherten negierten Deltas
///   (<c>ReversalDeltas == Negation(Original.AllocationDelta())</c>, kein
///   Cross-Booking-Lookup im Replay), verknüpft über
///   <c>ReversesBookingId</c> — das Original bleibt im Journal;
/// - Erfolgsfall: Reversal-Buchung UND Original-Revision++ in einer
///   einzigen Transaktion (atomar) — der Revisionszuwachs macht den
///   Doppelstorno-Strukturprüfung möglich: ein zweites Storno mit
///   veralteter Erwartungsrevision schlägt als Revisionskonflikt fehl
///   (Plan-Abnahme B1);
/// - Idempotenz zuerst (vor Katalog- und Revisionsprüfung): gleiche Id
///   mit gleichem Inhalt wird nur quittiert (keine zweite
///   Storno-Buchung, Revision bleibt); gleiche Id mit anderem Inhalt oder
///   anderem Typ ist ein Konflikt;
/// - stornierbare Typen: Ausgabe, Einnahme, Konto-Transfer, Umwidmung,
///   Verbund (Transfer+Umwidmung) und Storno („Storno eines Stornos" =
///   Widerruf); nicht stornierbar: Anfangsbestand und
///   Kontostandskorrektur (Differenzen werden durch eine neue
///   dokumentierte Korrektur ersetzt, nicht storniert);
/// - Revision: <c>ExpectedBaseRevision</c> muss der Ist-Revision des
///   Originals entsprechen, sonst <see cref="DomainConflictException"/>
///   mit erwartetem und Ist-Wert;
/// - Storno nicht möglich, wenn eine offene Begleichungsposition existiert,
///   auf die sich die Buchung bezieht (<c>RelatedSettlementId</c>) — der
///   Storno würde den offenen Bestand auf eine nicht mehr wirkende
///   Buchung zeigen; zuerst muss die Position geschlossen werden;
/// - Begleichungspositionen werden atomar mit dem Storno nachgeführt
///   (Vorher/Nachher-Übergangsanalyse des Journals, Design Schritt 6
///   §3.3): z. B. setzt ein Storno einer beglichenen Ausgabe die
///   Begleichung auf den Journalzustand zurück (Plan-Abnahme B4) — die
///   Journalwahrheit bleibt konsistent, alte Zeilen verschwinden nicht
///   still.
/// </summary>
public sealed class ReversalService(
    IBookingRepository bookingRepository,
    ISettlementPositionRepository settlementPositionRepository)
{
    /// <summary>
    /// Ergebnis eines Stornos: <see cref="Created"/> ist true, wenn das
    /// Storno als neue Buchung angelegt wurde (Original-Revision erhöht);
    /// false bei Idempotenz-Treffer (das vorhandene Storno wird
    /// zurückgegeben, nichts wurde neu geschrieben).
    /// </summary>
    public sealed record Result(bool Created, Booking Booking);

    public async Task<Result> ReverseAsync(
        ReversalSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // Strukturprüfung der Vorgabe (Ids, Selbstverknüpfung, Revision).
        spec.Validate();

        // Idempotenz zuerst: das vorhandene Storno wird zurückgeliefert,
        // bevor Katalog- oder Revisionsprüfung greifen.
        var existing = await bookingRepository.FindByIdAsync(spec.Id, cancellationToken);
        if (existing is not null)
        {
            if (existing.Type != BookingType.Reversal)
            {
                throw new DomainConflictException(
                    $"Buchungs-Id '{spec.Id}' ist bereits vergeben (Typ {existing.Type}) — " +
                    "Storno-Operationen müssen eine freie Id verwenden.");
            }

            if (existing.ReversesBookingId != spec.OriginalBookingId
                || existing.Date != spec.Date
                || existing.Note != spec.Note)
            {
                throw new DomainConflictException(
                    $"Für die Buchung '{spec.Id}' existiert bereits eine Storno-Buchung mit " +
                    "anderem Inhalt. Gleiches Id, anderer Inhalt ist ein Konflikt — die " +
                    "vorhandene Buchung bleibt unverändert.");
            }

            return new Result(false, existing);
        }

        // Original laden und Stornierbarkeit prüfen.
        var original = await bookingRepository.FindByIdAsync(spec.OriginalBookingId, cancellationToken)
            ?? throw new InvalidReferenceException(
                $"Storno '{spec.Id}': das Original '{spec.OriginalBookingId}' existiert nicht.");

        if (!IsReversible(original.Type))
        {
            throw new LarioDomainException(
                $"Storno '{spec.Id}': Buchungen vom Typ {original.Type} sind nicht stornierbar. " +
                "Anfangsbestände und Kontostandskorrekturen ändern Sie über eine neue, " +
                "dokumentierte Buchung — nicht über ein Storno.");
        }

        // Revision des Originals (Rechenvertrag §5.5).
        if (original.BaseRevision != spec.ExpectedBaseRevision)
        {
            throw new DomainConflictException(
                $"Storno '{spec.Id}' der Buchung '{original.Id}': erwartete Basisrevision " +
                $"{spec.ExpectedBaseRevision} weicht von der Ist-Revision {original.BaseRevision} ab " +
                "(Revisionskonflikt). Die Buchung bleibt unverändert.")
            {
                ExpectedBaseRevision = spec.ExpectedBaseRevision,
                ActualBaseRevision = original.BaseRevision,
            };
        }

        var bookings = await bookingRepository.GetAllAsync(cancellationToken);

        // Doppelstorno: das Original darf höchstens wirksam storniert
        // sein. Geprüft wird der wirksame Status über die Storno-Kette:
        // ein widerrufenes Storno (Storno des Stornos) hat das Original
        // wieder wirksam gemacht — dann ist ein neues Storno des
        // Originals zulässig (Review R1 P2). Mit veralteter
        // Erwartungsrevision ist ein paralleler Doppelstorno oben bereits
        // ein Revisionskonflikt (Design Schritt 6 §2.5/§3.2).
        if (new BookingJournal(bookings).IsEffectivelyReversed(original.Id))
        {
            throw new DomainConflictException(
                $"Storno '{spec.Id}': die Buchung '{original.Id}' ist bereits storniert — " +
                "ein Doppelstorno ist ausgeschlossen. Widerrufen Sie das bestehende Storno " +
                "erstmals (neues Storno auf die Storno-Buchung), wenn Sie den Storno " +
                "zurücknehmen wollen.");
        }

        var booking = spec.ToBooking(original);

        // Review R2 P1 (B1): ein Storno eines Stornos (Widerruf) reaktiviert
        // den Ast darüber. Ein Ast darf nur reaktiviert werden, wenn er
        // finanziell noch mit dem aktuellen Zustand übereinstimmt —
        // sonst entstehen doppelte wirksame Stornos oder Geld aus dem
        // Nichts (siehe CheckReversalChainConsistency).
        if (original.Type == BookingType.Reversal)
        {
            CheckReversalChainConsistency(bookings, original, booking, spec.Id);
        }

        // Eine offene Begleichungsposition, auf die sich die Buchung
        // bezieht, blockiert das Storno (Design Schritt 6 §3.2): der
        // Storno würde den offenen Bestand auf eine nicht mehr wirkende
        // Buchung zeigen — die Position muss zuerst geschlossen werden.
        if (original.RelatedSettlementId is { } relatedSettlementId)
        {
            var position = await settlementPositionRepository
                .FindByIdAsync(relatedSettlementId, cancellationToken);
            if (position is not null && position.Status != SettlementStatus.Beglichen)
            {
                throw new DomainConflictException(
                    $"Storno '{spec.Id}': die Buchung '{original.Id}' ist an die offene " +
                    $"Begleichungsposition '{relatedSettlementId}' gebunden — ein Storno würde " +
                    "den offenen Bestand auf eine nicht mehr existente Buchung zeigen. " +
                    "Schließen Sie die Position zuerst (Ausgleich oder Korrektur), dann ist " +
                    "das Storno möglich.");
            }
        }

        // Begleichungspositionen atomar mit dem Storno nachführen
        // (Vorher = Journal, Nachher = Journal + Reversal — die gesamte
        // Schritt-5-Übergangsanalyse greift ohne Sonderwege, Design
        // Schritt 6 §3.3).
        var persistedPositions = await settlementPositionRepository.GetAllAsync(cancellationToken);
        var changes = SettlementPositionSync.Plan(
            bookings, booking, persistedPositions,
            () => $"set-{Guid.NewGuid():N}");

        // Review R3 P1: Die erwarteten Revisionen aller geprüften
        // Kettenglieder werden unverändert aus dem geprüften Snapshot an
        // den Repository übergeben — das direkte Original ist die
        // getrackte Instanz, deren Revision die Vorgabe getragen hat; die
        // Vorfahren sind die (AsNoTracking) Journal-Einträge aus demselben
        // Snapshot, den die Kettenprüfung verwendet hat. Der Repository
        // CAS gegen genau diese Revisionen und liest keine aktuelleren
        // Werte nach: eine parallele Commit zwischen Prüfung und Schreiben
        // (Korrektur des Wurzeloriginals, neues direktes Storno der Wurzel)
        // führt so zum Revisionskonflikt samt Rollback, statt dass das
        // geprüfte Ergebnis auf veraltetem Stand committet würde.
        var byId = bookings.ToDictionary(b => b.Id);
        var originalChain = new List<Booking> { original };
        var current = original;
        while (current.Type == BookingType.Reversal && current.ReversesBookingId is { } parentId)
        {
            if (!byId.TryGetValue(parentId, out var parent))
            {
                throw new InvalidReferenceException(
                    $"Storno '{spec.Id}': die Storno-Kette ist unterbrochen — das Original " +
                    $"'{current.Id}' verweist auf die unbekannte Buchung '{parentId}'.");
            }

            originalChain.Add(parent);
            current = parent;
        }

        // Atomar: Reversal-Buchung + Ketten-Revision (eine Transaktion —
        // alle Kettenglieder mit den Snapshot-Revisionen als
        // Concurrency-Basis; Review R2 B2 / R3 P1).
        var stored = await bookingRepository.AddWithOriginalRevisionAsync(
            booking, originalChain, changes.Select(c => c.Row).ToList(), cancellationToken);
        return new Result(true, stored);
    }

    /// <summary>
    /// Stornierbare Buchungs-Typen (Design Schritt 6 §3.2): alle Typen
    /// mit echter Konto- oder Pool-Auswirkung — Ausgabe, Einnahme,
    /// Konto-Transfer, Umwidmung, Verbund (Transfer+Umwidmung) und Storno
    /// selbst („Storno eines Stornos" = revisionsgerechter Widerruf).
    /// Nicht stornierbar: Anfangsbestand (er wird nicht rückgängig, sondern
    /// korrigiert) und Kontostandskorrektur (Differenz wird durch eine
    /// neue dokumentierte Korrektur ersetzt, nicht storniert); Erstattungen
    /// sind in Schritt 6 nicht stornierbar (sie sind vom Storno getrennt —
    /// B2).
    /// </summary>
    private static bool IsReversible(BookingType type) => type switch
    {
        BookingType.Expense => true,
        BookingType.Income => true,
        BookingType.AccountTransfer => true,
        BookingType.Reassignment => true,
        BookingType.TransferReassignment => true,
        BookingType.Reversal => true,
        _ => false,
    };

    /// <summary>
    /// Konsistenzcheck der Storno-Kette vor einem Widerruf (Storno eines
    /// Stornos, Review R2 P1 / B1). Der zu widerrufende Ast wird im
    /// künftigen Zustand (aktuelles Journal + neue Buchung) auf drei
    /// Invarianten geprüft:
    /// <list type="number">
    /// <item><description><b>Linearität</b> — ein Storno darf insgesamt
    /// höchstens <i>einen</i> Storno tragen. Zwei Widerrufe desselben Stornos
    /// würden die Kette gabeln und den wirksamen Status mehrdeutig machen
    /// (die Journal-Parität würde je nach Wegführung anders ausfallen).</description></item>
    /// <item><description><b>Höchstens ein wirksames Storno je Buchung</b> —
    /// im künftigen Zustand darf keine Buchung der Kette zwei wirksame
    /// Stornos haben. Ein zweites wirksames Storno des Wurzeloriginals
    /// pariert sich im Status zu „nicht storniert“, verschiebt das Saldo
    /// aber um den Stornobetrag.</description></item>
    /// <item><description><b>Frische</b> — jedes wirksame Storno des
    /// Wurzeloriginals im künftigen Zustand muss den <i>aktuellen</i>
    /// Inhalt des Wurzeloriginals (Kontoposten und Pool-Zuordnung, negiert)
    /// abbilden. Ein Ast, der während einer In-place-Korrektur des
    /// Wurzeloriginals widerrufen wurde, ist veraltet — seine Reaktivierung
    /// würde Geld aus dem Nichts erzeugen. Stattdessen wird ein neues Storno
    /// des Wurzeloriginals erstellt (der Stornoinhalt leitet sich zum
    /// Erstellungszeitpunkt aus dem aktuellen Original ab).</description></item>
    /// </list>
    /// </summary>
    private static void CheckReversalChainConsistency(
        IReadOnlyList<Booking> bookings, Booking original, Booking newReversal, string specId)
    {
        var byId = bookings.ToDictionary(b => b.Id);

        // 1. Linearität: ein Storno trägt noch keinen Storno.
        var existingReversalsOfOriginal = bookings
            .Where(b => b.Type == BookingType.Reversal && b.ReversesBookingId == original.Id)
            .ToList();
        if (existingReversalsOfOriginal.Count > 0)
        {
            throw new DomainConflictException(
                $"Storno '{specId}': das Storno '{original.Id}' wurde bereits widerrufen " +
                $"(durch '{existingReversalsOfOriginal[0].Id}') — ein zweiter Widerruf desselben " +
                "Stornos ist ausgeschlossen. Die Storno-Kette ist streng linear (ein Original, " +
                "maximal ein Storno je Kettenglied). Widerrufen Sie stattdessen den gesamten " +
                "Zweig oder stornieren Sie das Wurzeloriginal neu, wenn der Storno nicht mehr " +
                "bestehen soll.");
        }

        // Kette zum Wurzeloriginal hochlaufen (das erste Kettenglied, das
        // selbst kein Storno ist). Abbrüche sind ein Integritätsfehler.
        var chain = new List<Booking> { original };
        for (var current = original;
             current.Type == BookingType.Reversal && current.ReversesBookingId is { } parentId;
             current = byId[parentId])
        {
            if (!byId.TryGetValue(parentId, out var parent))
            {
                throw new InvalidReferenceException(
                    $"Storno '{specId}': die Storno-Kette ist unterbrochen — das Original " +
                    $"'{current.Id}' verweist auf die unbekannte Buchung '{parentId}'.");
            }
            chain.Add(parent);
        }
        var root = chain[^1];

        // Künftiger Zustand: aktuelles Journal + neue Buchung.
        var future = new BookingJournal([.. bookings, newReversal]);

        // 2. Höchstens ein wirksames Storno je Kettenglied im künftigen
        //    Zustand (insbesondere am Wurzeloriginal — doppeltes wirksames
        //    Storno = Saldo-Verschiebung).
        foreach (var link in chain)
        {
            var effectiveReversals = future.ReversalsOf(link.Id)
                .Where(r => !future.IsEffectivelyReversed(r.Id))
                .ToList();
            if (effectiveReversals.Count > 1)
            {
                throw new DomainConflictException(
                    $"Storno '{specId}': die Buchung '{link.Id}' hätte danach " +
                    $"{effectiveReversals.Count} wirksame Stornos " +
                    $"({string.Join(", ", effectiveReversals.Select(r => r.Id))}) — " +
                    "maximal ein wirksames Storno je Buchung ist erlaubt, sonst " +
                    "verschiebt sich der Saldo um den Stornobetrag. Widerrufen Sie zuerst " +
                    "das Storno, das dauerhaft bestehen soll.");
            }
        }

        // 3. Frische: jedes wirksame Storno des Wurzeloriginals im künftigen
        //    Zustand muss dessen aktuellen Inhalt abbilden.
        foreach (var reversal in future.ReversalsOf(root.Id)
                     .Where(r => !future.IsEffectivelyReversed(r.Id)))
        {
            if (!MatchesCurrentContent(reversal, root))
            {
                throw new DomainConflictException(
                    $"Storno '{specId}': der Widerruf von '{original.Id}' würde das Storno " +
                    $"'{reversal.Id}' von '{root.Id}' reaktivieren, aber '{root.Id}' wurde " +
                    "in-place korrigiert — das Storno deckt nicht mehr den aktuellen Inhalt. " +
                    "Die Reaktivierung eines veralteten Astes ist ausgeschlossen (sie würde " +
                    "Geld aus dem Nichts erzeugen). Legen Sie stattdessen ein neues Storno " +
                    "des Wurzeloriginals an; es übernimmt dessen aktuellen Inhalt.");
            }
        }
    }

    /// <summary>
    /// Inhaltliche Gleichheit eines Stornos mit dem aktuellen Gegenstück:
    /// die Reversal-Deltas des Stornos müssen exakt die negierten
    /// Pool-Zuordnungen des Originals sein und die Kontoposten exakt die
    /// negierten Kontoposten (beide Mengen in deterministischer Reihenfolge
    /// verglichen). Nicht-inhaltliche Änderungen (Notiz, Datum) sind für die
    /// Frische irrelevant — sie verschieben keine Salden.
    /// </summary>
    private static bool MatchesCurrentContent(Booking reversal, Booking root)
    {
        var expectedDeltas = root.AllocationDelta()
            .Select(d => (AccountId: d.AccountId, PoolId: d.PoolId, d.Amount.Negate()))
            .OrderBy(d => d.AccountId, StringComparer.Ordinal)
            .ThenBy(d => d.PoolId, StringComparer.Ordinal)
            .ToList();
        var actualDeltas = reversal.ReversalDeltas
            .Select(d => (AccountId: d.AccountId, PoolId: d.PoolId, Amount: d.Amount))
            .OrderBy(d => d.AccountId, StringComparer.Ordinal)
            .ThenBy(d => d.PoolId, StringComparer.Ordinal)
            .ToList();
        if (!expectedDeltas.SequenceEqual(actualDeltas))
        {
            return false;
        }

        var expectedPostings = root.AccountPostings
            .Select(p => (AccountId: p.AccountId, Amount: p.Amount.Negate()))
            .OrderBy(p => p.AccountId, StringComparer.Ordinal)
            .ToList();
        var actualPostings = reversal.AccountPostings
            .Select(p => (AccountId: p.AccountId, Amount: p.Amount))
            .OrderBy(p => p.AccountId, StringComparer.Ordinal)
            .ToList();
        return expectedPostings.SequenceEqual(actualPostings);
    }
}
