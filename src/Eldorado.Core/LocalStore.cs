using Microsoft.Data.Sqlite;

namespace Eldorado.Core;

public sealed record QuoteDraft(string RequestId, decimal Price, string Currency, string DeliveryTime, string Status);

public sealed class LocalStore : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly object gate = new();

    public LocalStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS drafts(id TEXT PRIMARY KEY, price TEXT NOT NULL, currency TEXT NOT NULL, delivery TEXT NOT NULL, status TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS streams(id TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS seen(stream TEXT NOT NULL, id TEXT NOT NULL, PRIMARY KEY(stream,id));
            """;
        command.ExecuteNonQuery();
    }

    public QuoteDraft? LoadDraft(string id)
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT price,currency,delivery,status FROM drafts WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? new QuoteDraft(id, decimal.Parse(reader.GetString(0), System.Globalization.CultureInfo.InvariantCulture), reader.GetString(1), reader.GetString(2), reader.GetString(3)) : null;
        }
    }

    public string? GetStatus(string id) => LoadDraft(id)?.Status;

    public void SaveDraft(QuoteDraft draft)
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO drafts VALUES($id,$price,$currency,$delivery,$status)
                ON CONFLICT(id) DO UPDATE SET price=$price,currency=$currency,delivery=$delivery,status=$status
                """;
            command.Parameters.AddWithValue("$id", draft.RequestId);
            command.Parameters.AddWithValue("$price", draft.Price.ToString(System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$currency", draft.Currency);
            command.Parameters.AddWithValue("$delivery", draft.DeliveryTime);
            command.Parameters.AddWithValue("$status", draft.Status);
            command.ExecuteNonQuery();
        }
    }

    public void SetStatus(string id, string status)
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE drafts SET status=$status WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$status", status);
            command.ExecuteNonQuery();
        }
    }

    // Commit the baseline and observed IDs together so restarting cannot announce old rows.
    public List<string> Observe(string stream, IEnumerable<string> ids)
    {
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            using var baseline = connection.CreateCommand();
            baseline.Transaction = transaction;
            baseline.CommandText = "INSERT OR IGNORE INTO streams VALUES($stream)";
            baseline.Parameters.AddWithValue("$stream", stream);
            bool first = baseline.ExecuteNonQuery() == 1;
            var fresh = new List<string>();
            foreach (string id in ids.Distinct())
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT OR IGNORE INTO seen VALUES($stream,$id)";
                command.Parameters.AddWithValue("$stream", stream);
                command.Parameters.AddWithValue("$id", id);
                if (command.ExecuteNonQuery() == 1 && !first) fresh.Add(id);
            }
            transaction.Commit();
            return fresh;
        }
    }

    public void Dispose() => connection.Dispose();
}
