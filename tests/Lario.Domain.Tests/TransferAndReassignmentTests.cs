using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;

namespace Lario.Domain.Tests;

/// <summary>
/// Schritt 5 (Rechenvertrag §3.3): reiner Kontotransfer, Umwidmung und
/// Kombination als Buchungstypen — Spezifikations-Prüfungen und
/// typspezifische Buchungsinvarianten.
/// </summary>
public class TransferAndReassignmentTests
{
    private static MasterDataCatalog Catalog()
    {
        var catalog = new MasterDataCatalog([], [], [], []);
        catalog.CreateAccount("acc-haupt", "Hauptkonto", AccountType.Bank);
        catalog.CreateAccount("acc-freizeit", "Freizeitkonto", AccountType.Bank);
        catalog.CreateAccount("acc-wohnung", "Wohnungskonto", AccountType.Bank);
        catalog.CreateAccount("acc-altes", "Altkonto", AccountType.Bank);
        catalog.ArchiveAccount("acc-altes");
        catalog.CreatePool("pool-flex", "Flex");
        catalog.CreatePool("pool-freizeit", "Freizeit");
        catalog.CreatePool("pool-ruecklage", "Rücklage");
        catalog.CreatePool("pool-altes", "Alter Pool");
        catalog.ArchivePool("pool-altes");
        catalog.CreateCategory("cat-lebensmittel", "Lebensmittel");
        catalog.CreateCategory("cat-freizeit", "Freizeit");
        return catalog;
    }

    private static TransferSpec TransferSpec(
        string id,
        string fromAccountId,
        string toAccountId,
        Money amount,
        IReadOnlyList<PoolAllocation>? purposes = null,
        string? relatedSettlementId = null) =>
        new()
        {
            Id = id,
            Date = new DateOnly(2026, 11, 6),
            FromAccountId = fromAccountId,
            ToAccountId = toAccountId,
            Amount = amount,
            Purposes = purposes,
            RelatedSettlementId = relatedSettlementId,
        };

    // ------------------------------------------------------------------
    // TransferSpec: Validierung und Materialisierung
    // ------------------------------------------------------------------

    [Fact(DisplayName = "Transfer: gültige Vorgabe wird akzeptiert und materialisiert (Zweck als Buchungsinhalt)")]
    public void TransferSpec_Valid_ToBooking_MaterializesPurposes()
    {
        var catalog = Catalog();
        var spec = TransferSpec(
            "tr-1", "acc-haupt", "acc-freizeit", Money.FromCents(5_000),
            purposes:
            [
                new PoolAllocation("pool-flex", Money.FromCents(3_000)),
                new PoolAllocation("pool-freizeit", Money.FromCents(2_000)),
            ]);
        spec.Validate(catalog);

        var booking = spec.ToBooking(spec.Purposes!);

        Assert.Equal(BookingType.AccountTransfer, booking.Type);
        Assert.Equal(2, booking.AccountPostings.Count);
        Assert.Equal(Money.FromCents(-5_000),
            booking.AccountPostings.Single(p => p.AccountId == "acc-haupt").Amount);
        Assert.Equal(Money.FromCents(5_000),
            booking.AccountPostings.Single(p => p.AccountId == "acc-freizeit").Amount);
        // Reiner Transfer: keine Poolbewegung, keine Ausgabe, kein
        // Finanzierungsmonat (§3.3).
        Assert.Empty(booking.PoolPostings);
        Assert.Empty(booking.Splits);
        Assert.Null(booking.FundingMonth);
        // Die Zweckverteilung ist Teil der Buchung (§3.4).
        Assert.Equal(2, booking.TransferPurposes.Count);
        Assert.Equal("pool-flex", booking.TransferPurposes[0].PoolId);
        Assert.Equal(Money.FromCents(3_000), booking.TransferPurposes[0].Amount);
    }

    [Theory(DisplayName = "Transfer: nicht-positive Beträge werden abgelehnt")]
    [InlineData(0)]
    [InlineData(-100)]
    public void TransferSpec_NonPositiveAmount_Throws(int cents)
    {
        var catalog = Catalog();
        var spec = TransferSpec("tr-1", "acc-haupt", "acc-freizeit", Money.FromCents(cents));
        Assert.Throws<LarioDomainException>(() => spec.Validate(catalog));
    }

