namespace Lario.Domain.Journal;

/// <summary>
/// Buchung: der kleinste unsplittbare Buchungssatz des Journals
/// (Rechenvertrag §3.2). Alle zusammengehörigen Postings werden atomar
/// gespeichert; eine Teilübernahme ist unmöglich.
///
/// Revision (Rechenvertrag §5.5): eine neue Buchung beginnt mit dem
/// einheitlichen Startwert 1. Die erwartete Basisrevision (expectedBaseRevision)
/// gehört zu Änderungen existierender Buchungen (Korrektur, Storno,
/// Sync-Operation) — nicht zu ihrer Erzeugung (ab Schritt 6/10).
/// </summary>
public sealed class Booking
{
    /// <summary>Stabile Identität der Buchung.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Typ des Buchungssatzes (siehe <see cref="BookingType"/>).</summary>
    public BookingType Type { get; init; }

    /// <summary>Fachdatum (tatsächliches Datum/Stichtag, ohne Zeitkomponente).</summary>
    public DateOnly Date { get; init; }

    /// <summary>Freitext, optional.</summary>
    public string? Note { get; init; }

    /// <summary>Eigene Basisrevision; Startwert 1 (Rechenvertrag §5.5).</summary>
    public int BaseRevision { get; set; } = 1;

    /// <summary>Kontobewegungen der Buchung (signierte Cent-Beträge).</summary>
    public List<AccountPosting> AccountPostings { get; init; } = [];

    /// <summary>Poolbewegungen der Buchung (signierte Cent-Beträge).</summary>
    public List<PoolPosting> PoolPostings { get; init; } = [];

    /// <summary>
    /// Erzeugt eine Buchung ohne Postings. Dieser Konstrutor ist
    /// EF-Compliant: EF Core kann Sammlungs-Navigationsparameter
    /// (<c>AccountPostings</c>, <c>PoolPostings</c>) nicht im
    /// Konstrutor binden und wählt daher diesen Konstrutor bei der
    /// Materialisierung; die Sammlungen werden danach per Navigation-
    /// Fixup gefüllt (Schatten-Fremdschlüssel im DbContext).
    /// </summary>
    public Booking(string id, BookingType type, DateOnly date, string? note = null)
        : this(id, type, date, note, null, null)
    {
    }

    /// <summary>
    /// Erzeugt eine Buchung mit ihren Postings (atomare Buchungseinheit).
    /// </summary>
    public Booking(
        string id,
        BookingType type,
        DateOnly date,
        string? note,
        IReadOnlyList<AccountPosting>? accountPostings,
        IReadOnlyList<PoolPosting>? poolPostings)
    {
        Id = id;
        Type = type;
        Date = date;
        Note = note;
        AccountPostings = (accountPostings ?? []).ToList();
        PoolPostings = (poolPostings ?? []).ToList();
        ValidatePostings();
    }

    /// <summary>
    /// Prüfungen der Postings: keine leeren Referenzen, je Buchung und
    /// Konto/Pool maximal eine Posting-Zelle (eine Buchung fasst dieselbe
    /// Zelle nicht doppelt auf).
    /// </summary>
    private void ValidatePostings()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new LarioDomainException("Buchung ohne Id.");
        }

        var accounts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var posting in AccountPostings)
        {
            if (string.IsNullOrWhiteSpace(posting.AccountId))
            {
                throw new LarioDomainException($"Buchung '{Id}' mit AccountPosting ohne Konto-Id.");
            }

            if (!accounts.Add(posting.AccountId))
            {
                throw new LarioDomainException(
                    $"Buchung '{Id}': Konto '{posting.AccountId}' kommt mehrfach vor.");
            }
        }

        var pools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var posting in PoolPostings)
        {
            if (string.IsNullOrWhiteSpace(posting.PoolId))
            {
                throw new LarioDomainException($"Buchung '{Id}' mit PoolPosting ohne Pool-Id.");
            }

            if (!pools.Add(posting.PoolId))
            {
                throw new LarioDomainException(
                    $"Buchung '{Id}': Pool '{posting.PoolId}' kommt mehrfach vor.");
            }
        }
    }

    public override string ToString() => $"{Id} ({Type}, {Date:yyyy-MM-dd}, Rev {BaseRevision})";
}
