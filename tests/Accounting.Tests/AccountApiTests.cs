using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Accounting.Tests;

public sealed class AccountApiTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"accounting-{Guid.NewGuid():N}.db");

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("Database:Path", _dbPath));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            File.Delete(_dbPath + suffix);
        }
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Post(HttpClient client, string path, string json)
    {
        var response = await client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(text.StartsWith('{'), text);
        return (response.StatusCode, JsonSerializer.Deserialize<JsonElement>(text));
    }

    private static Task<(HttpStatusCode Status, JsonElement Body)> Credit(HttpClient c, long cents) =>
        Post(c, "/api/account/credits", $"{{\"amountCents\":{cents}}}");

    private static Task<(HttpStatusCode Status, JsonElement Body)> Debit(HttpClient c, long cents) =>
        Post(c, "/api/account/debits", $"{{\"amountCents\":{cents}}}");

    private static async Task<long> Balance(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/account/balance")).GetProperty("balanceCents").GetInt64();

    [Fact]
    public async Task InitialBalanceIs1000()
    {
        using var factory = CreateFactory();
        Assert.Equal(100_000, await Balance(factory.CreateClient()));
    }

    [Fact]
    public async Task ConsecutiveCreditsAndDebitsMatchCobolReference()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();
        Assert.Equal(120_000, (await Credit(client, 20_000)).Body.GetProperty("balanceCents").GetInt64());
        Assert.Equal(90_000, (await Debit(client, 30_000)).Body.GetProperty("balanceCents").GetInt64());
        Assert.Equal(90_050, (await Credit(client, 50)).Body.GetProperty("balanceCents").GetInt64());
    }

    [Fact]
    public async Task DebitToExactlyZeroSucceedsAndOverdraftIsRejectedUnchanged()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();
        var overdraft = await Debit(client, 100_001);
        Assert.Equal(HttpStatusCode.Conflict, overdraft.Status);
        Assert.Equal("insufficient_funds", overdraft.Body.GetProperty("error").GetString());
        Assert.Equal(100_000, await Balance(client));

        var exact = await Debit(client, 100_000);
        Assert.Equal(HttpStatusCode.OK, exact.Status);
        Assert.Equal(0, await Balance(client));
    }

    [Fact]
    public async Task ZeroAmountIsSuccessfulAndChangesNothing()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();
        Assert.Equal(100_000, (await Credit(client, 0)).Body.GetProperty("balanceCents").GetInt64());
        Assert.Equal(100_000, (await Debit(client, 0)).Body.GetProperty("balanceCents").GetInt64());
    }

    [Fact]
    public async Task OverflowIsRejectedAndMaximumIsAccepted()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();
        var over = await Credit(client, 99_900_000);
        Assert.Equal(HttpStatusCode.Conflict, over.Status);
        Assert.Equal("overflow", over.Body.GetProperty("error").GetString());
        Assert.Equal(100_000, await Balance(client));

        var max = await Credit(client, 99_999_999 - 100_000);
        Assert.Equal(99_999_999, max.Body.GetProperty("balanceCents").GetInt64());
        Assert.Equal("overflow", (await Credit(client, 1)).Body.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"amountCents\":-1}")]
    [InlineData("{\"amountCents\":1.5}")]
    [InlineData("{\"amountCents\":\"100\"}")]
    [InlineData("{\"amountCents\":100000000}")]
    [InlineData("{\"amountCents\":99999999999999999999}")]
    public async Task InvalidAmountsAreRejectedWithoutChange(string json)
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();
        foreach (var path in new[] { "/api/account/credits", "/api/account/debits" })
        {
            var result = await Post(client, path, json);
            Assert.Equal(HttpStatusCode.BadRequest, result.Status);
            Assert.Equal("invalid_amount", result.Body.GetProperty("error").GetString());
        }
        Assert.Equal(100_000, await Balance(client));
    }

    [Fact]
    public async Task BalanceSurvivesRealRestartAgainstSameDatabaseFile()
    {
        using (var first = CreateFactory())
        {
            await Debit(first.CreateClient(), 25_000);
        }
        SqliteConnection.ClearAllPools();

        using var second = CreateFactory();
        Assert.Equal(75_000, await Balance(second.CreateClient()));
    }

    [Fact]
    public async Task ConcurrentDebitsOf700From1000OnlyOneSucceeds()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();
        var results = await Task.WhenAll(Debit(client, 70_000), Debit(client, 70_000));

        Assert.Single(results, r => r.Status == HttpStatusCode.OK);
        Assert.Single(results, r => r.Status == HttpStatusCode.Conflict);
        Assert.Equal(30_000, await Balance(client));
    }

    [Fact]
    public async Task ManyConcurrentCreditsLoseNoUpdates()
    {
        using var factory = CreateFactory();
        var client = factory.CreateClient();
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Credit(client, 100)));
        Assert.Equal(105_000, await Balance(client));
    }
}
