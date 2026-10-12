using Lario.Domain.MasterData;

namespace Lario.Domain.Journal;

/// <summary>
/// Eine Zeile der Einnahmeverteilung: ein Pool und der darin verteilte
/// positive Betrag.
/// </summary>
public sealed record IncomeAllocationSpec(string PoolId, Money Amount);

/// <summary>
/// Einnahmeverteilung (Rechenvertrag §3.2, Plan Schritt 4): ein
/// positiver Betrag auf einem Konto (Account +) und eine vollständige
/// Verteilung auf Pools (Pool +). Die Summe der Verteilung muss exakt
/// dem Gesamtbetrag entsprechen — es gibt keinen „nicht zugeordneten“
/// Rest einer Einnahme (die einzige Systemposition für nicht zugeordnet
/// ist der Anfangsbestand, Rechenvertrag §3.7).
///
/// Finanzierungsmonat (Rechenvertrag §2.2): Tatsächlicher Eingang
/// (Fachdatum) und Finanzierungsmonat sind unabhängige Angaben. Ohne
/// Angabe gilt die Standardregel „Gehalt am Monatsende finanziert
/// standardmäßig den Folgemonat“: Default = Folgemonat des Buchungsdats.
/// Der Finanzierungsmonat wird bei Erstellung in der Buchung
/// materialisiert; die Bindung an Verpflichtungen und die Deckungs-
/// rechen folgen in Schritt 12.
///
/// Noch keine Transferannahme: Eine Einnahme ist in Schritt 4 immer
/// ein einzelner Kontozugang (Prüfteil 1); Übertragungen zwischen
/// Konten folgen in Schritt 5.
/// </summary>
public sealed class IncomeSpec
{
    /// <summary>Stabile Identität der Einnahme (Idempotenzschlüssel).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Fachdatum des tatsächlichen Eingangs.</summary>
    public DateOnly Date { get; init; }

    /// <summary>Gesamtbetrag in Cent, positiv.</summary>
    public Money Amount { get; init; }

    /// <summary>Konto des Eingangs (Account +).</summary>
    public string AccountId { get; init; } = string.Empty;

    /// <summary>Freitext, optional.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// Finanzierungsmonat; null = Default-Regel (Folgemonat des
    /// Buchungsdats, siehe <see cref="EffectiveFundingMonth"/>).
    /// </summary>
    public YearMonth? FundingMonth { get; init; }

    /// <summary>
    /// Verteilung auf Pools (Pool +); muss exakt den Gesamtbetrag
    /// abdecken.
    /// </summary>
    public IReadOnlyList<IncomeAllocationSpec> Allocations { get; init; } = [];

    /// <summary>Erwartete Basisrevision (Rechenvertrag §5.5; wird mitgeführt).</summary>
    public int? ExpectedBaseRevision { get; init; }

    /// <summary>
    /// Wirksamer Finanzierungsmonat: explizit angegeben oder Default
    /// (Folgemonat des Buchungsdats).
    /// </summary>
    public YearMonth EffectiveFundingMonth() =>
        FundingMonth ?? YearMonth.FromDate(Date).Next();

    /// <summary>
    /// Prüfungen der Einnahme gegen den Stammdaten-Katalog:
    /// positiver Betrag; Konto existierend und aktiv; Verteilung mit
    /// positiven Zeilen, aktiven und eindeutigen Pools; Summe der
    /// Verteilung exakt gleich dem Gesamtbetrag (keine lückenhafte
    /// Verteilung).
    /// </summary>
    public void Validate(MasterDataCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new LarioDomainException("Einnahme ohne Id.");
        }

        if (Amount.Cents <= 0)
        {
            throw new LarioDomainException(
                $"Einnahme '{Id}': Betrag muss positiv sein (ist {Amount.ToEuro():0.00} €).");
        }

        var account = catalog.FindAccount(AccountId)
            ?? throw new InvalidReferenceException(
                $"Einnahme '{Id}' referenziert unbekanntes Konto '{AccountId}'.");

        if (account.Status != EntityStatus.Active)
        {
            throw new InvalidReferenceException(
                $"Einnahme '{Id}' referenziert das archivierte Konto '{AccountId}': " +
                "archivierte Entitäten liefern keine neuen Referenzen.");
        }

        if (Allocations.Count == 0)
        {
            throw new LarioDomainException(
                $"Einnahme '{Id}' ohne Verteilung: Einnahmen werden vollständig auf Pools verteilt.");
        }

        var seenPools = new HashSet<string>(StringComparer.Ordinal);
        var distributed = Money.Zero;
        foreach (var allocation in Allocations)
        {
            if (string.IsNullOrWhiteSpace(allocation.PoolId))
            {
                throw new LarioDomainException($"Einnahme '{Id}' mit Verteilung ohne Pool-Id.");
            }

            if (allocation.Amount.Cents <= 0)
            {
                throw new LarioDomainException(
                    $"Einnahme '{Id}': Verteilung auf '{allocation.PoolId}' muss positiv sein " +
                    $"(ist {allocation.Amount.ToEuro():0.00} €).");
            }

            var pool = catalog.FindPool(allocation.PoolId)
                ?? throw new InvalidReferenceException(
                    $"Einnahme '{Id}' referenziert unbekannten Pool '{allocation.PoolId}'.");

            if (pool.Status != EntityStatus.Active)
            {
                throw new InvalidReferenceException(
                    $"Einnahme '{Id}' referenziert den archivierten Pool '{allocation.PoolId}': " +
                    "archivierte Entitäten liefern keine neuen Referenzen.");
            }

            if (!seenPools.Add(allocation.PoolId))
            {
                throw new LarioDomainException(
                    $"Einnahme '{Id}': Pool '{allocation.PoolId}' kommt mehrfach in der Verteilung vor " +
                    "(eine Einnahme fasst einen Pool in einer Zeile auf).");
            }

            distributed = distributed.Add(allocation.Amount);
        }

        if (distributed != Amount)
        {
            throw new LarioDomainException(
                $"Einnahme '{Id}': Verteilung ({distributed.ToEuro():0.00} €) deckt den Gesamtbetrag " +
                $"({Amount.ToEuro():0.00} €) nicht exakt ab — es gibt keinen nicht zugeordneten Rest.");
        }
    }

    /// <summary>
    /// Konvertiert die geprüfte Einnahmeverteilung in eine Buchung
    /// (atomare Buchungseinheit): ein AccountPosting +Gesamtbetrag und
    /// je Verteilung ein PoolPosting +Zeilenbetrag; der wirksame
    /// Finanzierungsmonat wird materialisiert.
    /// </summary>
    public Booking ToBooking()
    {
        var accountPosting = new AccountPosting { AccountId = AccountId, Amount = Amount };
        var poolPostings = Allocations
            .Select(a => new PoolPosting { PoolId = a.PoolId, Amount = a.Amount })
            .ToList();

        return new Booking(
            Id,
            BookingType.Income,
            Date,
            Note,
            [accountPosting],
            poolPostings,
            EffectiveFundingMonth());
    }
}
