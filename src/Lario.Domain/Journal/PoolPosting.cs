namespace Lario.Domain.Journal;

/// <summary>
/// Poolbewegung einer Buchung (Rechenvertrag §3.4): signierter Cent-Betrag
/// auf einem Pool. Positive Beträge erhöhen den Poolbestand, negative
/// Beträge senken ihn. Die Zuordnung zur Buchung erfolgt über die
/// Sammlungsbeziehung in <see cref="Booking"/>.
/// </summary>
public sealed class PoolPosting
{
    /// <summary>Pool, auf dem die Bewegung liegt.</summary>
    public string PoolId { get; init; } = string.Empty;

    /// <summary>Signierter Betrag in Cent.</summary>
    public Money Amount { get; init; }
}
