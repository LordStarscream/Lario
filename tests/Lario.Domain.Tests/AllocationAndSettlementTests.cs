using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;

namespace Lario.Domain.Tests;

/// <summary>
/// Schritt 5 (Rechenvertrag §3.4/§3.5): signierte Konto-Pool-Zuordnung,
/// abgeleitet aus den Postings (Zeilensummen = Kontostände, Spaltensummen
/// = Poolbestände) und offene interne Ausgleichspositionen (deterministisch
/// ableitbar: Teilbegleichung, Vertiefung, neue Episode nach Abschluss).
/// Referenzzahlen: Plan §5 Schritt 5 „Prüfen“ 1–5 sowie Referenzfälle
/// 04 (Ausgleich), 05 (Kontotransfer), 06 (Umwidmung).
/// </summary>
public class AllocationAndSettlementTests
{
    private static readonly DateOnly Stichtag = new(2026, 1, 1);

    private static MasterDataCatalog Catalog()
    {
        var catalog = new MasterDataCatalog([], [], [], []);
        catalog.CreateAccount("acc-haupt", "Hauptkonto", AccountType.Bank);
        catalog.CreateAccount("acc-freizeit", "Freizeitkonto", AccountType.Bank);
        catalog.CreateAccount("acc-wohnung", "Wohnungskonto", AccountType.Bank);
        catalog.CreatePool("pool-flex", "Flex");
        catalog.CreatePool("pool-freizeit", "Freizeit");
        catalog.CreatePool("pool-ruecklage", "Rücklage");
        catalog.CreateCategory("cat-lebensmittel", "Lebensmittel");
        catalog.CreateCategory("cat-freizeit", "Freizeit");
        return catalog;
    }

    private static Booking OpeningBalance(
        string id, string accountId, string poolId, Money amount) =>
        new(
            id,
            BookingType.OpeningBalance,
            Stichtag,
            null,
            [new AccountPosting { AccountId = accountId, Amount = amount }],
            [new PoolPosting { PoolId = poolId, Amount = amount }]);

    /// <summary>
    /// Gemischter Einkauf 85,00 € vom Hauptkonto (Referenzfall 3,
    /// bk-2026-1202): 65 € Flex + 20 € Freizeit — der Freizeit-Anteil
    /// wird über das Hauptkonto bezahlt → Vorfinanzierung.
    /// </summary>
    private static Booking MixedPurchase85Euro()
    {
        var catalog = Catalog();
        var spec = new ExpenseSpec
        {
            Id = "bk-2026-1202",
            Date = new DateOnly(2026, 11, 5),
            AccountId = "acc-haupt",
            TotalAmount = Money.FromCents(8_500),
            Note = "Gemischter Einkauf",
            Splits =
            [
                new ExpenseSplitSpec
                {
                    Id = "sp-1",
                    Amount = Money.FromCents(6_500),
                    PoolId = "pool-flex",
                    CategoryId = "cat-lebensmittel",
                },
                new ExpenseSplitSpec
                {
                    Id = "sp-2",
                    Amount = Money.FromCents(2_000),
                    PoolId = "pool-freizeit",
                    CategoryId = "cat-freizeit",
                    Tax = new TaxMarking { Relevant = false },
                },
            ],
        };

        return spec.ToBooking(catalog);
    }

    /// <summary>Startzustand der Referenzfälle 04/05/06 (Fall 3 als Basis).</summary>
    private static List<Booking> ScenarioBase() =>
    [
        OpeningBalance("init-2026-01-01", "acc-haupt", "pool-flex", Money.FromCents(100_000)),
        OpeningBalance("init-2026-01-01-freizeit", "acc-freizeit", "pool-freizeit", Money.FromCents(30_000)),
    ];

