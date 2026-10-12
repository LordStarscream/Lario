using Lario.Domain.MasterData;

namespace Lario.Domain.Journal;

/// <summary>
/// Vorgabedaten einer Umwidmung (Rechenvertrag §3.3, Plan Schritt 5):
/// Pool− (Quelle sinkt) und Pool+ (Ziel steigt); keine Konto-Bewegung,
/// kein Konto-Pool-Ausgleich, keine Ausgabe, keine Kategorie.
///
/// Das physisch tragende Konto bleibt unverändert — die
/// <see cref="Accounts"/> tragen die Kontoverteilung der umgewidmeten
/// Mittel (je Zeile Konto + Zeilenbetrag). Die Zeilen summieren sich
/// exakt zum Umwidmungsbetrag (Splitregel) und sind Teil der Buchung,
/// damit die Konto-Pool-Zuordnung abgeleitet werden kann (§3.4).
///
/// Ist <see cref="Accounts"/> null, ist die Kontoverteilung <b>implizit</b>:
/// die umgewidmeten Mittel liegen auf den Konten, die den Quellpool
/// tragen. Die Auflösung (Journal nötig) und Materialisierung in die
/// Buchung obliegen dem Anwendungsfall; die Buchung trägt danach immer
/// eine vollständige Kontoverteilung (Rechenvertrag §5.2).
///
/// Eine Umwidmung kann sofort gelten (bestätigt ohne Transfer), während
/// der Kontotransfer später separat bestätigt wird (Rechenvertrag §5.2).
/// </summary>
public sealed class ReassignmentSpec
{
    /// <summary>Stabile Identität der Umwidmung (Idempotenzschlüssel).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Fachdatum der Umwidmung.</summary>
    public DateOnly Date { get; init; }

    /// <summary>Quellpool (Pool−).</summary>
    public string FromPoolId { get; init; } = string.Empty;

    /// <summary>Zielpool (Pool+).</summary>
    public string ToPoolId { get; init; } = string.Empty;

    /// <summary>Umwidmungsbetrag in Cent, positiv.</summary>
    public Money Amount { get; init; }

    /// <summary>
    /// Kontoverteilung der umgewidmeten Mittel (mindestens eine Zeile):
    /// auf welchen Konten liegen die umgewidmeten Mittel physisch?
    /// Die Summe der Zeilenbeträge muss exakt <see cref="Amount"/> betragen.
    /// Null = implizite Verteilung (Auflösung im Anwendungsfall aus dem
    /// Journal: der Quellpool wird eindeutig von einem Konto getragen).
    /// </summary>
    public IReadOnlyList<AccountAllocation>? Accounts { get; init; }

    /// <summary>Freitext, optional.</summary>
    public string? Note { get; init; }

    /// <summary>Erwartete Basisrevision (Rechenvertrag §5.5; wird mitgeführt).</summary>
    public int? ExpectedBaseRevision { get; init; }

    /// <summary>
    /// Prüfungen der Vorgabe gegen den Stammdaten-Katalog: positiver
    /// Betrag; Quell- und Zielpool existierend, aktiv und verschieden;
    /// explizite Kontoverteilung mit eindeutigen aktiven Konten und
    /// exakter Summe (Splitregel). Journalbezogene Prüfungen (Poolbestand
    /// trägt den Betrag, implizite Verteilung eindeutig) liegen im
    /// Anwendungsfall.
    /// </summary>
    public void Validate(MasterDataCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new LarioDomainException("Umwidmung ohne Id.");
        }

        if (Amount.Cents <= 0)
        {
            throw new LarioDomainException(
                $"Umwidmung '{Id}': Betrag muss positiv sein (ist {Amount.ToEuro():0.00} €).");
        }

        var from = catalog.FindPool(FromPoolId)
            ?? throw new InvalidReferenceException(
                $"Umwidmung '{Id}' referenziert unbekannten Quellpool '{FromPoolId}'.");

        var to = catalog.FindPool(ToPoolId)
            ?? throw new InvalidReferenceException(
                $"Umwidmung '{Id}' referenziert unbekanntes Zielpool '{ToPoolId}'.");

        if (from.Status != EntityStatus.Active)
        {
            throw new InvalidReferenceException(
                $"Umwidmung '{Id}' referenziert den archivierten Quellpool '{FromPoolId}': " +
                "archivierte Entitäten liefern keine neuen Referenzen.");
        }

        if (to.Status != EntityStatus.Active)
        {
            throw new InvalidReferenceException(
                $"Umwidmung '{Id}' referenziert den archivierten Zielpool '{ToPoolId}': " +
                "archivierte Entitäten liefern keine neuen Referenzen.");
        }

        if (FromPoolId == ToPoolId)
        {
            throw new LarioDomainException(
                $"Umwidmung '{Id}': Quell- und Zielpool müssen verschieden sein.");
        }

