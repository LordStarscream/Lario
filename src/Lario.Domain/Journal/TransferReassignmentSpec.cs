using Lario.Domain.MasterData;

namespace Lario.Domain.Journal;

/// <summary>
/// Vorgabedaten der Kombination aus Transfer und Umwidmung
/// (Rechenvertrag §5.2: „Kombination aus Transfer und Umwidmung ist ein
/// eigener Vorgangstyp mit einer atomaren Buchung"): Account−/Account+
/// (Kontotransfer) und Pool−/Pool+ (Umwidmung) mit demselben Betrag in
/// einer Buchung. Die Kombination trägt keine Zusatzverteilungen — sie
/// ist vollständig aus ihren Postings ableitbar (Zelle
/// (Quellkonto, Quellpool) −Betrag, Zelle (Zielkonto, Zielpool) +Betrag).
/// </summary>
public sealed class TransferReassignmentSpec
{
    /// <summary>Stabile Identität des Vorgangs (Idempotenzschlüssel).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Fachdatum des Vorgangs.</summary>
    public DateOnly Date { get; init; }

    /// <summary>Quellkonto (Account−).</summary>
    public string FromAccountId { get; init; } = string.Empty;

    /// <summary>Zielkonto (Account+).</summary>
    public string ToAccountId { get; init; } = string.Empty;

    /// <summary>Quellpool (Pool−).</summary>
    public string FromPoolId { get; init; } = string.Empty;

    /// <summary>Zielpool (Pool+).</summary>
    public string ToPoolId { get; init; } = string.Empty;

    /// <summary>Betrag in Cent, positiv (gleicher Betrag für Konto- und Poolseite).</summary>
    public Money Amount { get; init; }

    /// <summary>Freitext, optional.</summary>
    public string? Note { get; init; }

    /// <summary>Erwartete Basisrevision (Rechenvertrag §5.5; wird mitgeführt).</summary>
    public int? ExpectedBaseRevision { get; init; }

    /// <summary>
    /// Prüfungen der Vorgabe gegen den Stammdaten-Katalog: positiver
    /// Betrag; Konten und Pools existierend und aktiv; Quell- und
    /// Zielkonto verschieden; Quell- und Zielpool verschieden.
    /// Journalbezogene Prüfungen (Kontostand trägt den Betrag) liegen im
    /// Anwendungsfall.
    /// </summary>
    public void Validate(MasterDataCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new LarioDomainException("Kombination ohne Id.");
        }

        if (Amount.Cents <= 0)
        {
            throw new LarioDomainException(
                $"Kombination '{Id}': Betrag muss positiv sein (ist {Amount.ToEuro():0.00} €).");
        }

        var fromAccount = catalog.FindAccount(FromAccountId)
            ?? throw new InvalidReferenceException(
                $"Kombination '{Id}' referenziert unbekanntes Quellkonto '{FromAccountId}'.");

        var toAccount = catalog.FindAccount(ToAccountId)
            ?? throw new InvalidReferenceException(
                $"Kombination '{Id}' referenziert unbekanntes Zielkonto '{ToAccountId}'.");

        if (fromAccount.Status != EntityStatus.Active)
        {
            throw new InvalidReferenceException(
                $"Kombination '{Id}' referenziert das archivierte Quellkonto '{FromAccountId}': " +
                "archivierte Entitäten liefern keine neuen Referenzen.");
        }

        if (toAccount.Status != EntityStatus.Active)
        {
            throw new InvalidReferenceException(
                $"Kombination '{Id}' referenziert das archivierte Zielkonto '{ToAccountId}': " +
                "archivierte Entitäten liefern keine neuen Referenzen.");
        }

        if (FromAccountId == ToAccountId)
        {
            throw new LarioDomainException(
                $"Kombination '{Id}': Quell- und Zielkonto müssen verschieden sein.");
        }

        var fromPool = catalog.FindPool(FromPoolId)
            ?? throw new InvalidReferenceException(
                $"Kombination '{Id}' referenziert unbekannten Quellpool '{FromPoolId}'.");

        var toPool = catalog.FindPool(ToPoolId)
            ?? throw new InvalidReferenceException(
                $"Kombination '{Id}' referenziert unbekannten Zielpool '{ToPoolId}'.");

        if (fromPool.Status != EntityStatus.Active)
        {
            throw new InvalidReferenceException(
                $"Kombination '{Id}' referenziert den archivierten Quellpool '{FromPoolId}': " +
                "archivierte Entitäten liefern keine neuen Referenzen.");
        }

        if (toPool.Status != EntityStatus.Active)
        {
            throw new InvalidReferenceException(
                $"Kombination '{Id}' referenziert den archivierten Zielpool '{ToPoolId}': " +
                "archivierte Entitäten liefern keine neuen Referenzen.");
        }

        if (FromPoolId == ToPoolId)
        {
            throw new LarioDomainException(
                $"Kombination '{Id}': Quell- und Zielpool müssen verschieden sein.");
        }
    }

    /// <summary>
    /// Konvertiert die geprüfte Vorgabe in eine Buchung: exakt ein
    /// negatives und ein positives AccountPosting (Kontotransfer) sowie
    /// ein negatives und ein positives PoolPosting (Umwidmung), jeweils
    /// mit demselben Betrag. Keine Zusatzverteilungen.
    /// </summary>
    public Booking ToBooking()
    {
        return new Booking(
            Id,
            BookingType.TransferReassignment,
            Date,
            Note,
            [
                new AccountPosting { AccountId = FromAccountId, Amount = Amount.Negate() },
                new AccountPosting { AccountId = ToAccountId, Amount = Amount },
            ],
            [
                new PoolPosting { PoolId = FromPoolId, Amount = Amount.Negate() },
                new PoolPosting { PoolId = ToPoolId, Amount = Amount },
            ]);
    }
}
