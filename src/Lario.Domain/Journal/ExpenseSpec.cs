using Lario.Domain.MasterData;

namespace Lario.Domain.Journal;

/// <summary>
/// Teilposten-Vorgabe einer Ausgabe: Betrag, Pool, Kategorie, optional
/// Notiz und optional eine <b>explizite</b> Steuerkennzeichnung.
///
/// <see cref="Tax"/> = null bedeutet: keine explizite Angabe — dann
/// übernimmt der Teilposten bei Erstellung die wirksame Steuer-Voreinstellung
/// seiner Kategorie (Plan §9: „Die Vorbelegung wird bei Erstellung in den
/// Posten übernommen“). Eine explizite Kennzeichnung — auch ein
/// explizites Nein — übersteuert die Voreinstellung.
/// </summary>
public sealed class ExpenseSplitSpec
{
    /// <summary>Stabile Identität des Teilpostens (z. B. „sp-1“).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Positiver Betrag in Cent.</summary>
    public Money Amount { get; init; }

    /// <summary>Zweck, den der Teilposten finanziert (Pool−).</summary>
    public string PoolId { get; init; } = string.Empty;

    /// <summary>Alltagskategorie des Teilpostens.</summary>
    public string CategoryId { get; init; } = string.Empty;

    /// <summary>Freitext, optional.</summary>
    public string? Note { get; init; }

    /// <summary>Explizite Steuerkennzeichnung; null = wirksame Kategorie-Voreinstellung.</summary>
    public TaxMarking? Tax { get; init; }
}

/// <summary>
/// Ausgabe mit mehreren Teilposten (Rechenvertrag §3.2/§3.5, Plan
/// Schritt 4): ein Konto zahlt (Account −Gesamtbetrag), die finanzierten
/// Pools werden verbraucht (Pool − je Teilposten, je Pool zusammengefasst).
/// Der Gesamtbetrag ist ein eigenes Feld der Vorgabe; die Summe der
/// Teilposten muss exakt dazu addieren (Splitregel) — eine Abweichung
/// (z. B. 84,99 € bei Gesamtbetrag 85 €) wird zurückgewiesen.
///
/// Für die erste Version zahlt das Konto, auf dem die finanzierten Pools
/// liegen (Plan: „für diesen ersten Test liegen beide Pools auf dem
/// Zahlungskonto“); kontenübergreifende Deckung und signierte
/// Konto-Pool-Zuordnung folgen in Schritt 5.
/// </summary>
public sealed class ExpenseSpec
{
    /// <summary>Stabile Identität der Ausgabe (Idempotenzschlüssel).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Fachdatum der Ausgabe (bestimmt die Zurechnung, Rechenvertrag §2.2).</summary>
    public DateOnly Date { get; init; }

    /// <summary>Gesamtbetrag der Ausgabe in Cent, positiv.</summary>
    public Money TotalAmount { get; init; }

    /// <summary>Zahlungskonto (Account −Gesamtbetrag).</summary>
    public string AccountId { get; init; } = string.Empty;

    /// <summary>Freitext der Ausgabe, optional.</summary>
    public string? Note { get; init; }

    /// <summary>Teilposten (mindestens einer).</summary>
    public IReadOnlyList<ExpenseSplitSpec> Splits { get; init; } = [];

    /// <summary>Erwartete Basisrevision (Rechenvertrag §5.5; wird mitgeführt).</summary>
    public int? ExpectedBaseRevision { get; init; }

    /// <summary>
    /// Prüfungen der Ausgabe gegen den Stammdaten-Katalog:
    /// positiver Gesamtbetrag; Konto existierend und aktiv; Teilposten
    /// mit eindeutigen Ids und positiven Beträgen auf aktiven Pools und
    /// Kategorien; Splitregel — die Teilposten summieren sich exakt zum
    /// Gesamtbetrag; Steuerkennzeichnung je Teilposten (Selbstprüfung +
    /// aktive Steuerkategorie + Teilbetrag ≤ Splitbetrag).
    /// </summary>
    public void Validate(MasterDataCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new LarioDomainException("Ausgabe ohne Id.");
        }

