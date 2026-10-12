using Lario.Domain;
using Lario.Domain.Journal;
using Lario.Domain.MasterData;

namespace Lario.Domain.Tests;

/// <summary>
/// Einnahme (Schritt 4, Rechenvertrag §3.2/§2.2): ein Konto, exakte
/// Pool-Verteilung, materieller Finanzierungsmonat (Default: Folgemonat),
/// keine Teilposten, keine Kontobewegung im Finanzierungsmonat.
/// Referenzzahl Plan §5 Schritt 4 „Prüfen" 1: 1.100 €.
/// </summary>
public class IncomeSpecTests
{
    private static readonly DateOnly Eingangsdatum = new(2026, 10, 5);

    private static MasterDataCatalog Catalog()
    {
        var catalog = new MasterDataCatalog([], [], [], []);
        catalog.CreateAccount("acc-haupt", "Hauptkonto", AccountType.Bank);
        catalog.CreateAccount("acc-wohnung", "Wohnungskonto", AccountType.Bank);
        catalog.CreateAccount("acc-altes", "Altkonto", AccountType.Bank);
        catalog.ArchiveAccount("acc-altes");
        catalog.CreatePool("pool-flex", "Flex");
        catalog.CreatePool("pool-freizeit", "Freizeit");
        catalog.CreatePool("pool-altes", "Alter Pool");
        catalog.ArchivePool("pool-altes");
        return catalog;
    }

    private static IncomeSpec Spec(
        string id,
        Money amount,
        string accountId = "acc-haupt",
        YearMonth? fundingMonth = null,
        IReadOnlyList<IncomeAllocationSpec>? allocations = null) =>
        new()
        {
            Id = id,
            Date = Eingangsdatum,
            AccountId = accountId,
            Amount = amount,
            FundingMonth = fundingMonth,
            Allocations = allocations
                ?? [new IncomeAllocationSpec("pool-flex", amount)],
        };

    [Fact(DisplayName = "Plan-Kernfall: 1.100 € Einnahme, Default-Finanzierungsmonat = Folgemonat")]
    public void PlanCase_DefaultFundingMonth_IsNextMonth()
    {
        var spec = Spec("ein-1", Money.FromCents(110_000), "acc-wohnung");
        spec.Validate(Catalog());

        // Tatsächliches Datum bleibt der Eingang; der Finanzierungsmonat
        // wird als Wert materialisiert und finanziert den Folgemonat.
        Assert.Equal(YearMonth.Create(2026, 11), spec.EffectiveFundingMonth());

        var booking = spec.ToBooking();
        Assert.Equal(BookingType.Income, booking.Type);
        Assert.Single(booking.AccountPostings);
        Assert.Equal("acc-wohnung", booking.AccountPostings[0].AccountId);
        Assert.Equal(Money.FromCents(110_000), booking.AccountPostings[0].Amount);
        Assert.Equal(YearMonth.Create(2026, 11), booking.FundingMonth);
        Assert.Empty(booking.Splits);
        Assert.Single(booking.PoolPostings);
        Assert.Equal(Money.FromCents(110_000), booking.PoolPostings[0].Amount);
    }

    [Fact(DisplayName = "Expliziter Finanzierungsmonat wird übernommen")]
    public void ExplicitFundingMonth_Wins()
    {
        var spec = Spec("ein-2", Money.FromCents(110_000),
            fundingMonth: YearMonth.Create(2027, 3));
        spec.Validate(Catalog());
        Assert.Equal(YearMonth.Create(2027, 3), spec.ToBooking().FundingMonth);
    }

    [Fact(DisplayName = "Default-Finanzierungsmonat über Jahresgrenze: Dezember → Januar")]
    public void DefaultFundingMonth_CrossesYearBoundary()
    {
        var spec = new IncomeSpec
        {
            Id = "ein-3",
            Date = new DateOnly(2026, 12, 28),
            AccountId = "acc-haupt",
            Amount = Money.FromCents(5_000),
            Allocations = [new IncomeAllocationSpec("pool-flex", Money.FromCents(5_000))],
        };

        Assert.Equal(YearMonth.Create(2027, 1), spec.EffectiveFundingMonth());
        Assert.Equal(YearMonth.Create(2027, 1), spec.ToBooking().FundingMonth);
    }

    [Fact(DisplayName = "Verteilung kleiner als Gesamtbetrag wird abgelehnt")]
    public void UnderDistribution_IsRejected()
    {
        var spec = Spec("ein-4", Money.FromCents(110_000), allocations:
        [new IncomeAllocationSpec("pool-flex", Money.FromCents(109_999))]);

        Assert.Throws<LarioDomainException>(() => spec.Validate(Catalog()));
    }