    [Theory(DisplayName = "Transfer: unbekanntes oder archiviertes Quellkonto wird abgelehnt")]
    [InlineData("acc-unknown")]
    [InlineData("acc-altes")]
    public void TransferSpec_UnknownOrArchivedSourceAccount_Throws(string accountId)
    {
        var catalog = Catalog();
        var spec = TransferSpec("tr-1", accountId, "acc-freizeit", Money.FromCents(1_000));
        Assert.Throws<InvalidReferenceException>(() => spec.Validate(catalog));
    }

    [Fact(DisplayName = "Transfer: Quell- und Zielkonto müssen verschieden sein")]
    public void TransferSpec_SameAccount_Throws()
    {
        var catalog = Catalog();
        var spec = TransferSpec("tr-1", "acc-haupt", "acc-haupt", Money.FromCents(1_000));
        Assert.Throws<LarioDomainException>(() => spec.Validate(catalog));
    }

    [Fact(DisplayName = "Transfer: Zweckverteilung addiert nicht exakt zum Betrag (Splitregel)")]
    public void TransferSpec_PurposesDoNotSumToAmount_Throws()
    {
        var catalog = Catalog();
        var spec = TransferSpec(
            "tr-1", "acc-haupt", "acc-freizeit", Money.FromCents(5_000),
            purposes: [new PoolAllocation("pool-flex", Money.FromCents(4_999))]);
        Assert.Throws<LarioDomainException>(() => spec.Validate(catalog));
    }

    [Fact(DisplayName = "Transfer: Pool kommt mehrfach in der Zweckverteilung vor")]
    public void TransferSpec_DuplicatePurposePool_Throws()
    {
        var catalog = Catalog();
        var spec = TransferSpec(
            "tr-1", "acc-haupt", "acc-freizeit", Money.FromCents(5_000),
            purposes:
            [
                new PoolAllocation("pool-flex", Money.FromCents(3_000)),
                new PoolAllocation("pool-flex", Money.FromCents(2_000)),
            ]);
        Assert.Throws<LarioDomainException>(() => spec.Validate(catalog));
    }

    [Fact(DisplayName = "Transfer: unbekannter bzw. archivierter Zweckpool wird abgelehnt")]
    public void TransferSpec_UnknownOrArchivedPurposePool_Throws()
    {
        var catalog = Catalog();

        var unknown = TransferSpec(
            "tr-1", "acc-haupt", "acc-freizeit", Money.FromCents(1_000),
            purposes: [new PoolAllocation("pool-unknown", Money.FromCents(1_000))]);
        Assert.Throws<InvalidReferenceException>(() => unknown.Validate(catalog));

        var archived = TransferSpec(
            "tr-2", "acc-haupt", "acc-freizeit", Money.FromCents(1_000),
            purposes: [new PoolAllocation("pool-altes", Money.FromCents(1_000))]);
        Assert.Throws<InvalidReferenceException>(() => archived.Validate(catalog));
    }

    [Fact(DisplayName = "Transfer: Begleichungsreferenz wird in die Buchung übernommen")]
    public void TransferSpec_RelatedSettlementId_IsCarriedOver()
    {
        var catalog = Catalog();
        var spec = TransferSpec(
            "tr-1", "acc-freizeit", "acc-haupt", Money.FromCents(2_000),
            purposes: [new PoolAllocation("pool-freizeit", Money.FromCents(2_000))],
            relatedSettlementId: "set-0010");
        spec.Validate(catalog);

        var booking = spec.ToBooking(spec.Purposes!);
        Assert.Equal("set-0010", booking.RelatedSettlementId);
    }

    // ------------------------------------------------------------------
    // ReassignmentSpec: Validierung und Materialisierung
    // ------------------------------------------------------------------

