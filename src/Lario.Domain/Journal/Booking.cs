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
    /// Zweckverteilung der gewanderten Mittel (nur
    /// <see cref="BookingType.AccountTransfer"/>): je Zeile Pool +
    /// Zeilenbetrag, Summe exakt = Transferbetrag. Ein reiner Transfer
    /// hat keine Pool-Postings — die Zweckverteilung ist daher Teil der
    /// Buchung, damit die Konto-Pool-Zuordnung aus den Postings
    /// rekonstruierbar bleibt (Rechenvertrag §3.3/§3.4).
    /// </summary>
    public List<PoolAllocation> TransferPurposes { get; init; } = [];

    /// <summary>
    /// Kontoverteilung der umgewidmeten Mittel (nur
    /// <see cref="BookingType.Reassignment"/>): je Zeile Konto +
    /// Zeilenbetrag, Summe exakt = Umwidmungsbetrag. Eine Umwidmung hat
    /// keine Konto-Postings — die physisch tragenden Konten sind daher
    /// Teil der Buchung (Rechenvertrag §3.3/§3.4).
    /// </summary>
    public List<AccountAllocation> ReassignmentAccounts { get; init; } = [];

    /// <summary>
    /// Offene Ausgleichsposition, die diese Buchung (teilweise) begleicht
    /// (nur <see cref="BookingType.AccountTransfer"/>; Rechenvertrag §3.5).
    /// </summary>
    public string? RelatedSettlementId { get; init; }

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
        IReadOnlyList<ExpenseSplit>? splits = null,
        IReadOnlyList<PoolAllocation>? transferPurposes = null,
        IReadOnlyList<AccountAllocation>? reassignmentAccounts = null,
        string? relatedSettlementId = null)
    {
        Id = id;
        Type = type;
        Date = date;
        Note = note;
        AccountPostings = (accountPostings ?? []).ToList();
        PoolPostings = (poolPostings ?? []).ToList();
        FundingMonth = fundingMonth;
        Splits = (splits ?? []).ToList();
        TransferPurposes = (transferPurposes ?? []).ToList();
        ReassignmentAccounts = (reassignmentAccounts ?? []).ToList();
        RelatedSettlementId = relatedSettlementId;
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

                if (!FundingMonth.Value.IsValid)
                {
                    throw new LarioDomainException(
                        $"Einnahme '{Id}' mit ungültigem Finanzierungsmonat '{FundingMonth}' " +
                        "(Monat 1–12, Jahr 1–9999 erforderlich) — ein ungültiger Wert darf nicht gespeichert werden.");
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

            case BookingType.AccountTransfer:
                RequireNoFundingMonthOrSplits("Transfer");
                RequireBalancedAccountPostings("Transfer");
                if (PoolPostings.Count != 0)
                {
                    throw new LarioDomainException(
                        $"Transfer '{Id}' trägt Pool-Postings — der Gesamtpoolbestand bleibt unveränderbar (§3.3).");
                }

                if (ReassignmentAccounts.Count != 0)
                {
                    throw new LarioDomainException(
                        $"Transfer '{Id}' trägt eine Kontoverteilung — die gehört zu Umwidmungen.");
                }

                ValidatePoolAllocationLines(TransferPurposes, "Transfer");
                break;

            case BookingType.Reassignment:
                RequireNoFundingMonthOrSplits("Umwidmung");
                if (AccountPostings.Count != 0)
                {
                    throw new LarioDomainException(
                        $"Umwidmung '{Id}' trägt Konto-Postings — eine Umwidmung ist keine Kontobewegung (§3.3).");
                }

                RequireBalancedPoolPostings("Umwidmung");

                if (TransferPurposes.Count != 0)
                {
                    throw new LarioDomainException(
                        $"Umwidmung '{Id}' trägt eine Zweckverteilung — die gehört zu Transfers.");
                }

                ValidateAccountAllocationLines(ReassignmentAccounts, "Umwidmung");
                break;

            case BookingType.TransferReassignment:
                RequireNoFundingMonthOrSplits("Transfer/Umwidmung");
                RequireBalancedAccountPostings("Transfer/Umwidmung");
                RequireBalancedPoolPostings("Transfer/Umwidmung");

                // Beide Postingpaare addieren einzeln zu null; der getragene
                // Betrag ist die absolute Höhe des negativen Postings.
                var transferAmount = AccountPostings.Single(p => p.Amount.Cents > 0).Amount;
                var reassignmentAmount = PoolPostings.Single(p => p.Amount.Cents > 0).Amount;
                if (transferAmount != reassignmentAmount)
                {
                    throw new LarioDomainException(
                        $"Kombination '{Id}': Kontobewegung ({transferAmount.ToEuro():0.00} €) und Poolbewegung " +
                        $"({reassignmentAmount.ToEuro():0.00} €) müssen denselben Betrag tragen.");
                }

                if (TransferPurposes.Count != 0 || ReassignmentAccounts.Count != 0)
                {
                    throw new LarioDomainException(
                        $"Kombination '{Id}' trägt Zusatzverteilungen — die Kombination ist " +
                        "vollständig aus ihren Postings ableitbar.");
                }

                break;

            default:
                // Die übrigen Buchungstypen folgen in den jeweiligen
                // Schritten (Reservierungen/Korrekturen/Storno);
                // typspezifische Invarianten werden dort ergänzt.
                break;
        }
    }

    private void RequireNoFundingMonthOrSplits(string label)
    {
        if (FundingMonth is not null)
        {
            throw new LarioDomainException(
                $"{label} '{Id}' trägt einen Finanzierungsmonat — der gehört zu Einnahmen.");
        }

        if (Splits.Count > 0)
        {
            throw new LarioDomainException(
                $"{label} '{Id}' trägt Teilposten — Teilposten gehören zu Ausgaben.");
        }
    }

    private void RequireBalancedAccountPostings(string label)
    {
        var negative = AccountPostings.Where(p => p.Amount.Cents < 0).ToList();
        var positive = AccountPostings.Where(p => p.Amount.Cents > 0).ToList();
        if (AccountPostings.Count != 2 || negative.Count != 1 || positive.Count != 1)
        {
            throw new LarioDomainException(
                $"{label} '{Id}' muss exakt ein negatives und ein positives AccountPosting tragen " +
                $"(hat {AccountPostings.Count}).");
        }

        if (negative[0].Amount != positive[0].Amount.Negate())
        {
            throw new LarioDomainException(
                $"{label} '{Id}': Kontobewegungen addieren nicht zu null " +
                $"({negative[0].Amount.ToEuro():0.00} € / {positive[0].Amount.ToEuro():0.00} €) — " +
                "interne Transfers sind weder Einnahme noch Ausgabe.");
        }
    }

    private void RequireBalancedPoolPostings(string label)
    {
        var negative = PoolPostings.Where(p => p.Amount.Cents < 0).ToList();
        var positive = PoolPostings.Where(p => p.Amount.Cents > 0).ToList();
        if (PoolPostings.Count != 2 || negative.Count != 1 || positive.Count != 1)
        {
            throw new LarioDomainException(
                $"{label} '{Id}' muss exakt ein negatives und ein positives PoolPosting tragen " +
                $"(hat {PoolPostings.Count}).");
        }

        if (negative[0].Amount != positive[0].Amount.Negate())
        {
            throw new LarioDomainException(
                $"{label} '{Id}': Poolbewegungen addieren nicht zu null " +
                $"({negative[0].Amount.ToEuro():0.00} € / {positive[0].Amount.ToEuro():0.00} €) — " +
                "eine Umwidmung erzeugt weder Geld noch verbraucht sie Geld.");
        }
    }

    private void ValidatePoolAllocationLines(IReadOnlyList<PoolAllocation> lines, string label)
    {
        if (lines.Count == 0)
        {
            throw new LarioDomainException(
                $"{label} '{Id}' ohne Zweckverteilung: der Zweck der gewanderten Mittel ist Teil der Buchung (§3.4).");
        }

        ValidateAllocationLineIdsAndAmounts(lines.Select(l => (l.PoolId, l.Amount)), label);
    }

    private void ValidateAccountAllocationLines(IReadOnlyList<AccountAllocation> lines, string label)
    {
        if (lines.Count == 0)
        {
            throw new LarioDomainException(
                $"{label} '{Id}' ohne Kontoverteilung: die physischen Trägerkonten sind Teil der Buchung (§3.4).");
        }

        ValidateAllocationLineIdsAndAmounts(lines.Select(l => (l.AccountId, l.Amount)), label);
    }

    private static void ValidateAllocationLineIdsAndAmounts(
        IEnumerable<(string RefId, Money Amount)> lines, string label)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (refId, amount) in lines)
        {
            if (string.IsNullOrWhiteSpace(refId))
            {
                throw new LarioDomainException($"{label} mit Verteilungszeile ohne Id.");
            }

            if (!seen.Add(refId))
            {
                throw new LarioDomainException(
                    $"{label}: '{refId}' kommt mehrfach in der Verteilung vor.");
            }

            if (amount.Cents <= 0)
            {
                throw new LarioDomainException(
                    $"{label}: Verteilungszeile '{refId}' muss positiv sein (ist {amount.ToEuro():0.00} €).");
            }
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

    /// <summary>
    /// Delta der Buchung auf die signierte Konto-Pool-Zuordnung
    /// (Rechenvertrag §3.4: „Sie wird aus den Postings abgeleitet").
    /// Deterministisch; die Reihenfolge der Einträge ist stabil.
    /// <para>
    /// - <b>Anfangsbestand/Einnahme:</b> je Pool-Posting (Konto, Pool) +Betrag;
    ///   der nicht zugeordnete Rest eines Anfangsbestands hat keine Zelle.
    /// - <b>Ausgabe:</b> je Pool-Posting (Quellkonto, Pool) −Betrag — der
    ///   Verbrauch des Zwecks wird am bezahlenden Konto aufgelöst.
    /// - <b>Kontotransfer:</b> je Zweckzeile (Quelle, Pool) −Betrag und
    ///   (Ziel, Pool) +Betrag — der Zweck bleibt erhalten (§3.3).
    /// - <b>Umwidmung:</b> je Kontozeile (Konto, Quellpool) −Betrag und
    ///   (Konto, Zielpool) +Betrag — das physische Konto bleibt (§3.3).
    /// - <b>Kombination:</b> (Quellkonto, Quellpool) −Betrag und
    ///   (Zielkonto, Zielpool) +Betrag.
    /// </para>
    /// Noch nicht umgesetzte Buchungstypen (Reservierung, Storno, …) werfen:
    /// ihre Zuordnungswirkung wird im jeweiligen Schritt ergänzt.
    /// </summary>
    public IReadOnlyList<AllocationDeltaEntry> AllocationDelta()
    {
        switch (Type)
        {
            case BookingType.OpeningBalance:
            case BookingType.Income:
                var singleAccount = AccountPostings.Single().AccountId;
                return PoolPostings
                    .Select(p => new AllocationDeltaEntry(singleAccount, p.PoolId, p.Amount))
                    .ToList();

            case BookingType.Expense:
                var expenseAccount = AccountPostings.Single().AccountId;
                return PoolPostings
                    .Select(p => new AllocationDeltaEntry(expenseAccount, p.PoolId, p.Amount))
                    .ToList();

            case BookingType.AccountTransfer:
                var transfer = AccountPostings
                    .ToDictionary(p => p.Amount.Cents >= 0);
                var fromAccount = transfer[false].AccountId;
                var toAccount = transfer[true].AccountId;
                var transferDeltas = new List<AllocationDeltaEntry>(TransferPurposes.Count * 2);
                foreach (var purpose in TransferPurposes)
                {
                    transferDeltas.Add(new AllocationDeltaEntry(fromAccount, purpose.PoolId, purpose.Amount.Negate()));
                    transferDeltas.Add(new AllocationDeltaEntry(toAccount, purpose.PoolId, purpose.Amount));
                }

                return transferDeltas;

            case BookingType.Reassignment:
                var poolMove = PoolPostings
                    .ToDictionary(p => p.Amount.Cents >= 0);
                var fromPool = poolMove[false].PoolId;
                var toPool = poolMove[true].PoolId;
                var reassignmentDeltas = new List<AllocationDeltaEntry>(ReassignmentAccounts.Count * 2);
                foreach (var accountLine in ReassignmentAccounts)
                {
                    reassignmentDeltas.Add(new AllocationDeltaEntry(accountLine.AccountId, fromPool, accountLine.Amount.Negate()));
                    reassignmentDeltas.Add(new AllocationDeltaEntry(accountLine.AccountId, toPool, accountLine.Amount));
                }

                return reassignmentDeltas;

            case BookingType.TransferReassignment:
                var accountMove = AccountPostings
                    .ToDictionary(p => p.Amount.Cents >= 0);
                var poolMove2 = PoolPostings
                    .ToDictionary(p => p.Amount.Cents >= 0);
                var amount = accountMove[true].Amount;
                return new List<AllocationDeltaEntry>
                {
                    new(accountMove[false].AccountId, poolMove2[false].PoolId, amount.Negate()),
                    new(accountMove[true].AccountId, poolMove2[true].PoolId, amount),
                };

            default:
                throw new LarioDomainException(
                    $"Buchungstyp {Type} hat keine umgesetzte Zuordnungswirkung — " +
                    "die Konto-Pool-Zuordnung wird in Schritt 5 für die Kernbuchungen abgeleitet; " +
                    "weitere Typen folgen in ihren Schritten.");
        }
    }

    public override string ToString() => $"{Id} ({Type}, {Date:yyyy-MM-dd}, Rev {BaseRevision})";
}