    private static TransferSpec TransferSpec(
        string id,
        string fromAccountId,
        string toAccountId,
        Money amount,
        IReadOnlyList<PoolAllocation>? purposes = null,
        string? relatedSettlementId = null,
        DateOnly? date = null) =>
        new()
        {
            Id = id,
            Date = date ?? new DateOnly(2026, 11, 6),
            FromAccountId = fromAccountId,
            ToAccountId = toAccountId,
            Amount = amount,
            Purposes = purposes,
            RelatedSettlementId = relatedSettlementId,
        };

    // ------------------------------------------------------------------
    // Signierte Konto-Pool-Zuordnung (Matrix, Zeilen-/Spaltensummen)
    // ------------------------------------------------------------------

    [Fact(DisplayName = "Referenzfall 5: Transfer 800 € zweckerhaltend — Zuordnung wandert mit")]
    public void Allocation_ReferenceCase5_TransferMovesAllocation()
    {
        var bookings = ScenarioBase();
        var transfer = TransferSpec(
            "bk-2026-1204", "acc-haupt", "acc-wohnung", Money.FromCents(80_000),
            purposes: [new PoolAllocation("pool-flex", Money.FromCents(80_000))]);
        bookings.Add(transfer.ToBooking(transfer.Purposes!));

        var journal = new BookingJournal(bookings);
        var cells = journal.Allocations();

        // 80.000 Cent wandern als pool-flex-Zuordnung von acc-haupt auf acc-wohnung.
        Assert.Equal(
            [
                new AccountPoolCell("acc-freizeit", "pool-freizeit", Money.FromCents(30_000)),
                new AccountPoolCell("acc-haupt", "pool-flex", Money.FromCents(20_000)),
                new AccountPoolCell("acc-wohnung", "pool-flex", Money.FromCents(80_000)),
            ],
            cells);
        // Zeilensummen = Kontostände, Spaltensummen = Poolbestände.
        Assert.Empty(journal.AllocationInvariantViolations());
        Assert.Equal(Money.FromCents(130_000), journal.TotalAccountBalance());
        Assert.Equal(Money.FromCents(130_000), journal.TotalPoolBalance());
        Assert.Equal(Money.Zero, journal.AccountPoolGap());
        // Keine Poolbewegung: Pools bleiben unverändert.
        Assert.Equal(Money.FromCents(100_000), journal.PoolBalance("pool-flex"));
        Assert.Equal(Money.FromCents(30_000), journal.PoolBalance("pool-freizeit"));
    }

    [Fact(DisplayName = "Referenzfall 3: gemischter Einkauf erzeugt negative Zelle (−20 €)")]
    public void Allocation_ReferenceCase3_MixedPurchaseCreatesNegativeCell()
    {
        var bookings = ScenarioBase();
        bookings.Add(MixedPurchase85Euro());

        var journal = new BookingJournal(bookings);

        Assert.Equal(
            [
                new AccountPoolCell("acc-freizeit", "pool-freizeit", Money.FromCents(30_000)),
                new AccountPoolCell("acc-haupt", "pool-flex", Money.FromCents(93_500)),
                new AccountPoolCell("acc-haupt", "pool-freizeit", Money.FromCents(-2_000)),
            ],
            journal.Allocations());
        // Die negative Zelle bricht keine Invariante: Zeilensumme acc-haupt
        // = 93.500 − 2.000 = 91.500 Cent = Kontostand.
        Assert.Empty(journal.AllocationInvariantViolations());
        Assert.Equal(Money.FromCents(91_500), journal.AccountBalance("acc-haupt"));
        Assert.Equal(Money.FromCents(93_500), journal.PoolBalance("pool-flex"));
        Assert.Equal(Money.FromCents(28_000), journal.PoolBalance("pool-freizeit"));
    }

