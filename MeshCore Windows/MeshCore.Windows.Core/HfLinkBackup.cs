using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MeshCore.Windows.Core;

internal static class HfLinkBackup
{
    internal const string History = "hflink/history.json";
    internal const string Mercury = "hflink/mercury.ini";
    internal const string ExternalMercury = "hflink/external-mercury.ini";
    internal const int MaxIniBytes = 1024 * 1024;
    internal static string BundledConfig(string root) => Path.Combine(root, "mercury", "mercury.ini");
    internal static string ExternalConfig(string root) => Path.Combine(root, "mercury", "external-mercury.ini");

    private sealed record Snapshot(HfStation? Station, HfMessage[] Messages, HfAudit[] Audit, string[] DeletedIncoming,
        Dictionary<string, HfMessageFormat>? ContactFormats = null, HfHeardStation[]? HeardStations = null,
        Js8LinkSettings? Js8Settings = null);

    internal static void Capture(string root, Dictionary<string, byte[]> files)
    {
        AddIni(BundledConfig(root), Mercury);
        string path = Path.Combine(root, HfLink.DatabaseName);
        if (!File.Exists(path)) return;
        using var db = Open(path, SqliteOpenMode.ReadOnly);
        using var transaction = db.BeginTransaction(deferred: true);
        var settings = ReadColumn(db, transaction, $"SELECT id, value FROM settings WHERE id IN ('station', 'mercury-config-path', '{HfLink.Js8SettingsKey}')");
        var tables = ReadColumn(db, transaction, "SELECT name FROM sqlite_schema WHERE type = 'table' AND name IN ('deleted_incoming', 'heard_stations')");
        HfStation? station = settings.GetValueOrDefault("station") is { } saved ? JsonSerializer.Deserialize<HfStation>(saved) : null;
        var snapshot = new Snapshot(station,
            ReadJson<HfMessage>("SELECT value FROM messages ORDER BY rowid"),
            ReadJson<HfAudit>("SELECT value FROM audit ORDER BY id"),
            tables.ContainsKey("deleted_incoming")
                ? ReadColumn(db, transaction, "SELECT id FROM deleted_incoming").Keys.ToArray() : [],
            ReadColumn(db, transaction, $"SELECT id, value FROM settings WHERE id GLOB '{HfLink.ContactFormatPrefix}*'")
                .ToDictionary(entry => entry.Key[HfLink.ContactFormatPrefix.Length..],
                    entry => JsonSerializer.Deserialize<HfMessageFormat>(entry.Value!)),
            tables.ContainsKey("heard_stations") ? HfLink.ReadHeardStations(db, transaction) : null,
            settings.GetValueOrDefault(HfLink.Js8SettingsKey) is { } js8 ? JsonSerializer.Deserialize<Js8LinkSettings>(js8) : null);
        files.Add(History, JsonSerializer.SerializeToUtf8Bytes(snapshot));
        if (settings.GetValueOrDefault("mercury-config-path") is { } external &&
            JsonSerializer.Deserialize<string>(external) is { } config)
            AddIni(config, ExternalMercury);

        T[] ReadJson<T>(string sql)
        {
            using var query = db.CreateCommand();
            query.Transaction = transaction;
            query.CommandText = sql;
            using var reader = query.ExecuteReader();
            var records = new List<T>();
            while (reader.Read())
                records.Add(JsonSerializer.Deserialize<T>(reader.GetString(0)) ?? throw new InvalidDataException("An HFLink backup record is invalid."));
            return records.ToArray();
        }
        void AddIni(string ini, string name)
        {
            if (!File.Exists(ini)) return;
            if (new FileInfo(ini).Length > MaxIniBytes) throw new InvalidDataException("Mercury configuration exceeds the backup size limit.");
            byte[] bytes = File.ReadAllBytes(ini);
            ValidateIni(bytes);
            files.Add(name, bytes);
        }
    }

    private static Dictionary<string, string?> ReadColumn(SqliteConnection db, SqliteTransaction transaction, string sql)
    {
        using var query = db.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = sql;
        using var reader = query.ExecuteReader();
        var records = new Dictionary<string, string?>();
        while (reader.Read())
            records.Add(reader.GetString(0), reader.FieldCount == 1 ? null : reader.GetString(1));
        return records;
    }

