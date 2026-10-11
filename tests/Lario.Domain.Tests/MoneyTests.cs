using System.Globalization;
using Lario.Domain;

namespace Lario.Domain.Tests;

/// <summary>
/// Referenzwerte für die verbindliche Geldrepräsentation
/// (EUR als ganze Cents, Rechenvertrag docs/step-0/rechenvertrag.md).
/// </summary>
public class MoneyTests
{
    [Fact(DisplayName = "10,99 EUR + 0,01 EUR = 11,00 EUR (exakt, ohne float-Fehler)")]
    public void Add_RoundUpAtCentBoundary_IsExact()
    {
        Money result = Money.FromEuro(10.99m).Add(Money.FromEuro(0.01m));

        Assert.Equal(1100, result.Cents);
        Assert.Equal(Money.FromCents(1100), result);
    }

    [Fact(DisplayName = "Splitbeträge summieren sich exakt zum Gesamtbetrag (333+333+334 = 1000)")]
    public void SplitAmounts_SumExactlyToTotal()
    {
        Money total = Money.FromCents(333).Add(Money.FromCents(333)).Add(Money.FromCents(334));

        Assert.Equal(Money.FromCents(1000), total);
    }

    [Fact(DisplayName = "Subtraktion führt exakt auf null (Ausgabe reduziert Pool vollständig)")]
    public void Subtract_FullAmount_GoesToZero()
    {
        Money pool = Money.FromCents(5000);

        Money remaining = pool.Subtract(Money.FromCents(2000)).Subtract(Money.FromCents(3000));

        Assert.Equal(Money.Zero, remaining);
    }

    [Fact(DisplayName = "Negative Salden sind darstellbar (Unterdeckung wird sichtbar, nicht versteckt)")]
    public void NegativeBalances_AreRepresentable()
    {
        Money underfunded = Money.Zero.Subtract(Money.FromCents(75));

        Assert.Equal(-75, underfunded.Cents);
        Assert.True(underfunded < Money.Zero);
    }

    [Fact(DisplayName = "Negation kehrt das Vorzeichen um und ist invers")]
    public void Negate_IsInverse()
    {
        Money amount = Money.FromCents(-12345);

        Assert.Equal(Money.FromCents(12345), amount.Negate());
        Assert.Equal(amount, amount.Negate().Negate());
    }

    [Fact(DisplayName = "Negation von long.MinValue wird abgelehnt (nicht darstellbar)")]
    public void Negate_LongMinValue_Throws()
    {
        Assert.Throws<OverflowException>(() => Money.FromCents(long.MinValue).Negate());
    }

    // Hinweis: 0 +/- long.MaxValue ist KEIN Überlauf (Ergebnis ist der Grenzwert selbst)
    // und wird im Test AtBoundaryWithoutOverflow abgedeckt.
    [Theory]
    [InlineData(long.MaxValue, 1)]
    [InlineData(1, long.MaxValue)]
    [InlineData(long.MaxValue, long.MaxValue)]
    public void Add_OverflowAboveLongMax_Throws(long a, long b)
    {
        Assert.Throws<OverflowException>(() => Money.FromCents(a).Add(Money.FromCents(b)));
    }

    [Theory]
    [InlineData(long.MinValue, -1)]
    [InlineData(-1, long.MinValue)]
    [InlineData(long.MinValue, long.MinValue)]
    public void Add_OverflowBelowLongMin_Throws(long a, long b)
    {
        Assert.Throws<OverflowException>(() => Money.FromCents(a).Add(Money.FromCents(b)));
    }

    [Theory]
    [InlineData(long.MinValue, 1)]
    [InlineData(0, long.MinValue)]
    public void Subtract_OverflowBelowLongMin_Throws(long a, long b)
    {
        Assert.Throws<OverflowException>(() => Money.FromCents(a).Subtract(Money.FromCents(b)));
    }

    [Theory]
    [InlineData(long.MaxValue, -1)]
    [InlineData(1, long.MinValue)]
    public void Subtract_OverflowAboveLongMax_Throws(long a, long b)
    {
        Assert.Throws<OverflowException>(() => Money.FromCents(a).Subtract(Money.FromCents(b)));
    }

    [Fact(DisplayName = "Grenzwerte ohne Überlauf sind erlaubt (long.MaxValue bleibt erhaltungsfähig)")]
    public void Add_AtBoundaryWithoutOverflow_IsAllowed()
    {
        Assert.Equal(long.MaxValue, Money.FromCents(long.MaxValue).Add(Money.Zero).Cents);
        Assert.Equal(long.MinValue, Money.FromCents(long.MinValue).Subtract(Money.Zero).Cents);
    }

    // decimal ist als Attributargument in C# nicht erlaubt — die Werte kommen
    // als invarianter Text und werden explizit geparst (exakter decimal-Wert).
    [Theory]
    [InlineData("1.005", 101)]        // halber Cent -> von null weg (100,5 -> 101)
    [InlineData("-1.005", -101)]      // symmetrisch
    [InlineData("10.999", 1100)]      // 1099,9 -> 1100
    [InlineData("0.004", 0)]          // unter halben Cent -> 0
    [InlineData("100000000", 10000000000)]
    public void FromEuro_RoundsHalfCentsAwayFromZero(string euroText, long expectedCents)
    {
        decimal euro = decimal.Parse(euroText, CultureInfo.InvariantCulture);

        Assert.Equal(expectedCents, Money.FromEuro(euro).Cents);
    }

    [Fact(DisplayName = "ToEuro ist die exakte Umkehrung für ganze Centwerte")]
    public void ToEuro_RoundTripsForWholeCents()
    {
        Money amount = Money.FromCents(-123456);

        Assert.Equal(-1234.56m, amount.ToEuro());
        Assert.Equal(amount, Money.FromEuro(amount.ToEuro()));
    }

    [Fact(DisplayName = "Gleichheit und Vergleich folgen dem Centwert")]
    public void EqualityAndComparison_FollowCents()
    {
        Assert.Equal(Money.FromCents(50), new Money(50));
        Assert.True(Money.FromCents(49) < Money.FromCents(50));
        Assert.True(Money.FromCents(51) > Money.FromCents(50));
        Assert.True(Money.Zero >= Money.Zero);
        Assert.True(Money.FromCents(-1) < Money.Zero);
    }
}