    [Fact(DisplayName = "Verteilung größer als Gesamtbetrag wird abgelehnt")]
    public void OverDistribution_IsRejected()
    {
        var spec = Spec("ein-5", Money.FromCents(110_000), allocations:
        [new IncomeAllocationSpec("pool-flex", Money.FromCents(110_001))]);

        Assert.Throws<LarioDomainException>(() => spec.Validate(Catalog()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Amount_ZeroOrNegative_IsRejected(long cents)
    {
        var spec = Spec("ein-6", Money.FromCents(cents));
        Assert.Throws<LarioDomainException>(() => spec.Validate(Catalog()));
    }

    [Theory]
    [InlineData("acc-geister")]
    [InlineData("acc-altes")]
    public void UnknownOrArchivedAccount_Throws(string accountId)
    {
        var spec = Spec("ein-7", Money.FromCents(10_000), accountId: accountId);
        Assert.Throws<InvalidReferenceException>(() => spec.Validate(Catalog()));
    }

    [Theory]
    [InlineData("pool-geister")]
    [InlineData("pool-altes")]
    public void UnknownOrArchivedPool_Throws(string poolId)
    {
        var spec = Spec("ein-8", Money.FromCents(10_000), allocations:
        [new IncomeAllocationSpec(poolId, Money.FromCents(10_000))]);
        Assert.Throws<InvalidReferenceException>(() => spec.Validate(Catalog()));
    }

    [Fact(DisplayName = "Leere oder doppelte Pool-Verteilung wird abgelehnt")]
    public void EmptyOrDuplicateAllocation_IsRejected()
    {
        Assert.Throws<LarioDomainException>(() =>
            Spec("ein-9a", Money.FromCents(10_000), allocations: [])
                .Validate(Catalog()));

        Assert.Throws<LarioDomainException>(() =>
            Spec("ein-9b", Money.FromCents(20_000), allocations:
            [
                new IncomeAllocationSpec("pool-flex", Money.FromCents(10_000)),
                new IncomeAllocationSpec("pool-flex", Money.FromCents(10_000)),
            ]).Validate(Catalog()));
    }

    [Fact(DisplayName = "Nullzeile in der Verteilung wird abgelehnt")]
    public void ZeroAllocationLine_IsRejected()
    {
        var spec = Spec("ein-10", Money.FromCents(10_000), allocations:
        [
            new IncomeAllocationSpec("pool-flex", Money.FromCents(10_000)),
            new IncomeAllocationSpec("pool-freizeit", Money.Zero),
        ]);
        Assert.Throws<LarioDomainException>(() => spec.Validate(Catalog()));
    }

    [Fact(DisplayName = "Buchungs-Invariante: Einnahme ohne Finanzierungsmonat wird abgelehnt")]
    public void Booking_WithoutFundingMonth_IsRejected()
    {
        Assert.Throws<LarioDomainException>(() => new Booking(
            "ein-11",
            BookingType.Income,
            Eingangsdatum,
            null,
            [new AccountPosting { AccountId = "acc-haupt", Amount = Money.FromCents(10_000) }],
            [new PoolPosting { PoolId = "pool-flex", Amount = Money.FromCents(10_000) }],
            fundingMonth: null));
    }

    [Fact(DisplayName = "Buchungs-Invariante: Einnahme mit Teilposten wird abgelehnt")]
    public void Booking_WithSplits_IsRejected()
    {
        Assert.Throws<LarioDomainException>(() => new Booking(
            "ein-12",
            BookingType.Income,
            Eingangsdatum,
            null,
            [new AccountPosting { AccountId = "acc-haupt", Amount = Money.FromCents(10_000) }],
            [new PoolPosting { PoolId = "pool-flex", Amount = Money.FromCents(10_000) }],
            fundingMonth: YearMonth.Create(2026, 11),
            splits: [new ExpenseSplit
            {
                Id = "s1",
                Amount = Money.FromCents(10_000),
                PoolId = "pool-flex",
                CategoryId = "cat-1",
            }]));
    }

    [Fact(DisplayName = "Buchungs-Invariante: negative Einnahme wird abgelehnt")]
    public void Booking_NegativeIncome_IsRejected()
    {
        Assert.Throws<LarioDomainException>(() => new Booking(
            "ein-13",
            BookingType.Income,
            Eingangsdatum,
            null,
            [new AccountPosting { AccountId = "acc-haupt", Amount = Money.FromCents(-10_000) }],
            [new PoolPosting { PoolId = "pool-flex", Amount = Money.FromCents(-10_000) }],
            fundingMonth: YearMonth.Create(2026, 11)));
    }

    [Fact(DisplayName = "Buchungs-Invariante: Pool-Postings müssen zum Kontozugang addieren")]
    public void Booking_PoolSumMismatch_IsRejected()
    {
        Assert.Throws<LarioDomainException>(() => new Booking(
            "ein-14",
            BookingType.Income,
            Eingangsdatum,
            null,
            [new AccountPosting { AccountId = "acc-haupt", Amount = Money.FromCents(10_000) }],
            [new PoolPosting { PoolId = "pool-flex", Amount = Money.FromCents(9_999) }],
            fundingMonth: YearMonth.Create(2026, 11)));
    }
}
