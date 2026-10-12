using Lario.Domain;

namespace Lario.Domain.Tests;

/// <summary>
/// <see cref="YearMonth"/> (Schritt 4): Kalendermonat als Werttyp —
/// Format YYYY-MM, Arithmetik, Parse mit strenger Validierung.
/// </summary>
public class YearMonthTests
{
    [Fact]
    public void Create_GültigenMonat_Akzeptiert()
    {
        var m = YearMonth.Create(2026, 11);
        Assert.Equal(2026, m.Year);
        Assert.Equal(11, m.Month);
    }

    [Theory]
    [InlineData(2026, 0)]
    [InlineData(2026, 13)]
    [InlineData(2026, -1)]
    [InlineData(-1, 5)]
    [InlineData(10_000, 5)]
    public void Create_UngültigerMonatOderJahr_Wirft(int year, int month)
    {
        Assert.Throws<ArgumentException>(() => YearMonth.Create(year, month));
    }

    [Theory]
    [InlineData(2026, 0)]
    [InlineData(2026, 13)]
    [InlineData(2026, -1)]
    [InlineData(-1, 5)]
    [InlineData(10_000, 5)]
    public void Konstrutor_UngültigerMonatOderJahr_Wirft(int year, int month)
    {
        // Regression (Schritt-4-Review P2c): der direkte Konstrutor muss
        // dieselbe Invariante wie Create erzwingen — ein ungültiger Wert
        // darf nicht durch direct construction entstehen.
        Assert.Throws<ArgumentException>(() => new YearMonth(year, month));
    }

    [Fact]
    public void Default_IstUngültig()
    {
        // Regression (Schritt-4-Review P2c): default(YearMonth) (0000-00)
        // umgeht den Konstrutor und muss als ungültig erkennbar sein —
        // Domäneneingänge (z. B. IncomeSpec.FundingMonth) prüfen IsValid
        // vor der Speicherung.
        var d = default(YearMonth);
        Assert.False(d.IsValid);
        Assert.Equal(0, d.Year);
        Assert.Equal(0, d.Month);
    }

    [Theory]
    [InlineData(2026, 11)]
    [InlineData(1, 1)]
    [InlineData(9999, 12)]
    public void IsValid_GültigeWerte_True(int year, int month)
    {
        Assert.True(YearMonth.Create(year, month).IsValid);
    }

    [Fact]
    public void Equality_DefaultIstNichtGleichMitGültigemMonat()
    {
        Assert.NotEqual(default(YearMonth), YearMonth.Create(2026, 11));
    }

    [Fact]
    public void FromDate_NimmtKalendermonatDesDatums()
    {
        Assert.Equal(YearMonth.Create(2026, 11), YearMonth.FromDate(new DateOnly(2026, 11, 15)));
        Assert.Equal(YearMonth.Create(2026, 1), YearMonth.FromDate(new DateOnly(2026, 1, 31)));
    }

    [Theory]
    [InlineData("2026-11")]
    [InlineData("1999-01")]
    [InlineData("2031-12")]
    public void Parse_GültigesFormat_Akzeptiert(string s)
    {
        var m = YearMonth.Parse(s);
        Assert.Equal(s, m.ToString());
    }

    [Theory]
    [InlineData("2026-1", typeof(FormatException))]    // Monat ohne Nullpadding
    [InlineData("26-11", typeof(FormatException))]     // zweistelliges Jahr
    [InlineData("2026-13", typeof(ArgumentException))] // Monat 13
    [InlineData("2026-00", typeof(ArgumentException))] // Monat 0
    [InlineData("2026-11-01", typeof(FormatException))] // ganzes Datum statt Monat
    [InlineData("2026/11", typeof(FormatException))]    // falscher Trenner
    [InlineData("nov-2026", typeof(FormatException))]  // Text statt Zahlen
    public void Parse_UngültigesFormat_Wirft(string s, Type expectedException)
    {
        Assert.Throws(expectedException, () => YearMonth.Parse(s));
    }

    [Fact]
    public void Parse_LeererString_Wirft()
    {
        Assert.Throws<ArgumentException>(() => YearMonth.Parse(""));
    }

    [Theory]
    [InlineData(2026, 1, 2026, 2)]
    [InlineData(2026, 11, 2026, 12)]
    [InlineData(2026, 12, 2027, 1)]
    public void Next_SteigtÜberJahresgrenze(int year, int month, int expectedYear, int expectedMonth)
    {
        Assert.Equal(YearMonth.Create(expectedYear, expectedMonth),
            YearMonth.Create(year, month).Next());
    }

    [Fact]
    public void ToString_Roundtrip()
    {
        var m = YearMonth.Parse("2026-07");
        Assert.Equal("2026-07", m.ToString());
    }

    [Fact]
    public void CompareTo_VergleichtChronologisch()
    {
        Assert.True(YearMonth.Create(2025, 12).CompareTo(YearMonth.Create(2026, 1)) < 0);
        Assert.True(YearMonth.Create(2026, 1).CompareTo(YearMonth.Create(2026, 2)) < 0);
        Assert.True(YearMonth.Create(2026, 12).CompareTo(YearMonth.Create(2026, 1)) > 0);
        Assert.Equal(0, YearMonth.Create(2026, 7).CompareTo(YearMonth.Create(2026, 7)));
    }

    [Fact]
    public void Equality_GleicheJahrUndMonat()
    {
        Assert.Equal(YearMonth.Create(2026, 11), YearMonth.Parse("2026-11"));
    }
}
