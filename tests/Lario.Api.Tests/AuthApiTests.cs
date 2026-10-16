using Lario.Host.Api;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Lario.Api.Tests;

/// <summary>
/// Login/Logout und Bearer-Schutz der /api/v1-Oberfläche (Schritt 7,
/// Design D1/D2) gegen den echten Host mit In-Memory-Testserver.
/// </summary>
public class AuthApiTests : IDisposable
{
    private readonly LarioApiFactory _factory;

    public AuthApiTests() => _factory = new LarioApiFactory();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Login_mit_korrekten_Credentials_liefert_Token_und_Gueltigkeit()
    {
        var login = await LoginAsync(NewClient());

        Assert.False(string.IsNullOrWhiteSpace(login.Token));
        Assert.Equal(720, login.ExpiresInMinutes);
    }

    [Fact]
    public async Task Login_mit_falschem_Passwort_liefert_401_im_verbindlichen_Fehlerformat()
    {
        var response = await NewClient().PostAsJsonAsync("/api/v1/auth/login",
            new { username = LarioApiFactory.TestUsername, password = "falsches-pw" });

        Assert.Equal(401, (int)response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(body);
        Assert.Equal("unauthorized", body!.Error.Code);
        Assert.False(string.IsNullOrWhiteSpace(body.Error.Message));
    }

    [Fact]
    public async Task Login_mit_falschem_Benutzernamen_liefert_401()
    {
        var response = await NewClient().PostAsJsonAsync("/api/v1/auth/login",
            new { username = "fremd", password = LarioApiFactory.TestPassword });

        Assert.Equal(401, (int)response.StatusCode);
    }

    [Fact]
    public async Task Geschoetzter_Endpunkt_ohne_Token_liefert_401()
    {
        var response = await NewClient().PostAsync("/api/v1/auth/logout", content: null);

        Assert.Equal(401, (int)response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("unauthorized", body!.Error.Code);
    }

    [Fact]
    public async Task Geschoetzter_Endpunkt_mit_ungueltigem_Token_liefert_401()
    {
        var client = NewClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "falsches-token");

        var response = await client.PostAsync("/api/v1/auth/logout", content: null);

        Assert.Equal(401, (int)response.StatusCode);
    }

    [Fact]
    public async Task Logout_entwertet_den_Token()
    {
        var login = await LoginAsync(NewClient());

        var logout = await NewClient(login.Token).PostAsync("/api/v1/auth/logout", content: null);
        Assert.Equal(204, (int)logout.StatusCode);

        // Der Token ist entwertet: weiterer Gebrauch scheitert.
        var rejected = await NewClient(login.Token).PostAsync("/api/v1/auth/logout", content: null);
        Assert.Equal(401, (int)rejected.StatusCode);
    }

    [Fact]
    public async Task Konfigurierte_Credentials_erzeugen_keine_credentials_json()
    {
        // Factory mit konfigurierten Credentials (Default): kein Hash-Datensatz.
        await LoginAsync(NewClient());
        Assert.False(File.Exists(_factory.CredentialFilePath));
    }

    [Fact]
    public async Task Neustart_aktualisiert_Tokens_und_nimmt_Credentials_aus_der_Ablage()
    {
        // A4: erster Host läuft mit eigenen Credentials aus credentials.json.
        var dataDirectory = Directory.CreateTempSubdirectory("lario-restart-test-").FullName;
        try
        {
            var first = new LarioApiFactory(dataDirectory, configuredCredentials: false);
            var output = new StringWriter();
            var original = Console.Out;
            string firstToken;
            string generatedPassword;
            try
            {
                Console.SetOut(output);
                var client1 = first.CreateClient();
                generatedPassword = ExtractGeneratedPassword(output.ToString());
                var login1 = await client1.PostAsJsonAsync("/api/v1/auth/login",
                    new { username = "lario", password = generatedPassword });
                Assert.Equal(200, (int)login1.StatusCode);
                firstToken = (await login1.Content.ReadFromJsonAsync<LoginResponse>())!.Token;

                var access1 = await WithToken(first.CreateClient(), firstToken)
                    .PostAsync("/api/v1/auth/logout", content: null);
                Assert.Equal(204, (int)access1.StatusCode);
            }
            finally
            {
                Console.SetOut(original);
            }
            first.Dispose();

            // Zweiter Host mit derselben Ablage: Token aus Lauf 1 ist tot
            // (In-Memory-Store), Credentials funktionieren weiter.
            using var second = new LarioApiFactory(dataDirectory, configuredCredentials: false);
            var client2 = second.CreateClient();

            var stale = await WithToken(second.CreateClient(), firstToken)
                .PostAsync("/api/v1/auth/logout", content: null);
            Assert.Equal(401, (int)stale.StatusCode);

            var login2 = await client2.PostAsJsonAsync("/api/v1/auth/login",
                new { username = "lario", password = generatedPassword });
            Assert.Equal(200, (int)login2.StatusCode);
            var token2 = (await login2.Content.ReadFromJsonAsync<LoginResponse>())!.Token;

            var access2 = await WithToken(second.CreateClient(), token2)
                .PostAsync("/api/v1/auth/logout", content: null);
            Assert.Equal(204, (int)access2.StatusCode);
        }
        finally
        {
            try
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private HttpClient NewClient(string? token = null)
    {
        var client = _factory.CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return client;
    }

    private static HttpClient WithToken(HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<LoginResponse> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = LarioApiFactory.TestUsername, password = LarioApiFactory.TestPassword });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    private static string ExtractGeneratedPassword(string consoleOutput)
    {
        const string marker = "einmalige Anzeige): ";
        var index = consoleOutput.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, "Konsole muss das erzeugte Passwort anzeigen.");
        var rest = consoleOutput[(index + marker.Length)..];
        var end = rest.IndexOf('\n', StringComparison.Ordinal);
        var password = end < 0 ? rest : rest[..end];
        Assert.False(string.IsNullOrWhiteSpace(password));
        return password.Trim();
    }
}