    [Fact(DisplayName = "Referenzfall 6: Umwidmung — Konten unverändert, Zuordnung wechselt Pool")]
    public void Allocation_ReferenceCase6_ReassignmentKeepsAccounts()
    {
        var bookings = ScenarioBase();
        var spec = new ReassignmentSpec
        {
            Id = "bk-2026-1205",
            Date = new DateOnly(2026, 11, 8),
            FromPoolId = "pool-flex",
            ToPoolId = "pool-ruecklage",
            Amount = Money.FromCents(50_000),
            Accounts = [new AccountAllocation("acc-haupt", Money.FromCents(50_000))],
        };
        spec.Validate(Catalog());
        bookings.Add(spec.ToBooking(spec.Accounts!));

        var journal = new BookingJournal(bookings);

        Assert.Equal(
            [
                new AccountPoolCell("acc-freizeit", "pool-freizeit", Money.FromCents(30_000)),
                new AccountPoolCell("acc-haupt", "pool-flex", Money.FromCents(50_000)),
                new AccountPoolCell("acc-haupt", "pool-ruecklage", Money.FromCents(50_000)),
            ],
            journal.Allocations());
        // Umwidmung ist keine Kontobewegung.
        Assert.Equal(Money.FromCents(100_000), journal.AccountBalance("acc-haupt"));
        Assert.Equal(Money.FromCents(30_000), journal.AccountBalance("acc-freizeit"));
        Assert.Empty(journal.AllocationInvariantViolations());
        // Pools: 100.000 → 50.000/50.000; Summe unverändert.
        Assert.Equal(Money.FromCents(50_000), journal.PoolBalance("pool-flex"));
        Assert.Equal(Money.FromCents(50_000), journal.PoolBalance("pool-ruecklage"));
        Assert.Equal(Money.Zero, journal.AccountPoolGap());
    }

    [Fact(DisplayName = "Zuordnung: Null-Zellen werden weggelassen; Allocation() liefert 0")]
    public void Allocation_ZeroCellsAreImplicit()
    {
        var journal = new BookingJournal(ScenarioBase());
        Assert.Equal(Money.Zero, journal.Allocation("acc-wohnung", "pool-flex"));
        Assert.DoesNotContain(journal.Allocations(), c => c.Amount.Cents == 0);
    }

    [Fact(DisplayName = "Zuordnung: nicht zugeordneter Anfangsbestands-Rest erklärt die Zeilensumme")]
    public void Allocation_UnallocatedOpeningRemainderKeepsInvariants()
    {
        // 100 € Anfangsbestand, nur 80 € verteilt — 20 € bleiben als
        // Systemposition „noch nicht zugeordnet“ sichtbar.
        var booking = new Booking(
            "init-partial",
            BookingType.OpeningBalance,
            Stichtag,
            null,
            [new AccountPosting { AccountId = "acc-haupt", Amount = Money.FromCents(100_000) }],
            [new PoolPosting { PoolId = "pool-flex", Amount = Money.FromCents(80_000) }]);
        var journal = new BookingJournal([booking]);

        Assert.Equal(Money.FromCents(20_000), journal.OpeningBalanceUnallocated("init-partial"));
        Assert.Equal(
            [("acc-haupt", Money.FromCents(20_000))],
            journal.UnallocatedOpeningRemainders());
        // Zeilensumme (80.000) + Rest (20.000) = Kontostand (100.000).
        Assert.Empty(journal.AllocationInvariantViolations());
        // Die Differenz bleibt sichtbar und fällt nicht in den Poolbestand.
        Assert.Equal(Money.FromCents(20_000), journal.AccountPoolGap());
    }

    // ------------------------------------------------------------------
    // Ausgleichspositionen: Ableitung, Teilbegleichung, Vertiefung
    // ------------------------------------------------------------------