    [Fact(DisplayName = "Umwidmung: gültige Vorgabe — nur Pool−/Pool+, keine Kontobewegung (Referenzfall 6)")]
    public void ReassignmentSpec_Valid_ToBooking_HasNoAccountPostings()
    {
        var catalog = Catalog();
        var spec = new ReassignmentSpec
        {
            Id = "rw-1",
            Date = new DateOnly(2026, 11, 8),
            FromPoolId = "pool-flex",
            ToPoolId = "pool-ruecklage",
            Amount = Money.FromCents(50_000),
            Note = "Rücklage aufbauen",
            Accounts = [new AccountAllocation("acc-haupt", Money.FromCents(50_000))],
        };
        spec.Validate(catalog);

        var booking = spec.ToBooking(spec.Accounts!);

        Assert.Equal(BookingType.Reassignment, booking.Type);
        Assert.Empty(booking.AccountPostings);
        Assert.Equal(Money.FromCents(-50_000),
            booking.PoolPostings.Single(p => p.PoolId == "pool-flex").Amount);
        Assert.Equal(Money.FromCents(50_000),
            booking.PoolPostings.Single(p => p.PoolId == "pool-ruecklage").Amount);
        Assert.Single(booking.ReassignmentAccounts);
        Assert.Equal("acc-haupt", booking.ReassignmentAccounts[0].AccountId);
        Assert.Empty(booking.TransferPurposes);
        Assert.Empty(booking.Splits);
        Assert.Null(booking.FundingMonth);
    }

    [Fact(DisplayName = "Umwidmung: leere explizite Verteilung wird abgelehnt; null ist implizit und braucht wirksame Zeilen")]
    public void ReassignmentSpec_WithoutAccounts_Throws()
    {
        var catalog = Catalog();

        // Explizit leer ist ein Fehler: entweder Zeilen oder null.
        var empty = new ReassignmentSpec
        {
            Id = "rw-1",
            Date = new DateOnly(2026, 11, 8),
            FromPoolId = "pool-flex",
            ToPoolId = "pool-ruecklage",
            Amount = Money.FromCents(50_000),
            Accounts = [],
        };
        Assert.Throws<LarioDomainException>(() => empty.Validate(catalog));

        // null = implizite Verteilung: die Katalogprüfung besteht (die
        // Auflösung liegt im Anwendungsfall), aber die Materialisierung
        // verlangt eine wirksame Kontoverteilung — die Trägerkonten sind
        // Teil der Buchung (§3.4).
        var implicitSpec = new ReassignmentSpec
        {
            Id = "rw-2",
            Date = new DateOnly(2026, 11, 8),
            FromPoolId = "pool-flex",
            ToPoolId = "pool-ruecklage",
            Amount = Money.FromCents(50_000),
        };
        implicitSpec.Validate(catalog);
        Assert.Throws<LarioDomainException>(() => implicitSpec.ToBooking([]));

        // Wirksame Zeilen aus der impliziten Auflösung werden akzeptiert.
        var booking = implicitSpec.ToBooking(
            [new AccountAllocation("acc-haupt", Money.FromCents(50_000))]);
        Assert.Single(booking.ReassignmentAccounts);
    }

    [Fact(DisplayName = "Umwidmung: wirksame Verteilung, die von der expliziten Angabe abweicht, wird abgelehnt")]
    public void ReassignmentSpec_EffectiveAccountsDifferFromExplicit_Throws()
    {
        var catalog = Catalog();
        var spec = new ReassignmentSpec
        {
            Id = "rw-1",
            Date = new DateOnly(2026, 11, 8),
            FromPoolId = "pool-flex",
            ToPoolId = "pool-ruecklage",
            Amount = Money.FromCents(50_000),
            Accounts = [new AccountAllocation("acc-haupt", Money.FromCents(50_000))],
        };
        spec.Validate(catalog);

        Assert.Throws<LarioDomainException>(
            () => spec.ToBooking(
                [new AccountAllocation("acc-freizeit", Money.FromCents(50_000))]));
    }

    [Fact(DisplayName = "Umwidmung: Kontoverteilung addiert nicht exakt zum Betrag")]
    public void ReassignmentSpec_AccountsDoNotSumToAmount_Throws()
    {
        var catalog = Catalog();
        var spec = new ReassignmentSpec
        {
            Id = "rw-1",
            Date = new DateOnly(2026, 11, 8),
            FromPoolId = "pool-flex",
            ToPoolId = "pool-ruecklage",
            Amount = Money.FromCents(50_000),
            Accounts = [new AccountAllocation("acc-haupt", Money.FromCents(49_999))],
        };
        Assert.Throws<LarioDomainException>(() => spec.Validate(catalog));
    }