        if (TotalAmount.Cents <= 0)
        {
            throw new LarioDomainException(
                $"Ausgabe '{Id}': Gesamtbetrag muss positiv sein (ist {TotalAmount.ToEuro():0.00} €).");
        }

        var account = catalog.FindAccount(AccountId)
            ?? throw new InvalidReferenceException(
                $"Ausgabe '{Id}' referenziert unbekanntes Konto '{AccountId}'.");

        if (account.Status != EntityStatus.Active)
        {
            throw new InvalidReferenceException(
                $"Ausgabe '{Id}' referenziert das archivierte Konto '{AccountId}': " +
                "archivierte Entitäten liefern keine neuen Referenzen.");
        }

        if (Splits.Count == 0)
        {
            throw new LarioDomainException($"Ausgabe '{Id}' ohne Teilposten.");
        }

        var seenSplitIds = new HashSet<string>(StringComparer.Ordinal);
        var splitSum = Money.Zero;
        foreach (var split in Splits)
        {
            if (string.IsNullOrWhiteSpace(split.Id))
            {
                throw new LarioDomainException($"Ausgabe '{Id}' mit Teilposten ohne Id.");
            }

            if (!seenSplitIds.Add(split.Id))
            {
                throw new LarioDomainException(
                    $"Ausgabe '{Id}': Teilposten-Id '{split.Id}' kommt mehrfach vor.");
            }

            if (split.Amount.Cents <= 0)
            {
                throw new LarioDomainException(
                    $"Ausgabe '{Id}': Teilposten '{split.Id}' muss positiv sein " +
                    $"(ist {split.Amount.ToEuro():0.00} €).");
            }

            var pool = catalog.FindPool(split.PoolId)
                ?? throw new InvalidReferenceException(
                    $"Teilposten '{split.Id}' der Ausgabe '{Id}' referenziert unbekannten Pool '{split.PoolId}'.");

            if (pool.Status != EntityStatus.Active)
            {
                throw new InvalidReferenceException(
                    $"Teilposten '{split.Id}' der Ausgabe '{Id}' referenziert den archivierten Pool " +
                    $"'{split.PoolId}': archivierte Entitäten liefern keine neuen Referenzen.");
            }

            var category = catalog.FindCategory(split.CategoryId)
                ?? throw new InvalidReferenceException(
                    $"Teilposten '{split.Id}' der Ausgabe '{Id}' referenziert unbekannte Kategorie '{split.CategoryId}'.");

            if (category.Status != EntityStatus.Active)
            {
                throw new InvalidReferenceException(
                    $"Teilposten '{split.Id}' der Ausgabe '{Id}' referenziert die archivierte Kategorie " +
                    $"'{split.CategoryId}': archivierte Entitäten liefern keine neuen Referenzen.");
            }

            split.Tax?.Validate(catalog, split.Amount);

            splitSum = splitSum.Add(split.Amount);
        }

