using Lario.Host.Api;

namespace Lario.Api.Tests;

/// <summary>
/// Token-Gültigkeit und -Entwertung (Schritt 7, Design D1) mit
/// steuerbarer Zeitquelle — keine echten Wartezeiten im Test.
/// </summary>
public class AuthTokenStoreTests
{
    [Fact]
    public void Ausgestelltes_Token_wird_zuerst_als_gueltig_erkannt()
    {
        var store = new AuthTokenStore();
        var (token, expiresAt) = store.Issue();

        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.True(store.TryValidate(token));
        Assert.True(expiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Abgelaufenes_Token_wird_abgelehnt()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new AuthTokenStore(TimeSpan.FromMinutes(1), () => now);
        var (token, _) = store.Issue();

        Assert.True(store.TryValidate(token));

        now = now.AddMinutes(2);
        Assert.False(store.TryValidate(token));

        // Ein abgelaufenes Token ist nicht wieder lebbar, auch wenn die
        // Zeitquelle zurückgesetzt wird (es wurde entfernt, nicht nur geprüft).
        now = now.AddMinutes(-3);
        Assert.False(store.TryValidate(token));
    }

    [Fact]
    public void Revoke_macht_das_Token_unverwendbar()
    {
        var store = new AuthTokenStore();
        var (token, _) = store.Issue();
        store.Revoke(token);

        Assert.False(store.TryValidate(token));
        Assert.False(store.TryValidate(token)); // idempotent
    }

    [Fact]
    public void Issue_erzeugt_jedes_Mal_ein_anderes_Token()
    {
        var store = new AuthTokenStore();
        var first = store.Issue().Token;
        var second = store.Issue().Token;

        Assert.NotEqual(first, second);
        Assert.True(store.TryValidate(first)); // beide gleichzeitig gültig
        Assert.True(store.TryValidate(second));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("kein-gueltiges-token")]
    public void Fehlendes_oder_unbekanntes_Token_wird_abgelehnt(string? token)
    {
        var store = new AuthTokenStore();
        Assert.False(store.TryValidate(token));
    }
}
