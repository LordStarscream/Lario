namespace Lario.Domain.Journal;

/// <summary>
/// Saldenabfragen über das Buchungsjournal (Rechenvertrag §3.2:
/// „Abfragen berechnen Salden aus den Grunddaten. Es gibt keine
/// gesondert gepflegten Saldo-Felder. Abgeleitete Salden müssen jederzeit
/// rekonstruierbar bleiben").
///
/// Der Journal-Stand ist ein Snapshot (Liste aller Buchungen); Abfragen
/// sind deterministisch und wiederholbar.
/// </summary>
public sealed class BookingJournal
{
    private readonly IReadOnlyList<Booking> _bookings;

    public BookingJournal(IEnumerable<Booking> bookings)
    {
        _bookings = bookings?.ToList() ?? [];
    }

    /// <summary>Alle Buchungen im Journal.</summary>
    public IReadOnlyList<Booking> Bookings => _bookings;

    /// <summary>Kontostand: Zeilensumme der AccountPostings des Kontos.</summary>
    public Money AccountBalance(string accountId) =>
        _bookings
            .SelectMany(b => b.AccountPostings)
            .Where(p => p.AccountId == accountId)
            .Aggregate(Money.Zero, (sum, p) => sum.Add(p.Amount));

    /// <summary>Poolbestand: Spaltensumme der PoolPostings des Pools.</summary>
    public Money PoolBalance(string poolId) =>
        _bookings
            .SelectMany(b => b.PoolPostings)
            .Where(p => p.PoolId == poolId)
            .Aggregate(Money.Zero, (sum, p) => sum.Add(p.Amount));

    /// <summary>Summe aller Kontostände (Gesamtvermögen).</summary>
    public Money TotalAccountBalance() =>
        _bookings
            .SelectMany(b => b.AccountPostings)
            .Aggregate(Money.Zero, (sum, p) => sum.Add(p.Amount));

    /// <summary>Summe aller Poolbestände.</summary>
    public Money TotalPoolBalance() =>
        _bookings
            .SelectMany(b => b.PoolPostings)
            .Aggregate(Money.Zero, (sum, p) => sum.Add(p.Amount));

    /// <summary>
    /// Normale Einnahmen (Buchungstyp <see cref="BookingType.Income"/>).
    /// Anfangsbestände sind keine Einnahmen und zählen hier nicht.
    /// </summary>
    public Money TotalIncome() =>
        _bookings
            .Where(b => b.Type == BookingType.Income)
            .SelectMany(b => b.AccountPostings)
            .Aggregate(Money.Zero, (sum, p) => sum.Add(p.Amount));

    /// <summary>
    /// Nicht zugeordneter Rest eines Anfangsbestands (Systemposition
    /// „noch nicht zugeordnet"): Total − Summe der PoolPostings. Bleibt
    /// sichtbar und zählt nicht automatisch zu Flex.
    /// </summary>
    public Money OpeningBalanceUnallocated(string openingBalanceId)
    {
        var booking = _bookings.FirstOrDefault(b => b.Id == openingBalanceId)
            ?? throw new InvalidReferenceException(
                $"Unbekannter Anfangsbestand '{openingBalanceId}'.");

        if (booking.Type != BookingType.OpeningBalance)
        {
            throw new LarioDomainException(
                $"Buchung '{openingBalanceId}' ist kein Anfangsbestand (Typ {booking.Type}).");
        }

        var total = booking.AccountPostings
            .Aggregate(Money.Zero, (sum, p) => sum.Add(p.Amount));
        var allocated = booking.PoolPostings
            .Aggregate(Money.Zero, (sum, p) => sum.Add(p.Amount));

        return total.Subtract(allocated);
    }

    /// <summary>Alle Anfangsbestände des Journals.</summary>
    public IReadOnlyList<Booking> OpeningBalances() =>
        _bookings.Where(b => b.Type == BookingType.OpeningBalance).ToList();

    /// <summary>
    /// Invariante der signierten Konto-Pool-Zuordnung (Rechenvertrag §3.4):
    /// Summe der Kontostände = Summe der Poolbestände. Für Anfangsbestände
    /// gilt das nur, wenn die Verteilung vollständig ist — der nicht
    /// zugeordnete Rest erklärt die Differenz.
    /// </summary>
    public Money AccountPoolGap() =>
        TotalAccountBalance().Subtract(TotalPoolBalance());
}
