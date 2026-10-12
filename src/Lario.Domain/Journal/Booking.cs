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
    /// Finanzierungsmonat der Einnahme (nur <see cref="BookingType.Income"/>).
    /// Tatsächlicher Eingang (Fachdatum) und Finanzierungsmonat sind
    /// unabhängige Angaben (Rechenvertrag §2.2); die Standardregel
    /// „Gehalt am Monatsende finanziert standardmäßig den Folgemonat“
    /// wird bei Erstellung der Einnahme angewandt (Materialisierung,
    /// keine Ableitung). Bindung an Verpflichtungen/Deckung: ab Schritt 12.
    /// </summary>
    public YearMonth? FundingMonth { get; init; }

    /// <summary>
    /// Teilposten der Ausgabe (nur <see cref="BookingType.Expense"/>):
    /// Pool, Kategorie, Betrag und Steuerkennzeichnung je Teilposten.
    /// Die Summe der Teilposten muss exakt dem Gesamtbetrag der Buchung
    /// entsprechen (Rechenvertrag §3.5).
    /// </summary>
    public List<ExpenseSplit> Splits { get; init; } = [];

    /// <summary>
    /// Erzeugt eine Buchung ohne Postings. Dieser Konstrutor ist
    /// EF-Compliant: EF Core kann Sammlungs-Navigationsparameter
    /// (<c>AccountPostings</c>, <c>PoolPostings</c>, <c>Splits</c>) und
    /// <c>Finanzierungsmonat</c> erst nach der Konstruktion setzen und
    /// wählt daher diesen Konstrutor bei der Materialisierung. Deshalb
    /// werden hier keine typspezifischen Invarianten geprüft — die
    /// gespeicherten Buchungen wurden beim Speichern (voller Konstrutor)
    /// geprüft. Für Domänenpfade ist der volle Konstrutor zu verwenden.
    /// </summary>
    public Booking(string id, BookingType type, DateOnly date, string? note = null)
    {
        Id = id;
        Type = type;
        Date = date;
        Note = note;
    }

    /// <summary>
    /// Erzeugt eine Buchung mit ihren Postings und Teilposten
    /// (atomare Buchungseinheit).
    /// </summary>
    public Booking(
        string id,
        BookingType type,
        DateOnly date,
        string? note,
        IReadOnlyList<AccountPosting>? accountPostings,
        IReadOnlyList<PoolPosting>? poolPostings,
        YearMonth? fundingMonth = null,
        IReadOnlyList<ExpenseSplit>? splits = null)
    {
        Id = id;
        Type = type;
        Date = date;
        Note = note;
        AccountPostings = (accountPostings ?? []).ToList();
        PoolPostings = (poolPostings ?? []).ToList();
        FundingMonth = fundingMonth;
        Splits = (splits ?? []).ToList();
        ValidatePostings();
        ValidateTypeSpecific();
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

    /// <summary>
    /// Typspezifische Invarianten der Kernbuchungen (Schritt 4):
    /// <para>
    /// <b>Einnahme:</b> exakt ein positiver Kontozugang; Pool-Postings sind
    /// positiv und summieren sich exakt zum Kontozugang (vollständige
    /// Einnahmeverteilung, kein nicht verteilter Rest); der
    /// Finanzierungsmonat ist gesetzt (Default wird bei Erstellung
    /// materialisiert); keine Teilposten.
    /// </para>
    /// <para>
    /// <b>Ausgabe:</b> exakt ein negativer Kontoabgang; Pool-Postings sind
    /// negativ und summieren sich exakt zum Kontoabgang; Teilposten sind
    /// vorhanden, positiv, mit eindeutigen Ids, verweisen nur auf die
    /// Postings-Pools und summieren sich exakt zum Gesamtbetrag
    /// (Splitregel, Rechenvertrag §3.5); kein Finanzierungsmonat.
    /// </para>
    /// <para>
    /// <b>Anfangsbestand:</b> weder Finanzierungsmonat noch Teilposten
    /// (keine Einnahme, Rechenvertrag §3.7).
    /// </para>
    /// </summary>
    private void ValidateTypeSpecific()
    {
        switch (Type)
        {
            case BookingType.Income:
                if (FundingMonth is null)
                {
                    throw new LarioDomainException(
                        $"Einnahme '{Id}' ohne Finanzierungsmonat (Default wird bei Erstellung materialisiert).");
                }

                if (Splits.Count > 0)
                {
                    throw new LarioDomainException(
                        $"Einnahme '{Id}' trägt Teilposten — Teilposten gehören zu Ausgaben.");
                }

                RequireSingleAccountPosting(out var income);
                if (income.Cents <= 0)
                {
                    throw new LarioDomainException(
                        $"Einnahme '{Id}': der Kontozugang muss positiv sein (ist {income.ToEuro():0.00} €).");
                }

                RequirePoolPostingsSumTo(income, $"Einnahme '{Id}'");
                foreach (var poolPosting in PoolPostings)
                {
                    if (poolPosting.Amount.Cents <= 0)
                    {
                        throw new LarioDomainException(
                            $"Einnahme '{Id}': Pool-Posting '{poolPosting.PoolId}' muss positiv sein.");
                    }
                }
                break;

            case BookingType.Expense:
                if (FundingMonth is not null)
                {
                    throw new LarioDomainException(
                        $"Ausgabe '{Id}' trägt einen Finanzierungsmonat — der gehört zu Einnahmen.");
                }

                RequireSingleAccountPosting(out var expense);
                if (expense.Cents >= 0)
                {
                    throw new LarioDomainException(
                        $"Ausgabe '{Id}': der Kontoabgang muss negativ sein (ist {expense.ToEuro():0.00} €).");
                }

                var total = expense.Negate();
                RequirePoolPostingsSumTo(expense, $"Ausgabe '{Id}'");
                foreach (var poolPosting in PoolPostings)
                {
                    if (poolPosting.Amount.Cents >= 0)
                    {
                        throw new LarioDomainException(
                            $"Ausgabe '{Id}': Pool-Posting '{poolPosting.PoolId}' muss negativ sein (Verbrauch).");
                    }
                }

                ValidateSplits(total);
                break;

            case BookingType.OpeningBalance:
                if (FundingMonth is not null)
                {
                    throw new LarioDomainException(
                        $"Anfangsbestand '{Id}' trägt einen Finanzierungsmonat — Anfangsbestand ist keine Einnahme.");
                }

                if (Splits.Count > 0)
                {
                    throw new LarioDomainException(
                        $"Anfangsbestand '{Id}' trägt Teilposten — Teilposten gehören zu Ausgaben.");
                }
                break;

            default:
                // Die übrigen Buchungstypen folgen in den jeweiligen
                // Schritten (Transfers/Reservierungen/Korrekturen);
                // typspezifische Invarianten werden dort ergänzt.
                break;
        }
    }

    private void RequireSingleAccountPosting(out Money amount)
    {
        if (AccountPostings.Count != 1)
        {
            throw new LarioDomainException(
                $"Buchung '{Id}' ({Type}) muss exakt ein AccountPosting tragen, hat {AccountPostings.Count}.");
        }

        amount = AccountPostings[0].Amount;
    }

    private void RequirePoolPostingsSumTo(Money accountAmount, string label)
    {
        if (PoolPostings.Count == 0)
        {
            throw new LarioDomainException(
                $"{label} ohne Pool-Postings: Konto und Zweck müssen zusammengehören.");
        }

        var poolSum = PoolPostings
            .Aggregate(Money.Zero, (sum, p) => sum.Add(p.Amount));
        if (poolSum != accountAmount)
        {
            throw new LarioDomainException(
                $"{label}: Pool-Postings ({poolSum.ToEuro():0.00} €) addieren nicht exakt " +
                $"zum Konto-Posting ({accountAmount.ToEuro():0.00} €).");
        }
    }

    private void ValidateSplits(Money total)
    {
        if (Splits.Count == 0)
        {
            throw new LarioDomainException($"Ausgabe '{Id}' ohne Teilposten.");
        }

        var seenSplitIds = new HashSet<string>(StringComparer.Ordinal);
        var poolPostingAmounts = new Dictionary<string, Money>(StringComparer.Ordinal);
        foreach (var posting in PoolPostings)
        {
            poolPostingAmounts[posting.PoolId] = posting.Amount;
        }

        // Teilposten je Pool (Beträge positiv);
        // am Ende muss je Pool die Summe dem (negativen) Pool-Posting entsprechen.
        var splitAmountByPool = new Dictionary<string, Money>(StringComparer.Ordinal);

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
                    $"Ausgabe '{Id}': Teilposten '{split.Id}' muss positiv sein (ist {split.Amount.ToEuro():0.00} €).");
            }

            if (string.IsNullOrWhiteSpace(split.PoolId))
            {
                throw new LarioDomainException($"Ausgabe '{Id}': Teilposten '{split.Id}' ohne Pool-Id.");
            }

            if (!poolPostingAmounts.ContainsKey(split.PoolId))
            {
                throw new LarioDomainException(
                    $"Ausgabe '{Id}': Teilposten '{split.Id}' finanziert Pool '{split.PoolId}', " +
                    "der nicht in den Pool-Postings der Buchung vorkommt.");
            }

            if (string.IsNullOrWhiteSpace(split.CategoryId))
            {
                throw new LarioDomainException($"Ausgabe '{Id}': Teilposten '{split.Id}' ohne Kategorie-Id.");
            }

            splitSum = splitSum.Add(split.Amount);
            splitAmountByPool[split.PoolId] =
                splitAmountByPool.GetValueOrDefault(split.PoolId, Money.Zero).Add(split.Amount);
        }

        if (splitSum != total)
        {
            throw new LarioDomainException(
                $"Ausgabe '{Id}': Teilposten ({splitSum.ToEuro():0.00} €) addieren nicht exakt " +
                $"zum Gesamtbetrag ({total.ToEuro():0.00} €) — Splitregel verletzt.");
        }

        foreach (var (poolId, amount) in poolPostingAmounts)
        {
            var splitsForPool = splitAmountByPool.GetValueOrDefault(poolId, Money.Zero);
            if (splitsForPool != amount.Negate())
            {
                throw new LarioDomainException(
                    $"Ausgabe '{Id}': Teilposten für Pool '{poolId}' ({splitsForPool.ToEuro():0.00} €) " +
                    $"entsprechen nicht dem Pool-Posting ({amount.ToEuro():0.00} €).");
            }
        }
    }

    public override string ToString() => $"{Id} ({Type}, {Date:yyyy-MM-dd}, Rev {BaseRevision})";
}