    [Fact(DisplayName = "Umwidmung: Quell- und Zielpool müssen verschieden sein")]
    public void ReassignmentSpec_SamePool_Throws()
    {
        var catalog = Catalog();
        var spec = new ReassignmentSpec
        {
            Id = "rw-1",
            Date = new DateOnly(2026, 11, 8),
            FromPoolId = "pool-flex",
            ToPoolId = "pool-flex",
            Amount = Money.FromCents(1_000),
            Accounts = [new AccountAllocation("acc-haupt", Money.FromCents(1_000))],
        };
        Assert.Throws<LarioDomainException>(() => spec.Validate(catalog));
    }

    [Fact(DisplayName = "Umwidmung: unbekannter bzw. archivierter Pool wird abgelehnt")]
    public void ReassignmentSpec_UnknownOrArchivedPool_Throws()
    {
        var catalog = Catalog();

        var unknown = new ReassignmentSpec
        {
            Id = "rw-1",
            Date = new DateOnly(2026, 11, 8),
            FromPoolId = "pool-unknown",
            ToPoolId = "pool-ruecklage",
            Amount = Money.FromCents(1_000),
            Accounts = [new AccountAllocation("acc-haupt", Money.FromCents(1_000))],
        };
        Assert.Throws<InvalidReferenceException>(() => unknown.Validate(catalog));

        var archived = new ReassignmentSpec
        {
            Id = "rw-2",
            Date = new DateOnly(2026, 11, 8),
            FromPoolId = "pool-flex",
            ToPoolId = "pool-altes",
            Amount = Money.FromCents(1_000),
            Accounts = [new AccountAllocation("acc-haupt", Money.FromCents(1_000))],
        };
        Assert.Throws<InvalidReferenceException>(() => archived.Validate(catalog));
    }

    // ------------------------------------------------------------------
    // TransferReassignmentSpec: Validierung und Materialisierung
    // ------------------------------------------------------------------

    [Fact(DisplayName = "Kombination: eine Buchung mit je einem Konto- und Pool-Posting-Paar")]
    public void TransferReassignmentSpec_Valid_ToBooking()
    {
        var catalog = Catalog();
        var spec = new TransferReassignmentSpec
        {
            Id = "trr-1",
            Date = new DateOnly(2026, 11, 9),
            FromAccountId = "acc-haupt",
            ToAccountId = "acc-wohnung",
            FromPoolId = "pool-flex",
            ToPoolId = "pool-ruecklage",
            Amount = Money.FromCents(10_000),
        };
        spec.Validate(catalog);

        var booking = spec.ToBooking();

        Assert.Equal(BookingType.TransferReassignment, booking.Type);
        Assert.Equal(2, booking.AccountPostings.Count);
        Assert.Equal(2, booking.PoolPostings.Count);
        Assert.Equal(Money.FromCents(-10_000),
            booking.AccountPostings.Single(p => p.AccountId == "acc-haupt").Amount);
        Assert.Equal(Money.FromCents(10_000),
            booking.AccountPostings.Single(p => p.AccountId == "acc-wohnung").Amount);
        Assert.Equal(Money.FromCents(-10_000),
            booking.PoolPostings.Single(p => p.PoolId == "pool-flex").Amount);
        Assert.Equal(Money.FromCents(10_000),
            booking.PoolPostings.Single(p => p.PoolId == "pool-ruecklage").Amount);
        // Die Kombination ist vollständig aus den Postings ableitbar.
        Assert.Empty(booking.TransferPurposes);
        Assert.Empty(booking.ReassignmentAccounts);
    }

    [Fact(DisplayName = "Kombination: unbekanntes Konto bzw. Pool wird abgelehnt")]
    public void TransferReassignmentSpec_UnknownReference_Throws()
    {
        var catalog = Catalog();

        var unknownAccount = new TransferReassignmentSpec
        {
            Id = "trr-1",
            Date = new DateOnly(2026, 11, 9),
            FromAccountId = "acc-unknown",
            ToAccountId = "acc-wohnung",
            FromPoolId = "pool-flex",
            ToPoolId = "pool-ruecklage",
            Amount = Money.FromCents(1_000),
        };
        Assert.Throws<InvalidReferenceException>(() => unknownAccount.Validate(catalog));

        var unknownPool = new TransferReassignmentSpec
        {
            Id = "trr-2",
            Date = new DateOnly(2026, 11, 9),
            FromAccountId = "acc-haupt",
            ToAccountId = "acc-wohnung",
            FromPoolId = "pool-unknown",
            ToPoolId = "pool-ruecklage",
            Amount = Money.FromCents(1_000),
        };
        Assert.Throws<InvalidReferenceException>(() => unknownPool.Validate(catalog));
    }

