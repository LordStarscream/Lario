using Lario.Domain;
using Lario.Domain.Journal;

namespace Lario.Domain.Tests;

/// <summary>
/// Typ-Invarianten der <see cref="Booking"/> (Schritt 4): Einnahme braucht
/// exakt eine positive Kontoposting, Pool-Postings, die zum Kontozugang
/// addieren, und einen Finanzierungsmonat — ohne Teilposten. Ausgabe
/// braucht Teilposten (Splitregel) und keinen Finanzierungsmonat.
/// Anfangsbestand trägt weder Finanzierungsmonat noch Teilposten.
/// </summary>
public class BookingTypeInvariantsTests
{
    private static readonly DateOnly Datum = new(2026, 10, 5);

    private static List<AccountPosting> Account(Money amount) =>
        [new AccountPosting { AccountId = "acc-haupt", Amount = amount }];

    private static List<PoolPosting> Pool(Money amount) =>
        [new PoolPosting { PoolId = "pool-flex", Amount = amount }];

    [Fact(DisplayName = "Einnahme ohne Finanzierungsmonat verletzt die Invariante")]
    public void Income_WithoutFundingMonth_Throws()
    {
        Assert.Throws<LarioDomainException>(() => new Booking(
            "ein-inv-1", BookingType.Income, Datum, null,
            Account(Money.FromCents(10_000)), Pool(Money.FromCents(10_000))));
    }

    [Fact(DisplayName = "Ausgabe ohne Teilposten verletzt die Invariante")]
    public void Expense_WithoutSplits_Throws()
    {
        Assert.Throws<LarioDomainException>(() => new Booking(
            "aus-inv-1", BookingType.Expense, Datum, null,
            Account(Money.FromCents(-10_000)), Pool(Money.FromCents(-10_000))));
    }

    [Fact(DisplayName = "Ausgabe mit Finanzierungsmonat verletzt die Invariante")]
    public void Expense_WithFundingMonth_Throws()
    {
        Assert.Throws<LarioDomainException>(() => new Booking(
            "aus-inv-2", BookingType.Expense, Datum, null,
            Account(Money.FromCents(-10_000)), Pool(Money.FromCents(-10_000)),
            fundingMonth: YearMonth.Create(2026, 11),
            splits: [new ExpenseSplit
            {
                Id = "s1",
                Amount = Money.FromCents(10_000),
                PoolId = "pool-flex",
                CategoryId = "cat-1",
            }]));
    }

    [Fact(DisplayName = "Anfangsbestand bleibt ohne Finanzierungsmonat und Teilposten gültig")]
    public void OpeningBalance_StaysValid()
    {
        var booking = new Booking(
            "init-inv-1", BookingType.OpeningBalance, Datum, null,
            Account(Money.FromCents(10_000)), Pool(Money.FromCents(10_000)),
            openingBalanceAccountId: "acc-haupt");

        Assert.Null(booking.FundingMonth);
        Assert.Empty(booking.Splits);
    }

    [Fact(DisplayName = "Ausgabe: Teilposten-Pool ohne Pool-Posting verletzt die Invariante")]
    public void Expense_SplitPoolWithoutPoolPosting_Throws()
    {
        Assert.Throws<LarioDomainException>(() => new Booking(
            "aus-inv-3", BookingType.Expense, Datum, null,
            Account(Money.FromCents(-10_000)), Pool(Money.FromCents(-10_000)),
            splits: [new ExpenseSplit
            {
                Id = "s1",
                Amount = Money.FromCents(10_000),
                PoolId = "pool-fremd",
                CategoryId = "cat-1",
            }]));
    }

    [Fact(DisplayName = "Ausgabe: Split-Summe ungleich Gesamtbetrag verletzt die Invariante")]
    public void Expense_SplitSumMismatch_Throws()
    {
        Assert.Throws<LarioDomainException>(() => new Booking(
            "aus-inv-4", BookingType.Expense, Datum, null,
            Account(Money.FromCents(-10_000)), Pool(Money.FromCents(-10_000)),
            splits: [new ExpenseSplit
            {
                Id = "s1",
                Amount = Money.FromCents(9_999),
                PoolId = "pool-flex",
                CategoryId = "cat-1",
            }]));
    }
}
