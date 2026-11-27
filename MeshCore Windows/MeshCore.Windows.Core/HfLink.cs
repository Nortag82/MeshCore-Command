using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace MeshCore.Windows.Core;

public enum HfStatus { AwaitingApproval, Sending, Sent, Delivered, Unconfirmed, Failed, Cancelled, Received }
public sealed record HfMessage(Guid Id, string From, string To, string Text, string Requester, string RequesterCall,
    DateTimeOffset Created, DateTimeOffset Updated, bool Incoming, bool Simulated, HfStatus Status, string Detail = "",
    Guid Attempt = default, DateTimeOffset? Sent = null, DateTimeOffset? Delivered = null,
    HfMessageFormat Format = HfMessageFormat.HfLink);
public sealed record HfAudit(DateTimeOffset Time, string Actor, string Event, Guid? Message);

public sealed class HfLink
{
    private const string Operator = "local operator";
    internal const string DatabaseName = "hflink-native.sqlite3";
    internal const string ContactFormatPrefix = "contact-format:";
    private readonly string _connection;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _modem = new(1);
    private readonly Func<HfStation, HfSimulation, IHfModem> _adapter;

    public HfLink(string path, Func<HfStation, HfSimulation, IHfModem>? adapter = null, Action<HfModemStatus>? report = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connection = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        _adapter = adapter ?? ((station, simulation) => station.Simulation ? new MockHfModem(simulation, report) : new MercuryHfModem(report));
        using var db = Open();
        InitializeDatabase(db);
        foreach (var message in ReadAll<HfMessage>(db, "messages").Where(m => m.Status == HfStatus.Sending ||
            m.Status == HfStatus.Sent && m.Format == HfMessageFormat.HfLink))
            SetStatus(db, message, HfStatus.Unconfirmed, "Previous session ended before delivery was confirmed.", "system");
    }

    internal static void InitializeDatabase(SqliteConnection db) =>
        Execute(db, """
            CREATE TABLE IF NOT EXISTS settings (id TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS messages (id TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS deleted_incoming (id TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS audit (id INTEGER PRIMARY KEY, value TEXT NOT NULL);
            """);

    public HfStation Station()
    {
        lock (_gate)
        {
            using var db = Open();
            return LoadStation(db);
        }
    }

    public bool FirewallSetupOffered()
    {
        lock (_gate)
        {
            using var db = Open();
            return Read<string>(db, "settings", "firewall-setup-offered") is not null;
        }
    }

    public void MarkFirewallSetupOffered()
    {
        lock (_gate)
        {
            using var db = Open();
            Write(db, "settings", "firewall-setup-offered", "yes");
        }
    }

    public string? MercuryConfigPath()
    {
        lock (_gate)
        {
            using var db = Open();
            return Read<string>(db, "settings", "mercury-config-path");
        }
    }

    public string? SaveMercuryConfig(MercurySettingsFile config, bool rememberPath = true)
    {
        lock (_gate)
        {
            using var db = Open();
            if (_modem.CurrentCount == 0) throw new InvalidOperationException("Wait for the active modem operation to finish.");
            string? backup = config.Save();
            if (rememberPath) Write(db, "settings", "mercury-config-path", config.Path);
            Record(db, Operator, "Local Mercury configuration saved");
            return backup;
        }
    }

