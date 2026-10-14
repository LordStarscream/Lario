namespace Lario.Infrastructure;

/// <summary>
/// Persistente Zeile eines Storno-Deltas (Design Schritt 6 §2.1/§2.2):
/// die explizit gespeicherte negierte Konto-Pool-Zuordnung des Originals.
/// Im Journal-Replay liefert der Storno diese Einträge ohne
/// Cross-Booking-Lookup auf das Original. Die Domain kennt das als
/// <see cref="Lario.Domain.Journal.AllocationDeltaEntry"/>; das Mapping
/// zwischen beiden obliegt dem <see cref="BookingRepository"/>.
/// </summary>
public sealed class ReversalDeltaRow
{
    public ReversalDeltaRow()
    {
    }

    public ReversalDeltaRow(string bookingId, string accountId, string poolId, long amount)
    {
        BookingId = bookingId;
        AccountId = accountId;
        PoolId = poolId;
        Amount = amount;
    }

    /// <summary>Id der stornierenden Buchung (Teil des Primärschlüssels).</summary>
    public string BookingId { get; init; } = string.Empty;

    /// <summary>Konto der Delta-Zelle (Teil des Primärschlüssels).</summary>
    public string AccountId { get; init; } = string.Empty;

    /// <summary>Pool der Delta-Zelle (Teil des Primärschlüssels).</summary>
    public string PoolId { get; init; } = string.Empty;

    /// <summary>Negierter Betrag der Delta-Zelle in Cent.</summary>
    public long Amount { get; init; }
}
