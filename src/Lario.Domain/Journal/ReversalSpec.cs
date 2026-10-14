namespace Lario.Domain.Journal;

/// <summary>
/// Storno-Vorgabe (Rechenvertrag §5.4, Design Schritt 6 §2.3): die
/// Gegenbewegung zu einer bestehenden Buchung. Der Storno ist eine eigene
/// Buchung (<see cref="BookingType.Reversal"/>): stabile eigene Id
/// (Idempotenzschlüssel), Verknüpfung zum Original
/// (<see cref="Booking.ReversesBookingId"/>), Konto- und Pool-Postings als
/// exakte Negation der Original-Postings und die negierten Original-Deltas
/// als <see cref="Booking.ReversalDeltas"/>. Das Original bleibt im
/// Journal; seine Revisionserhöhung, die Idempotenz- und die
/// Stornierbarkeitsprüfung (Buchungstyp) macht der Service (Design
/// Schritt 6 §3.2).
/// </summary>
public sealed record ReversalSpec(
    /// <summary>Stabile Identität der neuen Storno-Buchung (Idempotenzschlüssel).</summary>
    string Id,
    /// <summary>Stabile Identität der zu stornierenden Buchung (Original).</summary>
    string OriginalBookingId,
    /// <summary>
    /// Erwartete Basisrevision des Originals (Pflicht, Rechenvertrag
    /// §5.5): Abweichung von der Ist-Revision ist ein Revisionskonflikt
    /// (Fehler, kein Last-Write-Wins). Der Abgleich mit der Ist-Revision
    /// macht der Service.
    /// </summary>
    int ExpectedBaseRevision,
    /// <summary>Fachdatum des Stornos (rückdatiert zulässig).</summary>
    DateOnly Date,
    /// <summary>Freitext, optional.</summary>
    string? Note = null)
{
    /// <summary>
    /// Strukturprüfungen der Vorgabe: Ids nicht leer, der Storno verknüpft
    /// sich nicht mit sich selbst, erwartete Basisrevision mindestens 1.
    /// Existenz und Stornierbarkeit des Originals (Typ) sowie die
    /// Revision selbst prüft der Service.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new LarioDomainException("Storno ohne Id.");
        }

        if (string.IsNullOrWhiteSpace(OriginalBookingId))
        {
            throw new LarioDomainException(
                $"Storno '{Id}' ohne Original-Referenz (OriginalBookingId fehlt).");
        }

        if (Id == OriginalBookingId)
        {
            throw new LarioDomainException(
                $"Storno '{Id}' verknüpft sich mit sich selbst — das Original muss eine andere Buchung sein.");
        }

        if (ExpectedBaseRevision < 1)
        {
            throw new LarioDomainException(
                $"Storno '{Id}': erwartete Basisrevision muss mindestens 1 sein (ist {ExpectedBaseRevision}).");
        }
    }

    /// <summary>
    /// Baut die Storno-Buchung aus dem Original (atomare Buchungseinheit):
    /// Konto- und Pool-Postings sind die exakte Negation der
    /// Original-Postings (gleiche Referenzen, <c>Amount.Negate()</c>); die
    /// Reversal-Deltas sind die Negation von
    /// <c>original.AllocationDelta()</c> (gleiche Konto-/Pool-Ids,
    /// negierte Beträge) — damit gilt die Invariante
    /// <c>ReversalDeltas == Negation(Original.AllocationDelta())</c>
    /// (Design Schritt 6 §2.2). Kein Finanzierungsmonat, keine Teilposten,
    /// keine Zweck-/Umwidmungszeilen, keine Ausgleichsreferenz.
    /// </summary>
    public Booking ToBooking(Booking original)
    {
        ArgumentNullException.ThrowIfNull(original);

        var accountPostings = original.AccountPostings
            .Select(p => new AccountPosting { AccountId = p.AccountId, Amount = p.Amount.Negate() })
            .ToList();

        var poolPostings = original.PoolPostings
            .Select(p => new PoolPosting { PoolId = p.PoolId, Amount = p.Amount.Negate() })
            .ToList();

        var reversalDeltas = original.AllocationDelta()
            .Select(d => new AllocationDeltaEntry(d.AccountId, d.PoolId, d.Amount.Negate()))
            .ToList();

        return new Booking(
            Id,
            BookingType.Reversal,
            Date,
            Note,
            accountPostings,
            poolPostings,
            null,
            null,
            null,
            null,
            null,
            null,
            original.Id,
            null,
            reversalDeltas);
    }
}