    public void SaveStation(HfStation station)
    {
        if (!HfStation.ValidArqBandwidth(station.ArqBandwidthHz))
            throw new ArgumentException("Choose a supported Mercury ARQ bandwidth.");
        if (!MercuryBroadcast.Modes.Any(mode => mode.Index == station.BroadcastMode))
            throw new ArgumentException("Choose a supported Mercury broadcast mode.");
        if (station.UseBundledMercury) station = station with
        {
            Host = "127.0.0.1", ControlPort = 8300, DataPort = 8301, BroadcastPort = MercuryBroadcast.DefaultPort
        };
        string host = station.Host.Trim();
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || host.Length > 253 ||
            station.ControlPort is < 1 or > 65535 || station.DataPort is < 1 or > 65535 ||
            station.BroadcastPort is < 1 or > 65535 || station.BroadcastPort == station.ControlPort || station.BroadcastPort == station.DataPort ||
            station.ControlPort == station.DataPort || station.TimeoutSeconds is < 5 or > 600)
            throw new ArgumentException("Enter a valid host, three distinct TCP ports and a timeout of 5–600 seconds.");
        station = station with { Callsign = HfStation.Call(station.Callsign), Host = host };
        lock (_gate)
        {
            using var db = Open();
            if (_modem.CurrentCount == 0) throw new InvalidOperationException("Wait for the active modem operation to finish.");
            Write(db, "settings", "station", station);
            Record(db, Operator, "Station configuration saved");
        }
    }

    public HfMessageFormat ContactFormat(string destination)
    {
        destination = HfStation.Call(destination);
        lock (_gate)
        {
            using var db = Open();
            return Read<HfMessageFormat>(db, "settings", ContactFormatPrefix + destination);
        }
    }

    public Guid Queue(string destination, string text, HfMessageFormat format = HfMessageFormat.HfLink)
    {
        bool broadcast = format == HfMessageFormat.MercuryBroadcast;
        destination = broadcast ? MercuryBroadcast.Destination : HfStation.Call(destination);
        switch (format)
        {
            case HfMessageFormat.HfLink: HfPacket.ValidateText(text); break;
            case HfMessageFormat.MercuryChat: HfPacket.ValidateChatText(text); break;
            case HfMessageFormat.MercuryBroadcast: break;
            default: throw new ArgumentOutOfRangeException(nameof(format));
        }
        lock (_gate)
        {
            using var db = Open();
            var station = LoadStation(db);
            string from = HfStation.Call(station.Callsign);
            if (broadcast) MercuryBroadcast.ValidateText(from, text, station.BroadcastMode);
            if (from == destination) throw new ArgumentException("Choose a different destination station.");
            var now = DateTimeOffset.UtcNow;
            if (ReadAll<HfMessage>(db, "messages").Take(200).Any(m => !m.Incoming && m.To == destination &&
                m.From == from && m.Simulated == station.Simulation && m.Format == format && m.Text == text &&
                m.Created > now.AddMinutes(-1) && m.Status != HfStatus.Cancelled))
                throw new InvalidOperationException("That message was just queued. Use Retry on the original if needed.");
            var message = new HfMessage(Guid.NewGuid(), from, destination, text, Operator, from,
                now, now, false, station.Simulation, HfStatus.AwaitingApproval, Format: format);
            using var transaction = db.BeginTransaction();
            Write(db, "messages", message.Id.ToString("D"), message, transaction);
            if (!broadcast) Write(db, "settings", ContactFormatPrefix + destination, format, transaction);
            Record(db, Operator, "Queued for operator approval", message.Id, transaction);
            transaction.Commit();
            return message.Id;
        }
    }

    public IReadOnlyList<HfMessage> Messages()
    {
        lock (_gate)
        {
            using var db = Open();
            return ReadAll<HfMessage>(db, "messages", 500).ToArray();
        }
    }

    public IReadOnlyList<HfAudit> Audit()
    {
        lock (_gate)
        {
            using var db = Open();
            return ReadAll<HfAudit>(db, "audit", 200).ToArray();
        }
    }

    public void Cancel(Guid id) => ChangeRequest(id, retry: false);
    public void Retry(Guid id) => ChangeRequest(id, retry: true);

    public void DeleteMessage(HfMessage message)
    {
        lock (_gate)
        {
            if (_modem.CurrentCount == 0) throw new InvalidOperationException("Wait for the active modem operation to finish.");
            string key = message.Incoming ? IncomingKey(message.Simulated, message.To, message.From, message.Id) : message.Id.ToString("D");
            using var db = Open();
            using var transaction = db.BeginTransaction();
            if (Execute(db, "DELETE FROM messages WHERE id=$id", key, transaction: transaction) == 0)
                throw new InvalidOperationException("The message is no longer in the list.");
            if (message.Incoming)
                Execute(db, "INSERT OR IGNORE INTO deleted_incoming (id) VALUES ($id)", key, transaction: transaction);
            Record(db, Operator, message.Incoming ? "Received message deleted" : "Outgoing message deleted", message.Id, transaction);
            transaction.Commit();
        }
    }

    private void ChangeRequest(Guid id, bool retry)
    {
        lock (_gate)
        {
            using var db = Open();
            var message = GetMessage(db, id);
            if (message.Incoming) throw new InvalidOperationException("Select an outgoing transmission request.");
            if (retry)
            {
                if (message.Status is not (HfStatus.Failed or HfStatus.Unconfirmed))
                    throw new InvalidOperationException("Only failed or unconfirmed messages can be retried.");
                SetStatus(db, message, HfStatus.AwaitingApproval, "", Operator);
            }
            else
            {
                if (message.Status != HfStatus.AwaitingApproval) throw new InvalidOperationException("Only unsent requests can be cancelled.");
                SetStatus(db, message, HfStatus.Cancelled, "", Operator);
            }
        }
    }

    public async Task ProbeAsync(bool allowLive, CancellationToken stop)
    {
        EnterModem();
        try
        {
            var station = Station();
            CheckMode(station, allowLive);
            using var deadline = Deadline(station, stop);
            await _adapter(station, HfSimulation.NoAcknowledgement).ProbeAsync(station, deadline.Token);
        }
        finally { _modem.Release(); }
    }

    public async Task<HfStatus> ApproveAndSendAsync(Guid id, HfSimulation simulation, bool allowLive, CancellationToken stop)
    {
        EnterModem();
        try
        {
            HfStation station;
            HfMessage message;
            lock (_gate)
            {
                using var db = Open();
                station = LoadStation(db);
                CheckMode(station, allowLive);
                message = GetMessage(db, id);
                if (message.Incoming || message.Status != HfStatus.AwaitingApproval)
                    throw new InvalidOperationException("Select a message awaiting approval.");
                if (message.Simulated != station.Simulation || message.From != station.Callsign)
                    throw new InvalidOperationException("Station or simulation mode changed. Restore it or queue a new message.");
                if (message.Format == HfMessageFormat.MercuryBroadcast)
                    MercuryBroadcast.ValidateText(message.From, message.Text, station.BroadcastMode);
                message = message with { Attempt = Guid.NewGuid(), Sent = null };
                message = SetStatus(db, message, HfStatus.Sending, "", Operator, "Approved and transmission started");
            }
            bool oneWay = message.Format != HfMessageFormat.HfLink;
            string protocol = message.Format switch
            {
                HfMessageFormat.MercuryChat => HfPacket.MercuryChat,
                HfMessageFormat.MercuryBroadcast => MercuryBroadcast.Protocol,
                _ => HfPacket.Version
            };
            var packet = new HfPacket(protocol,
                "message", id, message.Attempt, message.From, message.To, message.Text);
            using var deadline = Deadline(station, stop);
            bool handedToModem = false;
            HfStatus status;
            string detail;
            try
            {
                var response = await _adapter(station, simulation).SendAsync(station, packet, () =>
                {
                    handedToModem = true;
                    lock (_gate)
                    {
                        using var db = Open();
                        message = SetStatus(db, message with { Sent = DateTimeOffset.UtcNow },
                            oneWay ? HfStatus.Sending : HfStatus.Sent, "Written to local modem.", Operator);
                    }
                }, deadline.Token);
                bool delivered = response?.Confirms(packet) == true && handedToModem;
                if (oneWay && handedToModem)
                {
                    status = HfStatus.Sent;
                    detail = message.Format == HfMessageFormat.MercuryBroadcast
                        ? "Broadcast submitted to the local modem. Broadcast has no delivery receipt."
                        : "Mercury Chat transfer finished. Ask the other station to reply to confirm receipt.";
                }
                else
                {
                    status = delivered ? HfStatus.Delivered : HfStatus.Unconfirmed;
                    detail = delivered ? "Matching HFLink receipt received from the destination station." : "No matching destination receipt.";
                }
            }
            catch (Exception error) when (error is TimeoutException or OperationCanceledException or IOException or System.Net.Sockets.SocketException or InvalidDataException)
            {
                status = handedToModem ? HfStatus.Unconfirmed : HfStatus.Failed;
                detail = error is TimeoutException or OperationCanceledException
                    ? (stop.IsCancellationRequested ? "Modem operation stopped." : "Mercury session timed out.") : error.Message;
            }
            lock (_gate)
            {
                using var db = Open();
                if (status == HfStatus.Delivered) message = message with { Delivered = DateTimeOffset.UtcNow };
                SetStatus(db, message, status, detail, Operator);
            }
            return status;
        }
        finally { _modem.Release(); }
    }

    public async Task ReceiveAsync(bool allowLive,
        Func<HfMessage, CancellationToken, Task<bool>> approveReceipt, CancellationToken stop,
        Action<HfMessage>? onReceived = null, bool broadcast = false)
    {
        EnterModem();
        try
        {
            HfStation station;
            lock (_gate)
            {
                using var db = Open();
                station = LoadStation(db);
                CheckMode(station, allowLive);
                Record(db, Operator, broadcast ? "Broadcast receive window opened" : "Attended receive window opened");
            }
            using var deadline = Deadline(station, stop);
            var approved = new HashSet<Guid>();
            try { await _adapter(station, HfSimulation.NoAcknowledgement).ReceiveAsync(station, AcceptPacketAsync, deadline.Token, broadcast); }
            catch (OperationCanceledException) when (broadcast && deadline.IsCancellationRequested && !stop.IsCancellationRequested) { }

            async Task<HfPacket?> AcceptPacketAsync(HfPacket packet, CancellationToken token)
            {
                if (packet.Type != "message") return null;
                HfMessageFormat format;
                if (broadcast)
                {
                    if (packet.Protocol != MercuryBroadcast.Protocol || packet.To != MercuryBroadcast.Destination || packet.From == station.Callsign) return null;
                    format = HfMessageFormat.MercuryBroadcast;
                }
                else
                {
                    if (packet.Protocol is not (HfPacket.Version or HfPacket.MercuryChat) || packet.To != station.Callsign) return null;
                    format = packet.Protocol == HfPacket.MercuryChat ? HfMessageFormat.MercuryChat : HfMessageFormat.HfLink;
                    if (format == HfMessageFormat.MercuryChat) HfPacket.ValidateChatText(packet.Text);
                    else HfPacket.ValidateText(packet.Text);
                }
                HfMessage message;
                bool isNew;
                string key = IncomingKey(station.Simulation, packet.To, packet.From, packet.Id);
                lock (_gate)
                {
                    using var db = Open();
                    using var deleted = db.CreateCommand();
                    deleted.CommandText = "SELECT 1 FROM deleted_incoming WHERE id=$id";
                    deleted.Parameters.AddWithValue("$id", key);
                    if (deleted.ExecuteScalar() is not null) return null;
                    var existing = Read<HfMessage>(db, "messages", key);
                    if (existing is not null && (existing.Text != packet.Text || existing.Attempt == packet.Attempt)) return null;
                    isNew = existing is null;
                    var now = DateTimeOffset.UtcNow;
                    message = existing is null
                        ? new(packet.Id, packet.From, packet.To, packet.Text!, "", "", now, now, true, station.Simulation, HfStatus.Received,
                            Attempt: packet.Attempt, Format: format)
                        : existing with { Attempt = packet.Attempt };
                    Write(db, "messages", key, message);
                    if (existing is null) Record(db, packet.From, "Incoming message stored", packet.Id);
                }
                if (isNew) onReceived?.Invoke(message);
                if (format != HfMessageFormat.HfLink || !approved.Add(packet.Attempt) || !await approveReceipt(message, token)) return null;
                token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    using var db = Open();
                    Record(db, Operator, "Receipt transmission authorized", packet.Id);
                }
                return packet.Acknowledge();
            }
        }
        finally { _modem.Release(); }
    }

    private static CancellationTokenSource Deadline(HfStation station, CancellationToken stop)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(stop);
        source.CancelAfter(TimeSpan.FromSeconds(station.TimeoutSeconds));
        return source;
    }

    private static void CheckMode(HfStation station, bool allowLive)
    {
        HfStation.Call(station.Callsign);
        if (!station.Simulation && !allowLive) throw new InvalidOperationException("This window allows simulation only.");
    }

    private void EnterModem()
    {
        if (!_modem.Wait(0)) throw new InvalidOperationException("A modem operation is already in progress.");
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connection);
        db.Open();
        return db;
    }

    private static HfStation LoadStation(SqliteConnection db) => Read<HfStation>(db, "settings", "station") ?? new();
    internal static string IncomingKey(bool simulation, string to, string from, Guid id) => $"in:{simulation}:{to}:{from}:{id:D}";
    private static HfMessage GetMessage(SqliteConnection db, Guid id) =>
        Read<HfMessage>(db, "messages", id.ToString("D")) ?? throw new ArgumentException("Outgoing message not found.");

    private static HfMessage SetStatus(SqliteConnection db, HfMessage message, HfStatus status, string detail, string actor, string? audit = null)
    {
        message = message with { Status = status, Updated = DateTimeOffset.UtcNow, Detail = detail };
        SaveMessage(db, message, actor, audit ?? status.ToString());
        return message;
    }

    private static void SaveMessage(SqliteConnection db, HfMessage message, string actor, string audit)
    {
        using var transaction = db.BeginTransaction();
        Write(db, "messages", message.Id.ToString("D"), message, transaction);
        Record(db, actor, audit, message.Id, transaction);
        transaction.Commit();
    }

    private static void Record(SqliteConnection db, string actor, string audit, Guid? message = null, SqliteTransaction? transaction = null) =>
        Execute(db, "INSERT INTO audit(value) VALUES($value)", value: JsonSerializer.Serialize(new HfAudit(DateTimeOffset.UtcNow, actor, audit, message)), transaction: transaction);

    private static void Write<T>(SqliteConnection db, string table, string id, T value, SqliteTransaction? transaction = null) =>
        Execute(db, $"INSERT INTO {table}(id,value) VALUES($id,$value) ON CONFLICT(id) DO UPDATE SET value=excluded.value",
            id, JsonSerializer.Serialize(value), transaction);

    private static T? Read<T>(SqliteConnection db, string table, string id)
    {
        using var command = db.CreateCommand();
        command.CommandText = $"SELECT value FROM {table} WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json) : default;
    }

    private static IEnumerable<T> ReadAll<T>(SqliteConnection db, string table, int limit = -1)
    {
        using var command = db.CreateCommand();
        command.CommandText = $"SELECT value FROM {table} ORDER BY rowid DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return JsonSerializer.Deserialize<T>(reader.GetString(0))!;
    }

    private static int Execute(SqliteConnection db, string sql, string? id = null, string? value = null, SqliteTransaction? transaction = null)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (id is not null) command.Parameters.AddWithValue("$id", id);
        if (value is not null) command.Parameters.AddWithValue("$value", value);
        return command.ExecuteNonQuery();
    }
}