    // ------------------------------------------------------------------
    // Buchungsinvarianten der drei Buchungstypen (voller Konstrutor)
    // ------------------------------------------------------------------

    private static Booking TransferBooking(
        List<AccountPosting> accountPostings,
        List<PoolPosting> poolPostings,
        IReadOnlyList<PoolAllocation>? purposes = null,
        IReadOnlyList<AccountAllocation>? accounts = null) =>
        new(
            "bk-t",
            BookingType.AccountTransfer,
            new DateOnly(2026, 11, 6),
            null,
            accountPostings,
            poolPostings,
            transferPurposes: purposes,
            reassignmentAccounts: accounts);

    private static readonly PoolAllocation[] FlexPurpose20 =
    [new PoolAllocation("pool-flex", Money.FromCents(2_000))];

    [Fact(DisplayName = "Buchungsinvariante: Transfer ohne Zweckverteilung wird abgelehnt")]
    public void Transfer_WithoutPurposes_Throws()
    {
        Assert.Throws<LarioDomainException>(() => TransferBooking(
            [
                new AccountPosting { AccountId = "acc-a", Amount = Money.FromCents(-1_000) },
                new AccountPosting { AccountId = "acc-b", Amount = Money.FromCents(1_000) },
            ],
            []));
    }

    [Fact(DisplayName = "Buchungsinvariante: Transfer mit Pool-Postings wird abgelehnt (Pools bleiben unverändert)")]
    public void Transfer_WithPoolPostings_Throws()
    {
        Assert.Throws<LarioDomainException>(() => TransferBooking(
            [
                new AccountPosting { AccountId = "acc-a", Amount = Money.FromCents(-1_000) },
                new AccountPosting { AccountId = "acc-b", Amount = Money.FromCents(1_000) },
            ],
            [
                new PoolPosting { PoolId = "pool-flex", Amount = Money.FromCents(-1_000) },
                new PoolPosting { PoolId = "pool-freizeit", Amount = Money.FromCents(1_000) },
            ],
            purposes: FlexPurpose20));
    }

    [Fact(DisplayName = "Buchungsinvariante: Transfer mit drei Konto-Postings wird abgelehnt")]
    public void Transfer_WithThreeAccountPostings_Throws()
    {
        Assert.Throws<LarioDomainException>(() => TransferBooking(
            [
                new AccountPosting { AccountId = "acc-a", Amount = Money.FromCents(-600) },
                new AccountPosting { AccountId = "acc-b", Amount = Money.FromCents(-400) },
                new AccountPosting { AccountId = "acc-c", Amount = Money.FromCents(1_000) },
            ],
            [],
            purposes: FlexPurpose20));
    }

    [Fact(DisplayName = "Buchungsinvariante: Transfer, der nicht zu null addiert, wird abgelehnt")]
    public void Transfer_UnbalancedPostings_Throws()
    {
        Assert.Throws<LarioDomainException>(() => TransferBooking(
            [
                new AccountPosting { AccountId = "acc-a", Amount = Money.FromCents(-900) },
                new AccountPosting { AccountId = "acc-b", Amount = Money.FromCents(1_000) },
            ],
            [],
            purposes: FlexPurpose20));
    }

    [Fact(DisplayName = "Buchungsinvariante: Transfer mit Kontoverteilung wird abgelehnt (gehört zur Umwidmung)")]
    public void Transfer_WithReassignmentAccounts_Throws()
    {
        Assert.Throws<LarioDomainException>(() => TransferBooking(
            [
                new AccountPosting { AccountId = "acc-a", Amount = Money.FromCents(-1_000) },
                new AccountPosting { AccountId = "acc-b", Amount = Money.FromCents(1_000) },
            ],
            [],
            purposes: FlexPurpose20,
            accounts: [new AccountAllocation("acc-a", Money.FromCents(1_000))]));
    }

