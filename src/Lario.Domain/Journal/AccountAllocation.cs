namespace Lario.Domain.Journal;

/// <summary>
/// Eine Zeile der Kontoverteilung einer Umwidmung (Rechenvertrag §5.2,
/// Schritt 5): Betrag, der physisch auf dem Konto bleibt und nur seinen
/// Zweck wechselt. Die Umwidmung selbst hat keine Konto-Bewegung — die
/// Zeilen sagen, auf welchen Konten die umgewidmeten Mittel liegen,
/// damit die Konto-Pool-Zuordnung abgeleitet werden kann.
/// </summary>
public sealed record AccountAllocation(string AccountId, Money Amount);