    [Fact(DisplayName = "Ausgleich: Vorfinanzierung erzeugt genau eine offene Position (20 €)")]
    public void Settlement_MixedPurchase_CreatesSingleOpenPosition()
    {
        var bookings = ScenarioBase();
        bookings.Add(MixedPurchase85Euro());

        var episode = Assert.Single(new BookingJournal(bookings).DeriveSettlementPositions());
        Assert.Equal("acc-haupt", episode.SourceAccountId);
        Assert.Equal("pool-freizeit", episode.TargetPoolId);
        Assert.Equal(Money.FromCents(2_000), episode.Amount);
        Assert.Equal(Money.FromCents(2_000), episode.RestAmount);
        Assert.Equal(SettlementStatus.Offen, episode.Status);
        Assert.Equal("bk-2026-1202", episode.CreatedByBookingId);
        Assert.Equal(new DateOnly(2026, 11, 5), episode.CreatedDate);
        Assert.Null(episode.SettledByBookingId);
    }

    [Fact(DisplayName = "Referenzfall 4: Rückfluss 20 € begleicht die Position vollständig")]
    public void Settlement_ReferenceCase4_FullSettlementClosesPosition()
    {
        var bookings = ScenarioBase();
        bookings.Add(MixedPurchase85Euro());
        var transfer = TransferSpec(
            "bk-2026-1203", "acc-freizeit", "acc-haupt", Money.FromCents(2_000),
            purposes: [new PoolAllocation("pool-freizeit", Money.FromCents(2_000))],
            relatedSettlementId: "set-0010");
        bookings.Add(transfer.ToBooking(transfer.Purposes!));

        var journal = new BookingJournal(bookings);
        var episode = Assert.Single(journal.DeriveSettlementPositions());

        Assert.Equal(SettlementStatus.Beglichen, episode.Status);
        Assert.Equal(Money.Zero, episode.RestAmount);
        Assert.Equal(Money.FromCents(2_000), episode.Amount);
        Assert.Equal("bk-2026-1202", episode.CreatedByBookingId);
        Assert.Equal("bk-2026-1203", episode.SettledByBookingId);
        Assert.Equal(new DateOnly(2026, 11, 6), episode.SettledDate);
        Assert.DoesNotContain(journal.DeriveSettlementPositions(),
            e => e.Status == SettlementStatus.Offen);

        // Negative Zuordnung aufgelöst; Konten und Pools decken sich wieder.
        Assert.Equal(Money.Zero, journal.Allocation("acc-haupt", "pool-freizeit"));
        Assert.Equal(Money.FromCents(93_500), journal.AccountBalance("acc-haupt"));
        Assert.Equal(Money.FromCents(28_000), journal.AccountBalance("acc-freizeit"));
        Assert.Empty(journal.AllocationInvariantViolations());
    }

    [Fact(DisplayName = "Plan-Schritt 5 Prüfen 4: Teilbegleichung 5 € lässt 15 € offen")]
    public void Settlement_PartialSettlement_LeavesRestOpen()
    {
        var bookings = ScenarioBase();
        bookings.Add(MixedPurchase85Euro());
        var transfer = TransferSpec(
            "bk-partial", "acc-freizeit", "acc-haupt", Money.FromCents(500),
            purposes: [new PoolAllocation("pool-freizeit", Money.FromCents(500))]);
        bookings.Add(transfer.ToBooking(transfer.Purposes!));

        var episode = Assert.Single(new BookingJournal(bookings).DeriveSettlementPositions());
        Assert.Equal(SettlementStatus.Offen, episode.Status);
        // Gesamtbetrag der Episode bleibt 20 €, Rest sinkt auf 15 €.
        Assert.Equal(Money.FromCents(2_000), episode.Amount);
        Assert.Equal(Money.FromCents(1_500), episode.RestAmount);
        Assert.Equal("bk-2026-1202", episode.CreatedByBookingId);
        Assert.Null(episode.SettledByBookingId);
    }