    private static Booking ReassignmentBooking(
        List<AccountPosting> accountPostings,
        List<PoolPosting> poolPostings,
        IReadOnlyList<AccountAllocation>? accounts = null,
        IReadOnlyList<PoolAllocation>? purposes = null) =>
        new(
            "bk-r",
            BookingType.Reassignment,
            new DateOnly(2026, 11, 8),
            null,
            accountPostings,
            poolPostings,
            transferPurposes: purposes,
            reassignmentAccounts: accounts);

    [Fact(DisplayName = "Buchungsinvariante: Umwidmung mit Konto-Postings wird abgelehnt")]
    public void Reassignment_WithAccountPostings_Throws()
    {
        Assert.Throws<LarioDomainException>(() => ReassignmentBooking(
            [
                new AccountPosting { AccountId = "acc-a", Amount = Money.FromCents(-1_000) },
                new AccountPosting { AccountId = "acc-b", Amount = Money.FromCents(1_000) },
            ],
            [
                new PoolPosting { PoolId = "pool-flex", Amount = Money.FromCents(-1_000) },
                new PoolPosting { PoolId = "pool-ruecklage", Amount = Money.FromCents(1_000) },
            ],
            accounts: [new AccountAllocation("acc-a", Money.FromCents(1_000))]));
    }

    [Fact(DisplayName = "Buchungsinvariante: Umwidmung mit Pool-Postings, die nicht zu null addieren, wird abgelehnt")]
    public void Reassignment_UnbalancedPoolPostings_Throws()
    {
        Assert.Throws<LarioDomainException>(() => ReassignmentBooking(
            [],
            [
                new PoolPosting { PoolId = "pool-flex", Amount = Money.FromCents(-900) },
                new PoolPosting { PoolId = "pool-ruecklage", Amount = Money.FromCents(1_000) },
            ],
            accounts: [new AccountAllocation("acc-a", Money.FromCents(1_000))]));
    }

    [Fact(DisplayName = "Buchungsinvariante: Umwidmung mit Zweckverteilung wird abgelehnt (gehört zum Transfer)")]
    public void Reassignment_WithTransferPurposes_Throws()
    {
        Assert.Throws<LarioDomainException>(() => ReassignmentBooking(
            [],
            [
                new PoolPosting { PoolId = "pool-flex", Amount = Money.FromCents(-1_000) },
                new PoolPosting { PoolId = "pool-ruecklage", Amount = Money.FromCents(1_000) },
            ],
            accounts: [new AccountAllocation("acc-a", Money.FromCents(1_000))],
            purposes: [new PoolAllocation("pool-flex", Money.FromCents(1_000))]));
    }

    [Fact(DisplayName = "Buchungsinvariante: Kombination mit unterschiedlichen Konto-/Poolbeträgen wird abgelehnt")]
    public void TransferReassignment_AccountAndPoolTotalsDiffer_Throws()
    {
        Assert.Throws<LarioDomainException>(() => new Booking(
            "bk-c",
            BookingType.TransferReassignment,
            new DateOnly(2026, 11, 9),
            null,
            [
                new AccountPosting { AccountId = "acc-a", Amount = Money.FromCents(-1_000) },
                new AccountPosting { AccountId = "acc-b", Amount = Money.FromCents(1_000) },
            ],
            [
                new PoolPosting { PoolId = "pool-flex", Amount = Money.FromCents(-900) },
                new PoolPosting { PoolId = "pool-ruecklage", Amount = Money.FromCents(900) },
            ]));
    }

    [Fact(DisplayName = "Buchungsinvariante: Kombination mit Zusatzverteilungen wird abgelehnt")]
    public void TransferReassignment_WithExtraAllocations_Throws()
    {
        Assert.Throws<LarioDomainException>(() => new Booking(
            "bk-c",
            BookingType.TransferReassignment,
            new DateOnly(2026, 11, 9),
            null,
            [
                new AccountPosting { AccountId = "acc-a", Amount = Money.FromCents(-1_000) },
                new AccountPosting { AccountId = "acc-b", Amount = Money.FromCents(1_000) },
            ],
            [
                new PoolPosting { PoolId = "pool-flex", Amount = Money.FromCents(-1_000) },
                new PoolPosting { PoolId = "pool-ruecklage", Amount = Money.FromCents(1_000) },
            ],
            transferPurposes: [new PoolAllocation("pool-flex", Money.FromCents(1_000))]));
    }
}
