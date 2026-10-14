using Lario.Domain.MasterData;

namespace Lario.Domain.Journal;

/// <summary>
/// Zeile einer Händlererstattung: ein Ursprungs-Pool der Ausgabe und der
/// positive Betrag, der an diesen Pool zurückfließt (Design Schritt 6
/// §2.3; Rechenvertrag §4: Rückfluss zum ursprünglichen Pool, keine
/// Einnahmeverteilung).
/// </summary>
public sealed record RefundLineSpec(string PoolId, Money Amount);

/// <summary>
/// Erstattungs-Vorgabe (Rechenvertrag §4, Design Schritt 6 §2.3):
/// Händlererstattung als eigene Buchung (<see cref="BookingType.Refund"/>):
/// positiver Eingang am Erstattungstag auf dem Gutschriftskonto (darf
/// anders sein als das Originalkonto) und Rückfluss der Zeilen an die
/// ursprünglichen Pools der Ausgabe. Keine Einnahme — kein
/// Finanzierungsmonat, keine Verteilung, keine Kategorien;
/// <see cref="Booking.TotalIncome"/>-artige Ableitungen zählen sie nicht
/// an. Teilrückzahlungen sind möglich (z. B. 10 € von 85 €). Die
/// Deckelung gegen bereits erteilte Erstattungen (bereits erteilt + neue
/// Zeile ≤ Splitbetrag der Ausgabe je Pool) macht der Service (Design
/// Schritt 6 §3.2); die Revision der Ursprungs-Ausgabe wird nicht erhöht.
/// </summary>
public sealed record RefundSpec(
    /// <summary>Stabile Identität der neuen Erstattungs-Buchung (Idempotenzschlüssel).</summary>
    string Id,
    /// <summary>Stabile Identität der Ursprungs-Ausgabe.</summary>
    string OriginalExpenseId,
    /// <summary>Konto der Gutschrift (Account +); darf anders sein als das Originalkonto.</summary>
    string AccountId,
    /// <summary>Erstattungstag (Fachdatum des tatsächlichen Eingangs).</summary>
    DateOnly Date,
    /// <summary>Zeilen: Rückfluss der Zeilenbeträge an die ursprünglichen Pools (mindestens eine).</summary>
    IReadOnlyList<RefundLineSpec> Lines,
    /// <summary>Freitext, optional.</summary>
    string? Note = null)
{
    /// <summary>
    /// Prüfungen der Erstattung gegen Stammdaten-Katalog und Original
    /// (Design Schritt 6 §2.3/§3.2): Ids nicht leer, die Erstattung
    /// verknüpft sich nicht mit sich selbst, Original-Id ==
    /// <see cref="OriginalExpenseId"/> und das Original ist eine Ausgabe;
    /// Gutschriftskonto existierend und aktiv; Zeilen nicht leer mit
    /// eindeutigen, aktiven Pools, positiven Beträgen und Pools ⊆
    /// Ursprungs-Pools der Ausgabe. Die Deckelung gegen bereits erteilte
    /// Erstattungen macht der Service.
    /// </summary>
    public void Validate(MasterDataCatalog catalog, Booking original)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(original);

        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new LarioDomainException("Erstattung ohne Id.");
        }

        if (string.IsNullOrWhiteSpace(OriginalExpenseId))
        {
            throw new LarioDomainException(
                $"Erstattung '{Id}' ohne Ursprungs-Ausgabe (OriginalExpenseId fehlt).");
        }

        if (Id == OriginalExpenseId)
        {
            throw new LarioDomainException(
                $"Erstattung '{Id}' verknüpft sich mit sich selbst — die Ursprungs-Ausgabe muss eine andere Buchung sein.");
        }

        if (original.Id != OriginalExpenseId)
        {
            throw new InvalidReferenceException(
                $"Erstattung '{Id}' für Ausgabe '{OriginalExpenseId}', das Original trägt die Id '{original.Id}': " +
                "der Bezug der Erstattung ist falsch.");
        }

        if (string.IsNullOrWhiteSpace(AccountId))
        {
            throw new LarioDomainException($"Erstattung '{Id}' ohne Gutschriftskonto.");
        }

        if (original.Type != BookingType.Expense)
        {
            throw new LarioDomainException(
                $"Erstattung '{Id}': Original '{original.Id}' ist keine Ausgabe (Buchungstyp {original.Type}) — " +
                "erstattungsfähig sind nur Ausgaben.");
        }

        var account = catalog.FindAccount(AccountId)
            ?? throw new InvalidReferenceException(
                $"Erstattung '{Id}' referenziert unbekanntes Konto '{AccountId}'.");

        if (account.Status != EntityStatus.Active)
        {
            throw new InvalidReferenceException(
                $"Erstattung '{Id}' referenziert das archivierte Konto '{AccountId}': " +
                "archivierte Entitäten liefern keine neuen Referenzen.");
        }

        if (Lines.Count == 0)
        {
            throw new LarioDomainException(
                $"Erstattung '{Id}' ohne Zeilen — der Rückfluss an die ursprünglichen Pools gehört zur Erstattung.");
        }

        var originalPools = original.PoolPostings
            .Select(p => p.PoolId)
            .ToHashSet(StringComparer.Ordinal);

        var seenPools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in Lines)
        {
            if (string.IsNullOrWhiteSpace(line.PoolId))
            {
                throw new LarioDomainException($"Erstattung '{Id}' mit Zeile ohne Pool-Id.");
            }

            if (line.Amount.Cents <= 0)
            {
                throw new LarioDomainException(
                    $"Erstattung '{Id}': Zeilenbetrag für Pool '{line.PoolId}' muss positiv sein " +
                    $"(ist {line.Amount.ToEuro():0.00} €).");
            }

            var pool = catalog.FindPool(line.PoolId)
                ?? throw new InvalidReferenceException(
                    $"Erstattung '{Id}': Zeile referenziert unbekannten Pool '{line.PoolId}'.");

            if (pool.Status != EntityStatus.Active)
            {
                throw new InvalidReferenceException(
                    $"Erstattung '{Id}': Zeile referenziert den archivierten Pool '{line.PoolId}': " +
                    "archivierte Entitäten liefern keine neuen Referenzen.");
            }

            if (!seenPools.Add(line.PoolId))
            {
                throw new LarioDomainException(
                    $"Erstattung '{Id}': Pool '{line.PoolId}' kommt mehrfach in den Zeilen vor " +
                    "(eine Erstattung fasst einen Pool in einer Zeile auf).");
            }

            if (!originalPools.Contains(line.PoolId))
            {
                throw new LarioDomainException(
                    $"Erstattung '{Id}': Pool '{line.PoolId}' ist kein Ursprungs-Pool der Ausgabe '{original.Id}' — " +
                    "der Rückfluss geht ausschließlich an die ursprünglichen Pools.");
            }
        }
    }

    /// <summary>
    /// Baut die Erstattungs-Buchung (atomare Buchungseinheit): ein
    /// Konto-Posting +Gesamtbetrag (Summe der Zeilen) am Gutschriftskonto
    /// und je Zeile ein positives Pool-Posting an den Ursprungs-Pool;
    /// <see cref="Booking.RefundsExpenseId"/> verknüpft die Ursprungs-
    /// Ausgabe. Kein Finanzierungsmonat, keine Teilposten, keine
    /// Zweck-/Umwidmungszeilen, keine Storno-Verknüpfung.
    /// </summary>
    public Booking ToBooking(Booking original)
    {
        ArgumentNullException.ThrowIfNull(original);

        var total = Lines
            .Aggregate(Money.Zero, (sum, line) => sum.Add(line.Amount));

        var accountPostings = new List<AccountPosting>
        {
            new() { AccountId = AccountId, Amount = total },
        };

        var poolPostings = Lines
            .Select(line => new PoolPosting { PoolId = line.PoolId, Amount = line.Amount })
            .ToList();

        return new Booking(
            Id,
            BookingType.Refund,
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
            null,
            original.Id,
            null);
    }
}