    [Fact(DisplayName = "Ausgleich: erneute Vorfinanzierung vertieft dieselbe Episode")]
    public void Settlement_Deepening_ExtendsSameEpisode()
    {
        var bookings = ScenarioBase();
        bookings.Add(MixedPurchase85Euro());

        // Zweiter Einkauf: 30 €, davon 20 € Freizeit über das Hauptkonto.
        var catalog = Catalog();
        var spec = new ExpenseSpec
        {
            Id = "bk-2026-1203",
            Date = new DateOnly(2026, 11, 10),
            AccountId = "acc-haupt",
            TotalAmount = Money.FromCents(3_000),
            Splits =
            [
                new ExpenseSplitSpec
                {
                    Id = "sp-1",
                    Amount = Money.FromCents(1_000),
                    PoolId = "pool-flex",
                    CategoryId = "cat-lebensmittel",
                },
                new ExpenseSplitSpec
                {
                    Id = "sp-2",
                    Amount = Money.FromCents(2_000),
                    PoolId = "pool-freizeit",
                    CategoryId = "cat-freizeit",
                },
            ],
        };
        bookings.Add(spec.ToBooking(catalog));

        var episode = Assert.Single(new BookingJournal(bookings).DeriveSettlementPositions());
        // Dieselbe Episode (erstellt durch den ersten Einkauf); Rest und
        // Gesamtbetrag wachsen um die Zunahme der Schuld.
        Assert.Equal(SettlementStatus.Offen, episode.Status);
        Assert.Equal(Money.FromCents(4_000), episode.Amount);
        Assert.Equal(Money.FromCents(4_000), episode.RestAmount);
        Assert.Equal("bk-2026-1202", episode.CreatedByBookingId);
    }

    [Fact(DisplayName = "Ausgleich: nach vollständiger Begleichung beginnt eine neue Episode")]
    public void Settlement_AfterSettlement_NewEpisodeStarts()
    {
        var bookings = ScenarioBase();
        bookings.Add(MixedPurchase85Euro());

        var settle = TransferSpec(
            "bk-settle", "acc-freizeit", "acc-haupt", Money.FromCents(2_000),
            purposes: [new PoolAllocation("pool-freizeit", Money.FromCents(2_000))]);
        bookings.Add(settle.ToBooking(settle.Purposes!));

        // Neue Vorfinanzierung: Einkauf 10 €, davon 5 € Freizeit über Haupt.
        var catalog = Catalog();
        var spec = new ExpenseSpec
        {
            Id = "bk-new-debt",
            Date = new DateOnly(2026, 11, 12),
            AccountId = "acc-haupt",
            TotalAmount = Money.FromCents(1_000),
            Splits =
            [
                new ExpenseSplitSpec
                {
                    Id = "sp-1",
                    Amount = Money.FromCents(500),
                    PoolId = "pool-flex",
                    CategoryId = "cat-lebensmittel",
                },
                new ExpenseSplitSpec
                {
                    Id = "sp-2",
                    Amount = Money.FromCents(500),
                    PoolId = "pool-freizeit",
                    CategoryId = "cat-freizeit",
                },
            ],
        };
        bookings.Add(spec.ToBooking(catalog));

        var episodes = new BookingJournal(bookings).DeriveSettlementPositions();
        Assert.Equal(2, episodes.Count);

        var closed = episodes.Single(e => e.CreatedByBookingId == "bk-2026-1202");
        Assert.Equal(SettlementStatus.Beglichen, closed.Status);
        Assert.Equal("bk-settle", closed.SettledByBookingId);

        var open = episodes.Single(e => e.CreatedByBookingId == "bk-new-debt");
        Assert.Equal(SettlementStatus.Offen, open.Status);
        Assert.Equal(Money.FromCents(500), open.RestAmount);
    }

