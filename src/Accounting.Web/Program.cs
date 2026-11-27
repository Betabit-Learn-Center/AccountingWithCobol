using System.Text.Json;
using Accounting.Web;

var builder = WebApplication.CreateBuilder(args);
var databasePath = builder.Configuration["Database:Path"] ?? "accounting.db";
builder.Services.AddSingleton(new AccountStore(databasePath));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/account/balance", (AccountStore store) => Balance(store.GetBalance()));

app.MapPost("/api/account/credits", async (HttpRequest request, AccountStore store) =>
    await Book(request, store.Credit));

app.MapPost("/api/account/debits", async (HttpRequest request, AccountStore store) =>
    await Book(request, store.Debit));

app.Run();

static IResult Balance(long cents) => Results.Ok(new { balanceCents = cents });

static IResult Error(int status, string code, string message, long? balance = null) =>
    Results.Json(new { error = code, message, balanceCents = balance }, statusCode: status);

static async Task<IResult> Book(HttpRequest request, Func<long, BookingResult> operation)
{
    var amount = await ReadAmountAsync(request);
    if (amount is null)
    {
        return Error(400, "invalid_amount",
            "amountCents must be a non-negative integer number of hundredths up to 99999999.");
    }

    var result = operation(amount.Value);
    return result.Status switch
    {
        BookingStatus.Ok => Balance(result.BalanceCents),
        BookingStatus.Overflow => Error(409, "overflow",
            "The balance would exceed the maximum of 999999.99.", result.BalanceCents),
        _ => Error(409, "insufficient_funds", "Insufficient funds for this debit.", result.BalanceCents),
    };
}

static async Task<long?> ReadAmountAsync(HttpRequest request)
{
    try
    {
        using var document = await JsonDocument.ParseAsync(request.Body);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("amountCents", out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt64(out var value)
            || value < 0
            || value > AccountStore.MaxBalanceCents)
        {
            return null;
        }
        return value;
    }
    catch (JsonException)
    {
        return null;
    }
}

public partial class Program;