        if (splitSum != TotalAmount)
        {
            throw new LarioDomainException(
                $"Ausgabe '{Id}': Teilposten ({splitSum.ToEuro():0.00} €) addieren nicht exakt " +
                $"zum Gesamtbetrag ({TotalAmount.ToEuro():0.00} €) — Splitregel verletzt.");
        }
    }

    /// <summary>
    /// Konvertiert die geprüfte Ausgabe in eine Buchung: ein AccountPosting
    /// −Gesamtbetrag, je Pool ein negatives PoolPosting (Zusammenfassung
    /// aller Teilposten des Pools) und die Teilposten selbst. Die
    /// Steuerkennzeichnung wird je Teilposten materialisiert: explizite
    /// Angabe bleibt unverändert; andernfalls gilt die wirksame
    /// Steuer-Voreinstellung der Split-Kategorie — ohne wirksame
    /// Voreinstellung oder mit <c>taxRelevant = null</c> („nicht
    /// festgelegt“) bleibt der Teilposten unmarkiert.
    /// </summary>
    public Booking ToBooking(MasterDataCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var poolTotals = new Dictionary<string, Money>(StringComparer.Ordinal);
        foreach (var split in Splits)
        {
            poolTotals[split.PoolId] =
                poolTotals.GetValueOrDefault(split.PoolId, Money.Zero).Add(split.Amount);
        }

        var accountPosting = new AccountPosting { AccountId = AccountId, Amount = TotalAmount.Negate() };
        var poolPostings = poolTotals
            .Select(p => new PoolPosting { PoolId = p.Key, Amount = p.Value.Negate() })
            .ToList();

        var splits = Splits
            .Select(s => new ExpenseSplit
            {
                Id = s.Id,
                Amount = s.Amount,
                PoolId = s.PoolId,
                CategoryId = s.CategoryId,
                Note = s.Note,
                Tax = ResolveMarking(s, catalog),
            })
            .ToList();

        return new Booking(
            Id,
            BookingType.Expense,
            Date,
            Note,
            [accountPosting],
            poolPostings,
            null,
            splits);
    }

    /// <summary>
    /// Ermittelt die zu speichernde Kennzeichnung eines Teilpostens:
    /// eine explizite Angabe bleibt in ihren Werten unverändert — die
    /// Herkunft wird auf „explizit“ normalisiert (ein vom Aufrufer
    /// gesetztes Herkunft-Flag wird nicht übernommen); ohne explizite
    /// Angabe wird die wirksame Kategorie-Voreinstellung materialisiert
    /// (Herkunft „Voreinstellung“).
    /// </summary>
    private static TaxMarking? ResolveMarking(ExpenseSplitSpec split, MasterDataCatalog catalog)
    {
        if (split.Tax is { } tax)
        {
            return new TaxMarking
            {
                Relevant = tax.Relevant,
                TaxCategoryId = tax.TaxCategoryId,
                MarkedAmount = tax.MarkedAmount,
                Note = tax.Note,
                InheritedFromPreset = false,
            };
        }

        return MaterializeEffectivePreset(catalog, split.CategoryId);
    }

    /// <summary>
    /// Materialisiert die wirksame Steuer-Voreinstellung einer
    /// Split-Kategorie (öffentlich, weil die Idempotenzprüfung der
    /// Ausgabe dieselbe Materialisierung für Teilposten ohne explizite
    /// Kennzeichnung benötigt; die Invarianten der Kennzeichnung selbst
    /// werden bei der Erstellung geprüft, nicht hier):
    /// - keine wirksame Voreinstellung oder <c>taxRelevant = null</c>
    ///   („nicht festgelegt“) → null (keine Kennzeichnung);
    /// - sonst die Kennzeichnung mit der (optionalen) Steuerkategorie und
    ///   ohne Teilbetrag (gesamter Splitbetrag markiert), markiert mit
    ///   <c>InheritedFromPreset = true</c> (Herkunft für die Idempotenz).
    /// </summary>
    public static TaxMarking? MaterializeEffectivePreset(MasterDataCatalog catalog, string categoryId)
    {
        var preset = catalog.ResolveEffectivePreset(categoryId);
        if (preset is null || preset.TaxRelevant is null)
        {
            // Keine wirksame Voreinstellung oder „nicht festgelegt“:
            // der Teilposten bleibt steuerlich unmarkiert.
            return null;
        }

        return new TaxMarking
        {
            Relevant = preset.TaxRelevant.Value,
            // Bei irrelevantem Ergebnis trägt die Kennzeichnung keine
            // Steuerkategorie (Invariant der Kennzeichnung).
            TaxCategoryId = preset.TaxRelevant.Value ? preset.TaxCategoryId : null,
            MarkedAmount = null,
            Note = null,
            InheritedFromPreset = true,
        };
    }
}