    [Fact(DisplayName = "Ausgleich: Rückfluss größer als die Schuld schließt die Position und die Zelle wird positiv")]
    public void Settlement_Oversettlement_ClosesPositionAndCellTurnsPositive()
    {
        var bookings = ScenarioBase();
        bookings.Add(MixedPurchase85Euro());

        // 25 € Rückfluss bei 20 € Schuld: Position geschlossen, Zelle +5 €.
        var transfer = TransferSpec(
            "bk-oversettle", "acc-freizeit", "acc-haupt", Money.FromCents(2_500),
            purposes: [new PoolAllocation("pool-freizeit", Money.FromCents(2_500))]);
        bookings.Add(transfer.ToBooking(transfer.Purposes!));

        var journal = new BookingJournal(bookings);
        var episode = Assert.Single(journal.DeriveSettlementPositions());
        Assert.Equal(SettlementStatus.Beglichen, episode.Status);
        Assert.Equal(Money.Zero, episode.RestAmount);
        // Die darüber hinaus positive Zuordnung ist normale Zuordnung,
        // keine neue Position.
        Assert.Equal(Money.FromCents(500), journal.Allocation("acc-haupt", "pool-freizeit"));
        Assert.DoesNotContain(journal.DeriveSettlementPositions(),
            e => e.Status == SettlementStatus.Offen);
    }

    // ------------------------------------------------------------------
    // Referenzfälle 04/05/06 als komplette Journal-Fälle
    // ------------------------------------------------------------------

    [Fact(DisplayName = "Referenzfall 4 komplett: Bankstand, Zweckverteilung und offene Ausgleiche stimmen gleichzeitig")]
    public void ReferenceCase4_FullScenario()
    {
        // Startzustand = Endzustand von Fall 3: 1.000 €/300 € + 85 €
        // gemischter Einkauf vom Hauptkonto (65 € Flex + 20 € Freizeit).
        var bookings = ScenarioBase();

        // Prüfen 3: Gesamtbestand vor dem Einkauf 1.300 €.
        var start = new BookingJournal(ScenarioBase());
        Assert.Equal(Money.FromCents(130_000), start.TotalAccountBalance());

        bookings.Add(MixedPurchase85Euro());

        var before = new BookingJournal(bookings);
        // Prüfen 1: Konten 915/300 €, Pools 935/280 €, offener Ausgleich 20 €.
        Assert.Equal(Money.FromCents(91_500), before.AccountBalance("acc-haupt"));
        Assert.Equal(Money.FromCents(30_000), before.AccountBalance("acc-freizeit"));
        Assert.Equal(Money.FromCents(93_500), before.PoolBalance("pool-flex"));
        Assert.Equal(Money.FromCents(28_000), before.PoolBalance("pool-freizeit"));
        // Prüfen 3: nach dem Einkauf 1.215 € (85 € Einkauf).
        Assert.Equal(Money.FromCents(121_500), before.TotalAccountBalance());
        var openBefore = before.DeriveSettlementPositions().Single();
        Assert.Equal(Money.FromCents(2_000), openBefore.RestAmount);

        // Rückfluss 20 € (Freizeit → Haupt), Begleichung der Position.
        var transfer = TransferSpec(
            "bk-2026-1203", "acc-freizeit", "acc-haupt", Money.FromCents(2_000),
            purposes: [new PoolAllocation("pool-freizeit", Money.FromCents(2_000))],
            relatedSettlementId: "set-0010");
        bookings.Add(transfer.ToBooking(transfer.Purposes!));

        var after = new BookingJournal(bookings);
        // Prüfen 2: Konten 935/280 €, dieselben Pools, kein offener Ausgleich.
        Assert.Equal(Money.FromCents(93_500), after.AccountBalance("acc-haupt"));
        Assert.Equal(Money.FromCents(28_000), after.AccountBalance("acc-freizeit"));
        Assert.Equal(Money.FromCents(93_500), after.PoolBalance("pool-flex"));
        Assert.Equal(Money.FromCents(28_000), after.PoolBalance("pool-freizeit"));
        // Prüfen 3: Gesamtbestand nach dem Transfer identisch
        // (reine Umverteilung, 1.215 €).
        Assert.Equal(Money.FromCents(121_500), after.TotalAccountBalance());
        Assert.Equal(before.TotalAccountBalance(), after.TotalAccountBalance());
        // Prüfen 5: keine offene Position; die Episode ist beglichen.
        Assert.DoesNotContain(after.DeriveSettlementPositions(),
            e => e.Status == SettlementStatus.Offen);
        var episode = after.DeriveSettlementPositions().Single();
        Assert.Equal(SettlementStatus.Beglichen, episode.Status);
        Assert.Equal("bk-2026-1203", episode.SettledByBookingId);
        // Bankstand, Zweckverteilung und offene Ausgleiche stimmen
        // gleichzeitig: Matrix konsistent.
        Assert.Empty(after.AllocationInvariantViolations());
        Assert.Equal(
            [
                new AccountPoolCell("acc-freizeit", "pool-freizeit", Money.FromCents(28_000)),
                new AccountPoolCell("acc-haupt", "pool-flex", Money.FromCents(93_500)),
            ],
            after.Allocations());
    }

