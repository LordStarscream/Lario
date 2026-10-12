using Lario.Domain;
using Lario.Domain.Journal;

namespace Lario.Application.Journal;

/// <summary>
/// Leitet die internen Ausgleichseffekte einer neuen Buchung ab
/// (Rechenvertrag §3.2/§3.5): Vergleich des abgeleiteten
/// Zellenzustands vor und nach der Buchung gegen die persistierten
/// Positionen. Reine Funktion ohne I/O — die Journalwahrheit bleibt die
/// Abfolge der Buchungen, die Positionstabelle ist deren persistente
/// Ansicht mit stabilen Identitäten.
///
/// Regeln:
/// - Eine Zelle (Konto, Pool), die durch die Buchung negativ wird oder
///   negativer bleibt, ist eine offene Ausgleichsposition. War sie zuvor
///   negativ, bleibt dieselbe Episode erhalten (gleiche
///   CreatedByBookingId) — nur Rest, Status und Begleichung ändern sich.
/// - Eine anwachsende negative Zelle wird nicht doppelt gebucht: die
///   Episode bleibt eine, der Rest wächst.
/// - Begleichung (Zelle wird nicht-negativ): die Episode schließt sich —
///   Rest 0, Status „beglichen", Referenz auf die gleichende Buchung und
///   deren Fachdatum.
/// - Persistierte Positionen ohne abgeleitete Episode sind ein
///   Integritätsfehler (die Ansicht darf nicht von der Wahrheit abweichen).
/// - Abgeleitete offene Episoden ohne persistierte Zeile werden
///   nachgeführt (z. B. aus einer früheren Version, in der Positionen
///   noch nicht persistiert wurden): deterministisch, gleiche
///   CreatedByBookingId, neue stabile Id.
/// </summary>
internal static class SettlementPositionSync
{
    /// <summary>Eine geplante Zeilenänderung: neue Zeile oder Update.</summary>
    public sealed record PlannedChange(SettlementPosition Row, bool IsNew);

    public static IReadOnlyList<PlannedChange> Plan(
        IReadOnlyList<Booking> bookings,
        Booking newBooking,
        IReadOnlyList<SettlementPosition> persisted,
        Func<string> newIdFactory)
    {
        ArgumentNullException.ThrowIfNull(bookings);
        ArgumentNullException.ThrowIfNull(newBooking);
        ArgumentNullException.ThrowIfNull(persisted);
        ArgumentNullException.ThrowIfNull(newIdFactory);

        var beforeJournal = new BookingJournal(bookings);
        var afterJournal = new BookingJournal(bookings.Concat([newBooking]));
        var beforeEpisodes = beforeJournal.DeriveSettlementPositions();
        var afterEpisodes = afterJournal.DeriveSettlementPositions();

        var beforeByKey = beforeEpisodes.ToDictionary(e => e.Key);
        var persistedByKey = persisted.ToDictionary(p =>
            (p.SourceAccountId, p.TargetPoolId, p.CreatedByBookingId));

        // Integrität: jede persistierte Position muss eine abgeleitete
        // Episode haben (die Ansicht darf nicht mehr enthalten als die
        // Wahrheit).
        foreach (var row in persisted)
        {
            if (!afterEpisodes.Any(e => e.Key ==
                (row.SourceAccountId, row.TargetPoolId, row.CreatedByBookingId)))
            {
                throw new LarioDomainException(
                    $"Integritätsfehler: die Ausgleichsposition '{row.Id}' " +
                    $"(Konto '{row.SourceAccountId}', Pool '{row.TargetPoolId}') " +
                    "hat kein Gegenstück im abgeleiteten Journalzustand.");
            }
        }

        var changes = new List<PlannedChange>();
        foreach (var episode in afterEpisodes)
        {
            var key = episode.Key;
            var beforeEpisode = beforeByKey.GetValueOrDefault(key);
            var unchanged = beforeEpisode is not null
                && beforeEpisode.Amount == episode.Amount
                && beforeEpisode.RestAmount == episode.RestAmount
                && beforeEpisode.Status == episode.Status
                && beforeEpisode.SettledByBookingId == episode.SettledByBookingId
                && beforeEpisode.SettledDate == episode.SettledDate;

            if (!persistedByKey.TryGetValue(key, out var storedRow))
            {
                // Keine persistierte Zeile:
                // - offene Episode, die es vorher schon gab → nachführen
                //   (frühere Version ohne Positionsverwaltung);
                // - Episode wurde durch die neue Buchung begründet →
                //   anlegen;
                // - beglichene Episode ohne Zeile → nichts zu tun
                //   (geschlossene Episoden ohne Historie bleiben unsichtbar).
                if (episode.Status == SettlementStatus.Offen)
                {
                    changes.Add(new PlannedChange(CreateRow(episode, newIdFactory), true));
                }

                continue;
            }

            if (unchanged)
            {
                continue;
            }

            changes.Add(new PlannedChange(
                new SettlementPosition
                {
                    Id = storedRow.Id,
                    SourceAccountId = storedRow.SourceAccountId,
                    TargetPoolId = storedRow.TargetPoolId,
                    CreatedByBookingId = storedRow.CreatedByBookingId,
                    CreatedDate = storedRow.CreatedDate,
                    Amount = episode.Amount,
                    RestAmount = episode.RestAmount,
                    Status = episode.Status,
                    SettledByBookingId = episode.SettledByBookingId,
                    SettledDate = episode.SettledDate,
                },
                false));
        }

        return changes;
    }

    private static SettlementPosition CreateRow(
        SettlementPositionState episode, Func<string> newIdFactory)
    {
        return new SettlementPosition
        {
            Id = newIdFactory(),
            SourceAccountId = episode.SourceAccountId,
            TargetPoolId = episode.TargetPoolId,
            CreatedByBookingId = episode.CreatedByBookingId,
            CreatedDate = episode.CreatedDate,
            Amount = episode.Amount,
            RestAmount = episode.RestAmount,
            Status = episode.Status,
            SettledByBookingId = episode.SettledByBookingId,
            SettledDate = episode.SettledDate,
        };
    }
}
