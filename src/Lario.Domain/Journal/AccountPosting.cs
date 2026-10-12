namespace Lario.Domain.Journal;

/// <summary>
/// Kontobewegung einer Buchung (Rechenvertrag §3.3): signierter Cent-Betrag
/// auf einem Konto. Positive Beträge erhöhen den Kontostand, negative
/// Beträge senken ihn. Die Zuordnung zur Buchung erfolgt über die
/// Sammlungsbeziehung in <see cref="Booking"/>.
/// </summary>
public sealed class AccountPosting
{
    /// <summary>Konto, auf dem die Bewegung liegt.</summary>
    public string AccountId { get; init; } = string.Empty;

    /// <summary>Signierter Betrag in Cent.</summary>
    public Money Amount { get; init; }
}
