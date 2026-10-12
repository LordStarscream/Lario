using Lario.Domain.MasterData;

namespace Lario.Domain.Journal;

/// <summary>
/// Vorgabedaten eines reinen Kontotransfers (Rechenvertrag §3.3, Plan
/// Schritt 5): Account− (Quelle sinkt), Account+ (Ziel steigt); der
/// Gesamtpoolbestand bleibt unverändert — keine Pool-Postings, keine
/// Ausgabe, keine Kategorie, kein Finanzierungsmonat.
///
/// Der Zweck bleibt erhalten (§3.3): <see cref="Purposes"/> trägt die
/// Zweckverteilung der gewanderten Mittel (je Zeile Pool + Zeilenbetrag).
/// Ist <see cref="Purposes"/> null, ist der Zweck <b>implizit</b>: die
/// gewanderten Mittel behalten den Zweck, den sie am Quellkonto hatten —
/// das ist eindeutig, wenn die Zuordnung des Quellkontos (vor der Buchung)
/// exakt einen positiven Pool trägt, dessen Betrag den Transferbetrag
/// trägt. Die Auflösung (Journal nötig) und Materialisierung in die Buchung
/// obliegen dem Anwendungsfall; die Buchung trägt danach immer eine
/// vollständige Zweckverteilung, damit die Konto-Pool-Zuordnung aus den
/// Postings rekonstruierbar bleibt (Rechenvertrag §3.4).
///
/// Die expliziten Zweckzeilen summieren sich exakt zum Transferbetrag
/// (Splitregel). Optional referenziert der Transfer eine offene
/// Ausgleichsposition (<see cref="RelatedSettlementId"/>): dann ist er
/// eine (Teil-)Begleichung der internen Vorfinanzierung (Rückfluss,
/// Rechenvertrag §3.5).
/// </summary>
public sealed class TransferSpec
{
    /// <summary>Stabile Identität des Transfers (Idempotenzschlüssel).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Fachdatum des Transfers.</summary>
    public DateOnly Date { get; init; }

    /// <summary>Quellkonto (Account−).</summary>
    public string FromAccountId { get; init; } = string.Empty;

    /// <summary>Zielkonto (Account+).</summary>
    public string ToAccountId { get; init; } = string.Empty;

    /// <summary>Transferbetrag in Cent, positiv.</summary>
    public Money Amount { get; init; }

    /// <summary>
    /// Zweckverteilung der gewanderten Mittel; null = impliziter Zweck
    /// („der Zweck bleibt erhalten“, Auflösung im Anwendungsfall).
    /// Bei expliziter Angabe mindestens eine Zeile; die Summe der
    /// Zeilenbeträge muss exakt <see cref="Amount"/> betragen.
    /// </summary>
    public IReadOnlyList<PoolAllocation>? Purposes { get; init; }

    /// <summary>
    /// Offene Ausgleichsposition, die dieser Transfer (teilweise) begleicht;
    /// null = reiner Transfer ohne Begleichung.
    /// </summary>
    public string? RelatedSettlementId { get; init; }

    /// <summary>Freitext, optional.</summary>
    public string? Note { get; init; }

    /// <summary>Erwartete Basisrevision (Rechenvertrag §5.5; wird mitgeführt).</summary>
    public int? ExpectedBaseRevision { get; init; }

    /// <summary>
    /// Prüfungen der Vorgabe gegen den Stammdaten-Katalog: positiver
    /// Betrag; Quell- und Zielkonto existierend, aktiv und verschieden;
    /// explizite Zweckverteilung mit eindeutigen aktiven Pools und exakter
    /// Summe (Splitregel). Journalbezogene Prüfungen (Kontostand trägt den
    /// Betrag, impliziter Zweck eindeutig, Begleichungsinvarianten) liegen
    /// im Anwendungsfall.
    /// </summary>
    public void Validate(MasterDataCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new LarioDomainException("Transfer ohne Id.");
        }

        if (Amount.Cents <= 0)
        {
            throw new LarioDomainException(
                $"Transfer '{Id}': Betrag muss positiv sein (ist {Amount.ToEuro():0.00} €).");
        }

        var from = catalog.FindAccount(FromAccountId)
            ?? throw new InvalidReferenceException(
                $"Transfer '{Id}' referenziert unbekanntes Quellkonto '{FromAccountId}'.");

        var to = catalog.FindAccount(ToAccountId)
            ?? throw new InvalidReferenceException(
                $"Transfer '{Id}' referenziert unbekanntes Zielkonto '{ToAccountId}'.");

        if (from.Status != EntityStatus.Active)
        {
            throw new InvalidReferenceException(
                $"Transfer '{Id}' referenziert das archivierte Quellkonto '{FromAccountId}': " +
                "archivierte Entitäten liefern keine neuen Referenzen.");
        }

        if (to.Status != EntityStatus.Active)
        {
            throw new InvalidReferenceException(
                $"Transfer '{Id}' referenziert das archivierte Zielkonto '{ToAccountId}': " +
                "archivierte Entitäten liefern keine neuen Referenzen.");
        }

        if (FromAccountId == ToAccountId)
        {
            throw new LarioDomainException(
                $"Transfer '{Id}': Quell- und Zielkonto müssen verschieden sein.");
        }

