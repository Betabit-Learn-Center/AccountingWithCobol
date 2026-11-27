using Microsoft.Data.Sqlite;

namespace Accounting.Web;

public enum BookingStatus { Ok, Overflow, InsufficientFunds }

public readonly record struct BookingResult(BookingStatus Status, long BalanceCents);

/// <summary>Single account stored in SQLite. All amounts are integer hundredths.</summary>
public sealed class AccountStore
{
    public const long InitialBalanceCents = 100_000;
    public const long MaxBalanceCents = 99_999_999;

    private readonly string _connectionString;

    public AccountStore(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 30,
        }.ToString();
        Initialize();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS account (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                balance_cents INTEGER NOT NULL CHECK (balance_cents BETWEEN 0 AND {MaxBalanceCents})
            );
            INSERT OR IGNORE INTO account (id, balance_cents) VALUES (1, {InitialBalanceCents});
            """;
        command.ExecuteNonQuery();
    }

    public long GetBalance()
    {
        using var connection = Open();
        return ReadBalance(connection);
    }

    public BookingResult Credit(long amountCents) =>
        Book(amountCents, "balance_cents <= @max - @amount", "balance_cents + @amount", BookingStatus.Overflow);

    public BookingResult Debit(long amountCents) =>
        Book(amountCents, "balance_cents >= @amount", "balance_cents - @amount", BookingStatus.InsufficientFunds);

    private BookingResult Book(long amountCents, string condition, string newValue, BookingStatus failure)
    {
        using var connection = Open();
        if (amountCents == 0)
        {
            return new BookingResult(BookingStatus.Ok, ReadBalance(connection));
        }

        // One conditional statement: check and update cannot be interleaved by another writer.
        using var command = connection.CreateCommand();
        command.CommandText =
            $"UPDATE account SET balance_cents = {newValue} WHERE id = 1 AND {condition} RETURNING balance_cents;";
        command.Parameters.AddWithValue("@amount", amountCents);
        command.Parameters.AddWithValue("@max", MaxBalanceCents);
        var result = command.ExecuteScalar();
        return result is long balance
            ? new BookingResult(BookingStatus.Ok, balance)
            : new BookingResult(failure, ReadBalance(connection));
    }

    private static long ReadBalance(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT balance_cents FROM account WHERE id = 1;";
        return (long)command.ExecuteScalar()!;
    }
}