    internal static void ValidateIni(byte[] bytes)
    {
        if (bytes.Length > MaxIniBytes || new UTF8Encoding(false, true).GetString(bytes).Contains('\0'))
            throw new InvalidDataException("Invalid Mercury configuration in the backup.");
    }

    internal static void ValidateHistory(byte[] bytes) => _ = Parse(bytes);

    private static Snapshot Parse(byte[] bytes)
    {
        var snapshot = JsonSerializer.Deserialize<Snapshot>(bytes) ?? throw new InvalidDataException("HFLink history is missing.");
        try { _ = snapshot.Js8Settings?.Validate(); }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid JS8Call settings in the backup.", error); }
        if (snapshot.Messages is null || snapshot.Audit is null || snapshot.DeletedIncoming is null ||
            (snapshot.Station is { } station && !HfStation.ValidArqBandwidth(station.ArqBandwidthHz)) ||
            snapshot.Messages.Any(message => message is null || message.Id == Guid.Empty || message.Text is null ||
                message.From is null || message.To is null || !Enum.IsDefined(message.Status) || !Enum.IsDefined(message.Format)) ||
            snapshot.ContactFormats?.Any(entry => !Enum.IsDefined(entry.Value) || !CanonicalCall(entry.Key)) == true ||
            snapshot.HeardStations?.Any(contact => contact is null || contact.Callsign is null ||
                !CanonicalCall(contact.Callsign) || !Enum.IsDefined(contact.LastFormat)) == true ||
            snapshot.Audit.Any(item => item is null || item.Actor is null || item.Event is null) ||
            snapshot.DeletedIncoming.Any(id => id is null || id.Length is < 1 or > 100 || !id.StartsWith("in:", StringComparison.Ordinal)))
            throw new InvalidDataException("Invalid HFLink backup records.");
        return snapshot;
    }

    private static bool CanonicalCall(string value)
    {
        try { return value == HfStation.Call(value); }
        catch (ArgumentException) { return false; }
    }

    internal static byte[] RestoreDatabase(string root, byte[] bytes, string? externalConfig)
    {
        var snapshot = Parse(bytes);
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, $".hflink-restore-{Guid.NewGuid():N}.sqlite3");
        try
        {
            using (var db = Open(path, SqliteOpenMode.ReadWriteCreate))
            {
                HfLink.InitializeDatabase(db);
                using var transaction = db.BeginTransaction();
                if (snapshot.Station is not null) Write("INSERT INTO settings (id,value) VALUES ($id,$value)", "station", JsonSerializer.Serialize(snapshot.Station));
                if (snapshot.Js8Settings is not null)
                    Write("INSERT INTO settings (id,value) VALUES ($id,$value)", HfLink.Js8SettingsKey, JsonSerializer.Serialize(snapshot.Js8Settings.Validate()));
                if (externalConfig is not null) Write("INSERT INTO settings (id,value) VALUES ($id,$value)", "mercury-config-path", JsonSerializer.Serialize(externalConfig));
                if (snapshot.ContactFormats is not null)
                    foreach (var entry in snapshot.ContactFormats)
                        Write("INSERT INTO settings (id,value) VALUES ($id,$value)", HfLink.ContactFormatPrefix + entry.Key, JsonSerializer.Serialize(entry.Value));
                if (snapshot.HeardStations is not null)
                    foreach (var contact in snapshot.HeardStations)
                        HfLink.RememberStation(db, contact, transaction);
                foreach (var message in snapshot.Messages)
                {
                    string id = message.Incoming ? HfLink.IncomingKey(message.Simulated, message.To, message.From, message.Id) : message.Id.ToString("D");
                    Write("INSERT INTO messages (id,value) VALUES ($id,$value)", id, JsonSerializer.Serialize(message));
                }
                foreach (var item in snapshot.Audit)
                    Write("INSERT INTO audit (value) VALUES ($value)", null, JsonSerializer.Serialize(item));
                foreach (string id in snapshot.DeletedIncoming)
                    Write("INSERT INTO deleted_incoming (id) VALUES ($id)", id, null);
                transaction.Commit();

                void Write(string sql, string? id, string? value)
                {
                    using var command = db.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    if (id is not null) command.Parameters.AddWithValue("$id", id);
                    if (value is not null) command.Parameters.AddWithValue("$value", value);
                    command.ExecuteNonQuery();
                }
            }
            return File.ReadAllBytes(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