        if (Purposes is not null)
        {
            if (Purposes.Count == 0)
            {
                throw new LarioDomainException(
                    $"Transfer '{Id}' mit leerer Zweckverteilung: entweder explizite " +
                    "Zweckzeilen angeben oder null (impliziter Zweck).");
            }

            var seenPools = new HashSet<string>(StringComparer.Ordinal);
            var purposeSum = Money.Zero;
            foreach (var purpose in Purposes)
            {
                if (string.IsNullOrWhiteSpace(purpose.PoolId))
                {
                    throw new LarioDomainException($"Transfer '{Id}' mit Zweckzeile ohne Pool-Id.");
                }

                if (!seenPools.Add(purpose.PoolId))
                {
                    throw new LarioDomainException(
                        $"Transfer '{Id}': Pool '{purpose.PoolId}' kommt mehrfach in der " +
                        "Zweckverteilung vor (ein Transfer fasst einen Pool in einer Zeile auf).");
                }

                if (purpose.Amount.Cents <= 0)
                {
                    throw new LarioDomainException(
                        $"Transfer '{Id}': Zweckzeile '{purpose.PoolId}' muss positiv sein " +
                        $"(ist {purpose.Amount.ToEuro():0.00} €).");
                }

                var pool = catalog.FindPool(purpose.PoolId)
                    ?? throw new InvalidReferenceException(
                        $"Transfer '{Id}' referenziert unbekannten Pool '{purpose.PoolId}'.");

                if (pool.Status != EntityStatus.Active)
                {
                    throw new InvalidReferenceException(
                        $"Transfer '{Id}' referenziert den archivierten Pool '{purpose.PoolId}': " +
                        "archivierte Entitäten liefern keine neuen Referenzen.");
                }

                purposeSum = purposeSum.Add(purpose.Amount);
            }

            if (purposeSum != Amount)
            {
                throw new LarioDomainException(
                    $"Transfer '{Id}': Zweckverteilung ({purposeSum.ToEuro():0.00} €) addiert nicht " +
                    $"exakt zum Transferbetrag ({Amount.ToEuro():0.00} €) — es gibt keinen " +
                    "nicht zugeordneten Rest.");
            }
        }
    }

    /// <summary>
    /// Konvertiert die geprüfte Vorgabe in eine Buchung: zwei Konto-Postings
    /// (Quelle −Betrag, Ziel +Betrag), keine Pool-Postings (Pools bleiben
    /// unverändert) und die <b>wirksame</b> Zweckverteilung als
    /// Buchungsinhalt. <paramref name="effectivePurposes"/> ist die
    /// aufgelöste Zweckverteilung (explizit oder vom Anwendungsfall aus
    /// dem Journal abgeleitet); sie muss die gleichen Prüfungen erfüllen
    /// wie <see cref="Purposes"/> (eindeutige Pools, positive Zeilen,
    /// exakte Summe) und wird hier erneut geprüft.
    /// </summary>
    public Booking ToBooking(IReadOnlyList<PoolAllocation> effectivePurposes)
    {
        ArgumentNullException.ThrowIfNull(effectivePurposes);
        ValidatePurposes(effectivePurposes, Purposes);

        return new Booking(
            Id,
            BookingType.AccountTransfer,
            Date,
            Note,
            [
                new AccountPosting { AccountId = FromAccountId, Amount = Amount.Negate() },
                new AccountPosting { AccountId = ToAccountId, Amount = Amount },
            ],
            [],
            transferPurposes: effectivePurposes.ToList(),
            relatedSettlementId: RelatedSettlementId);
    }

    /// <summary>
    /// Prüft die wirksame Zweckverteilung; bei explizit angegebenen
    /// Zeilen muss die wirksame Verteilung exakt die Angabe widerspiegeln
    /// (gleiche Zeilen, Reihenfolge egal) — eine Stille Ersetzung wäre
    /// ein Inhaltswechsel.
    /// </summary>
    private void ValidatePurposes(IReadOnlyList<PoolAllocation> effective, IReadOnlyList<PoolAllocation>? explicitPurposes)
    {
        if (effective.Count == 0)
        {
            throw new LarioDomainException($"Transfer '{Id}' ohne wirksame Zweckverteilung.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sum = Money.Zero;
        foreach (var purpose in effective)
        {
            if (!seen.Add(purpose.PoolId))
            {
                throw new LarioDomainException(
                    $"Transfer '{Id}': Pool '{purpose.PoolId}' kommt mehrfach in der " +
                    "wirksamen Zweckverteilung vor.");
            }

            if (purpose.Amount.Cents <= 0)
            {
                throw new LarioDomainException(
                    $"Transfer '{Id}': wirksame Zweckzeile '{purpose.PoolId}' muss positiv sein.");
            }

            sum = sum.Add(purpose.Amount);
        }

        if (sum != Amount)
        {
            throw new LarioDomainException(
                $"Transfer '{Id}': wirksame Zweckverteilung ({sum.ToEuro():0.00} €) addiert nicht " +
                $"exakt zum Transferbetrag ({Amount.ToEuro():0.00} €).");
        }

        if (explicitPurposes is not null)
        {
            var explicitLines = explicitPurposes
                .OrderBy(p => p.PoolId, StringComparer.Ordinal)
                .Select(p => (p.PoolId, p.Amount))
                .ToList();
            var effectiveLines = effective
                .OrderBy(p => p.PoolId, StringComparer.Ordinal)
                .Select(p => (p.PoolId, p.Amount))
                .ToList();
            if (!explicitLines.SequenceEqual(effectiveLines))
            {
                throw new LarioDomainException(
                    $"Transfer '{Id}': die wirksame Zweckverteilung weicht von der " +
                    "explizit angegebenen ab — implizite Auflösung ist nur ohne Angabe erlaubt.");
            }
        }
    }
}