    [Fact(DisplayName = "Referenzfall 5 komplett: 800 € Transfer, Pools unverändert, Zuordnung wandert")]
    public void ReferenceCase5_FullScenario()
    {
        var bookings = ScenarioBase();
        var transfer = TransferSpec(
            "bk-2026-1204", "acc-haupt", "acc-wohnung", Money.FromCents(80_000),
            purposes: [new PoolAllocation("pool-flex", Money.FromCents(80_000))]);
        bookings.Add(transfer.ToBooking(transfer.Purposes!));

        var journal = new BookingJournal(bookings);

        Assert.Equal(Money.FromCents(20_000), journal.AccountBalance("acc-haupt"));
        Assert.Equal(Money.FromCents(30_000), journal.AccountBalance("acc-freizeit"));
        Assert.Equal(Money.FromCents(80_000), journal.AccountBalance("acc-wohnung"));
        Assert.Equal(Money.FromCents(100_000), journal.PoolBalance("pool-flex"));
        Assert.Equal(Money.FromCents(30_000), journal.PoolBalance("pool-freizeit"));
        Assert.Empty(journal.AllocationInvariantViolations());
        Assert.Empty(journal.DeriveSettlementPositions());
    }

    [Fact(DisplayName = "Referenzfall 6 komplett: Umwidmung 500 €, sofort wirksam ohne Kontotransfer")]
    public void ReferenceCase6_FullScenario()
    {
        var bookings = ScenarioBase();
        var spec = new ReassignmentSpec
        {
            Id = "bk-2026-1205",
            Date = new DateOnly(2026, 11, 8),
            FromPoolId = "pool-flex",
            ToPoolId = "pool-ruecklage",
            Amount = Money.FromCents(50_000),
            Note = "Rücklage aufbauen",
            Accounts = [new AccountAllocation("acc-haupt", Money.FromCents(50_000))],
        };
        spec.Validate(Catalog());
        bookings.Add(spec.ToBooking(spec.Accounts!));

        var journal = new BookingJournal(bookings);

        // Konten unverändert; Pools 100.000 → 50.000/50.000.
        Assert.Equal(Money.FromCents(100_000), journal.AccountBalance("acc-haupt"));
        Assert.Equal(Money.FromCents(30_000), journal.AccountBalance("acc-freizeit"));
        Assert.Equal(Money.FromCents(50_000), journal.PoolBalance("pool-flex"));
        Assert.Equal(Money.FromCents(50_000), journal.PoolBalance("pool-ruecklage"));
        Assert.Equal(Money.FromCents(130_000), journal.TotalAccountBalance());
        Assert.Equal(Money.FromCents(130_000), journal.TotalPoolBalance());
        Assert.Empty(journal.AllocationInvariantViolations());
        Assert.Empty(journal.DeriveSettlementPositions());
    }
}