        if (Accounts is not null)
        {
            if (Accounts.Count == 0)
            {
                throw new LarioDomainException(
                    $"Umwidmung '{Id}' mit leerer Kontoverteilung: entweder explizite " +
                    "Kontozeilen angeben oder null (implizite Verteilung).");
            }

            var seenAccounts = new HashSet<string>(StringComparer.Ordinal);
            var accountSum = Money.Zero;
            foreach (var account in Accounts)
            {
                if (string.IsNullOrWhiteSpace(account.AccountId))
                {
                    throw new LarioDomainException($"Umwidmung '{Id}' mit Kontozeile ohne Konto-Id.");
                }

                if (!seenAccounts.Add(account.AccountId))
                {
                    throw new LarioDomainException(
                        $"Umwidmung '{Id}': Konto '{account.AccountId}' kommt mehrfach in der " +
                        "Kontoverteilung vor (eine Umwidmung fasst ein Konto in einer Zeile auf).");
                }

                if (account.Amount.Cents <= 0)
                {
                    throw new LarioDomainException(
                        $"Umwidmung '{Id}': Kontozeile '{account.AccountId}' muss positiv sein " +
                        $"(ist {account.Amount.ToEuro():0.00} €).");
                }

                var accountEntity = catalog.FindAccount(account.AccountId)
                    ?? throw new InvalidReferenceException(
                        $"Umwidmung '{Id}' referenziert unbekanntes Konto '{account.AccountId}'.");

                if (accountEntity.Status != EntityStatus.Active)
                {
                    throw new InvalidReferenceException(
                        $"Umwidmung '{Id}' referenziert das archivierte Konto '{account.AccountId}': " +
                        "archivierte Entitäten liefern keine neuen Referenzen.");
                }

                accountSum = accountSum.Add(account.Amount);
            }

            if (accountSum != Amount)
            {
                throw new LarioDomainException(
                    $"Umwidmung '{Id}': Kontoverteilung ({accountSum.ToEuro():0.00} €) addiert nicht " +
                    $"exakt zum Umwidmungsbetrag ({Amount.ToEuro():0.00} €) — es gibt keinen " +
                    "nicht zugeordneten Rest.");
            }
        }
    }

    /// <summary>
    /// Konvertiert die geprüfte Vorgabe in eine Buchung: zwei Pool-Postings
    /// (Quelle −Betrag, Ziel +Betrag), keine Konto-Postings (Konten bleiben
    /// unverändert) und die <b>wirksame</b> Kontoverteilung als
    /// Buchungsinhalt. <paramref name="effectiveAccounts"/> ist die
    /// aufgelöste Verteilung (explizit oder vom Anwendungsfall aus dem
    /// Journal abgeleitet); sie muss die gleichen Prüfungen erfüllen wie
    /// <see cref="Accounts"/> (eindeutige Konten, positive Zeilen, exakte
    /// Summe) und wird hier erneut geprüft.
    /// </summary>
    public Booking ToBooking(IReadOnlyList<AccountAllocation> effectiveAccounts)
    {
        ArgumentNullException.ThrowIfNull(effectiveAccounts);
        ValidateEffectiveAccounts(effectiveAccounts, Accounts);

        return new Booking(
            Id,
            BookingType.Reassignment,
            Date,
            Note,
            [],
            [
                new PoolPosting { PoolId = FromPoolId, Amount = Amount.Negate() },
                new PoolPosting { PoolId = ToPoolId, Amount = Amount },
            ],
            reassignmentAccounts: effectiveAccounts.ToList());
    }

    /// <summary>
    /// Prüft die wirksame Kontoverteilung; bei explizit angegebenen
    /// Zeilen muss die wirksame Verteilung exakt die Angabe widerspiegeln
    /// (gleiche Zeilen, Reihenfolge egal) — eine Stille Ersetzung wäre
    /// ein Inhaltswechsel.
    /// </summary>
    private void ValidateEffectiveAccounts(
        IReadOnlyList<AccountAllocation> effective, IReadOnlyList<AccountAllocation>? explicitAccounts)
    {
        if (effective.Count == 0)
        {
            throw new LarioDomainException(
                $"Umwidmung '{Id}' ohne Kontoverteilung: die physischen Trägerkonten " +
                "sind Teil der Buchung (Rechenvertrag §3.4).");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sum = Money.Zero;
        foreach (var account in effective)
        {
            if (string.IsNullOrWhiteSpace(account.AccountId))
            {
                throw new LarioDomainException($"Umwidmung '{Id}' mit Kontozeile ohne Konto-Id.");
            }

            if (!seen.Add(account.AccountId))
            {
                throw new LarioDomainException(
                    $"Umwidmung '{Id}': Konto '{account.AccountId}' kommt mehrfach in der " +
                    "wirksamen Kontoverteilung vor.");
            }

            if (account.Amount.Cents <= 0)
            {
                throw new LarioDomainException(
                    $"Umwidmung '{Id}': wirksame Kontozeile '{account.AccountId}' muss positiv sein.");
            }

            sum = sum.Add(account.Amount);
        }

        if (sum != Amount)
        {
            throw new LarioDomainException(
                $"Umwidmung '{Id}': wirksame Kontoverteilung ({sum.ToEuro():0.00} €) addiert " +
                $"nicht exakt zum Umwidmungsbetrag ({Amount.ToEuro():0.00} €).");
        }

        if (explicitAccounts is not null)
        {
            var explicitLines = explicitAccounts
                .OrderBy(a => a.AccountId, StringComparer.Ordinal)
                .Select(a => (a.AccountId, a.Amount))
                .ToList();
            var effectiveLines = effective
                .OrderBy(a => a.AccountId, StringComparer.Ordinal)
                .Select(a => (a.AccountId, a.Amount))
                .ToList();
            if (!explicitLines.SequenceEqual(effectiveLines))
            {
                throw new LarioDomainException(
                    $"Umwidmung '{Id}': die wirksame Kontoverteilung weicht von der " +
                    "explizit angegebenen ab — implizite Auflösung ist nur ohne Angabe erlaubt.");
            }
        }
    }
}
