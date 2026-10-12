using Lario.Domain.MasterData;

namespace Lario.Domain.Journal;

/// <summary>
/// Vorgabedaten eines Anfangsbestands (Rechenvertrag §3.7):
/// - <see cref="Date"/> ist der Stichtag (fachliches Datum ohne Zeit).
/// - <see cref="AccountId"/> ist das Konto, auf dem der Betrag liegt.
/// - <see cref="Total"/> ist der Gesamtbetrag auf dem Konto.
/// - <see cref="Distribution"/> ist die explizite Verteilung auf Pools —
///   Teil der Vorgabedaten, kein Nachfassen über Einnahme-Logik.
/// - Der nicht verteilte Rest (<see cref="Unallocated"/>) bleibt als
///   sichtbare Systemposition „noch nicht zugeordnet" und zählt nicht
///   automatisch zu Flex.
///
/// Der Anfangsbestand ist <b>keine Einnahme</b>: kein Finanzierungsmonat,
/// keine Deckungsberechnung, keine Zuweisung über Einnahme-Logik.
/// </summary>
public sealed record OpeningBalanceSpec(
    string Id,
    DateOnly Date,
    string AccountId,
    Money Total,
    IReadOnlyList<PoolAllocation> Distribution,
    string? Note = null)
{
    /// <summary>Summe der explizit verteilten Beträge.</summary>
    public Money AllocatedTotal =>
        Distribution.Aggregate(Money.Zero, (sum, line) => sum.Add(line.Amount));

    /// <summary>
    /// Sichtbarer Rest „noch nicht zugeordnet": Total − verteilte Beträge.
    /// Keine eigene Buchung, abgeleitet aus der Verteilung.
    /// </summary>
    public Money Unallocated => Total.Subtract(AllocatedTotal);

    /// <summary>
    /// Erzeugt die zugehörige Buchung: eine Konto-Belastung/Erhöhung um
    /// den Gesamtbetrag und je Verteilungszelle eine Poolbewegung. Der
    /// Rest bleibt als Ableitung (keine Poolposting).
    /// </summary>
    public Booking ToBooking()
    {
        var accountPostings = new List<AccountPosting>
        {
            new() { AccountId = AccountId, Amount = Total },
        };
        var poolPostings = Distribution
            .Select(line => new PoolPosting { PoolId = line.PoolId, Amount = line.Amount })
            .ToList();

        return new Booking(
            id: Id,
            type: BookingType.OpeningBalance,
            date: Date,
            note: Note,
            accountPostings: accountPostings,
            poolPostings: poolPostings);
    }

    /// <summary>
    /// Prüft die Vorgabedaten gegen den Stammdaten-Katalog:
    /// - Konto existiert und ist aktiv;
    /// - Pools existieren und sind aktiv, keine doppelte Pool-Zeile;
    /// - Total ≥ 0, alle Teilbeträge ≥ 0, Summe ≤ Total (Rest sichtbar).
    /// </summary>
    public void ValidateAgainst(MasterDataCatalog catalog)
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new LarioDomainException("Anfangsbestand ohne Id.");
        }

        var account = catalog.FindAccount(AccountId)
            ?? throw new InvalidReferenceException(
                $"Anfangsbestand '{Id}' referenziert unbekanntes Konto '{AccountId}'.");

        if (account.Status != EntityStatus.Active)
        {
            throw new InvalidReferenceException(
                $"Anfangsbestand '{Id}' referenziert archiviertes Konto '{AccountId}'.");
        }

        if (Total.Cents < 0)
        {
            throw new LarioDomainException(
                $"Anfangsbestand '{Id}' hat einen negativen Gesamtbetrag ({Total.ToEuro():0.00} €); " +
                "negative Anfangsbestände werden in Version 1 nicht unterstützt.");
        }

        var seenPools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in Distribution)
        {
            if (string.IsNullOrWhiteSpace(line.PoolId))
            {
                throw new LarioDomainException($"Anfangsbestand '{Id}' mit leerer Pool-Id in der Verteilung.");
            }

            var pool = catalog.FindPool(line.PoolId)
                ?? throw new InvalidReferenceException(
                    $"Anfangsbestand '{Id}' verteilt auf unbekannten Pool '{line.PoolId}'.");

            if (pool.Status != EntityStatus.Active)
            {
                throw new InvalidReferenceException(
                    $"Anfangsbestand '{Id}' verteilt auf archivierten Pool '{line.PoolId}'.");
            }

            if (line.Amount.Cents < 0)
            {
                throw new LarioDomainException(
                    $"Anfangsbestand '{Id}': Verteilung auf '{line.PoolId}' ist negativ ({line.Amount.ToEuro():0.00} €).");
            }

            if (!seenPools.Add(line.PoolId))
            {
                throw new LarioDomainException(
                    $"Anfangsbestand '{Id}': Pool '{line.PoolId}' kommt mehrfach in der Verteilung vor.");
            }
        }

        if (AllocatedTotal.Cents > Total.Cents)
        {
            throw new LarioDomainException(
                $"Anfangsbestand '{Id}': verteilte Beträge ({AllocatedTotal.ToEuro():0.00} €) " +
                $"übersteigen den Gesamtbetrag ({Total.ToEuro():0.00} €).");
        }
    }

    /// <summary>
    /// Inhaltliche Gleichheit zweier Anfangsbestands-Vorgaben (für die
    /// Idempotenzprüfung: gleiche Id + gleicher Inhalt = keine zweite
    /// Buchung; gleiche Id + anderer Inhalt = Konflikt).
    /// </summary>
    public bool HasSameContentAs(OpeningBalanceSpec other) =>
        Date == other.Date
        && AccountId == other.AccountId
        && Total == other.Total
        && Note == other.Note
        && Distribution.SequenceEqual(other.Distribution);
}
