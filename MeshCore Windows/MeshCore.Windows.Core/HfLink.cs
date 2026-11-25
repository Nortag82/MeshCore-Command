using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeshCore.Windows.Core;

public enum HfRole { User, Operator, Administrator }
public enum HfStatus { AwaitingApproval, Sending, Sent, Delivered, Unconfirmed, Failed, Cancelled, Received }
public sealed record HfUser(string Name, HfRole Role, string Callsign, bool Access, bool MayRequest, string LicenceCheck);
public sealed record HfMessage(Guid Id, string From, string To, string Text, string Requester, string RequesterCall,
    DateTimeOffset Created, DateTimeOffset Updated, bool Incoming, bool Simulated, HfStatus Status, string Detail = "",
    Guid Attempt = default, DateTimeOffset? Sent = null, DateTimeOffset? Delivered = null);
public sealed record HfAudit(DateTimeOffset Time, string Actor, string Event, Guid? Message);

public sealed class HfLink
{
    private sealed record Account(HfUser User, byte[] Salt, byte[] PasswordHash);
    private readonly string _connection;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Account> _sessions = [];
    private readonly SemaphoreSlim _modem = new(1);
    private readonly Func<HfStation, HfSimulation, IHfModem> _adapter;

    public HfLink(string path, Func<HfStation, HfSimulation, IHfModem>? adapter = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connection = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        _adapter = adapter ?? ((station, simulation) => station.Simulation ? new MockHfModem(simulation) : new MercuryHfModem());
        using var db = Open();
        Execute(db, """
            CREATE TABLE IF NOT EXISTS accounts (id TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS settings (id TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS messages (id TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS audit (id INTEGER PRIMARY KEY, value TEXT NOT NULL);
            """);
        foreach (var message in ReadAll<HfMessage>(db, "messages").Where(m => m.Status is HfStatus.Sending or HfStatus.Sent))
            SetStatus(db, message, HfStatus.Unconfirmed, "Previous session ended before delivery was confirmed.", "system");
    }

    public bool NeedsAdministrator
    {
        get { lock (_gate) { using var db = Open(); return Read<Account>(db, "accounts", "admin") is null; } }
    }

    public Guid SetupAdministrator(string password)
    {
        lock (_gate)
        {
            using var db = Open();
            if (Read<Account>(db, "accounts", "admin") is not null) throw new InvalidOperationException("An administrator already exists.");
            var account = MakeAccount(new("admin", HfRole.Administrator, "", false, false, ""), password);
            Write(db, "accounts", "admin", account);
            Record(db, "admin", "Administrator initialized");
            return Issue(account);
        }
    }

    public Guid SignIn(string name, string password)
    {
        lock (_gate)
        {
            using var db = Open();
            var account = Read<Account>(db, "accounts", name.Trim().ToLowerInvariant());
            if (account is null || password.Length > 256 ||
                !CryptographicOperations.FixedTimeEquals(account.PasswordHash, Hash(password, account.Salt)))
                throw new UnauthorizedAccessException("Incorrect HFLink username or password.");
            return Issue(account);
        }
    }

    public void SignOut(Guid session) { lock (_gate) _sessions.Remove(session); }

    public HfUser CurrentUser(Guid session)
    {
        lock (_gate) { using var db = Open(); return Require(db, session).User; }
    }

    public IReadOnlyList<HfUser> Users(Guid session)
    {
        lock (_gate)
        {
            using var db = Open();
            RequireAdmin(db, session);
            return ReadAll<Account>(db, "accounts").Select(account => account.User).OrderBy(user => user.Name).ToArray();
        }
    }

    public void SaveUser(Guid session, HfUser user, string password)
    {
        string name = user.Name.Trim().ToLowerInvariant();
        if (!Regex.IsMatch(name, @"\A[a-z0-9][a-z0-9_.-]{0,31}\z") || name == "admin" ||
            user.Role is not (HfRole.User or HfRole.Operator) || user.LicenceCheck.Length > 240)
            throw new ArgumentException("Use a username of 1–32 letters, digits, dots, underscores or hyphens and select a valid role.");
        string call = string.IsNullOrWhiteSpace(user.Callsign) && !user.Access ? "" : HfStation.Call(user.Callsign);
        user = user with { Name = name, Callsign = call };
        lock (_gate)
        {
            using var db = Open();
            RequireAdmin(db, session);
            var old = Read<Account>(db, "accounts", name);
            var account = password.Length == 0 && old is not null ? old with { User = user } : MakeAccount(user, password);
            Write(db, "accounts", name, account);
            Record(db, "admin", $"Permissions saved for {name}");
        }
    }

    public HfStation Station(Guid session)
    {
        lock (_gate)
        {
            using var db = Open();
            RequireAccess(db, session);
            return LoadStation(db);
        }
    }

    public string? MercuryConfigPath(Guid session)
    {
        lock (_gate)
        {
            using var db = Open();
            RequireAdmin(db, session);
            return Read<string>(db, "settings", "mercury-config-path");
        }
    }

    public string? SaveMercuryConfig(Guid session, MercurySettingsFile config)
    {
        lock (_gate)
        {
            using var db = Open();
            RequireAdmin(db, session);
            if (_modem.CurrentCount == 0) throw new InvalidOperationException("Wait for the active modem operation to finish.");
            string? backup = config.Save();
            Write(db, "settings", "mercury-config-path", config.Path);
            Record(db, "admin", "Local Mercury configuration saved");
            return backup;
        }
    }

    public void SaveStation(Guid session, HfStation station)
    {
        string host = station.Host.Trim();
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || host.Length > 253 ||
            station.ControlPort is < 1 or > 65535 || station.DataPort is < 1 or > 65535 ||
            station.ControlPort == station.DataPort || station.TimeoutSeconds is < 5 or > 600)
            throw new ArgumentException("Enter a valid host, two distinct TCP ports and a timeout of 5–600 seconds.");
        station = station with { Callsign = HfStation.Call(station.Callsign), Host = host };
        lock (_gate)
        {
            using var db = Open();
            RequireAdmin(db, session);
            if (_modem.CurrentCount == 0) throw new InvalidOperationException("Wait for the active modem operation to finish.");
            Write(db, "settings", "station", station);
            Record(db, "admin", "Station configuration saved");
        }
    }

    public Guid Queue(Guid session, string destination, string text)
    {
        destination = HfStation.Call(destination);
        HfPacket.ValidateText(text);
        lock (_gate)
        {
            using var db = Open();
            var user = RequireRequester(db, session);
            var station = LoadStation(db);
            string from = HfStation.Call(station.Callsign);
            if (from == destination) throw new ArgumentException("Choose a different destination station.");
            var now = DateTimeOffset.UtcNow;
            if (ReadAll<HfMessage>(db, "messages").Take(200).Any(m => !m.Incoming && m.Requester == user.Name && m.To == destination &&
                m.From == from && m.Simulated == station.Simulation && m.Text == text && m.Created > now.AddMinutes(-1) && m.Status != HfStatus.Cancelled))
                throw new InvalidOperationException("That message was just queued. Use Retry on the original if needed.");
            var message = new HfMessage(Guid.NewGuid(), from, destination, text, user.Name, user.Callsign,
                now, now, false, station.Simulation, HfStatus.AwaitingApproval);
            SaveMessage(db, message, user.Name, "Queued for operator approval");
            return message.Id;
        }
    }

    public IReadOnlyList<HfMessage> Messages(Guid session)
    {
        lock (_gate)
        {
            using var db = Open();
            var user = RequireAccess(db, session);
            return ReadAll<HfMessage>(db, "messages").Where(m => Visible(user, m)).Take(500).ToArray();
        }
    }

    public IReadOnlyList<HfAudit> Audit(Guid session)
    {
        lock (_gate)
        {
            using var db = Open();
            var user = RequireAccess(db, session);
            if (user.Role == HfRole.User) throw new UnauthorizedAccessException("Operator or administrator access is required.");
            return ReadAll<HfAudit>(db, "audit", 200).ToArray();
        }
    }

    public void Cancel(Guid session, Guid id) => ChangeRequest(session, id, retry: false);
    public void Retry(Guid session, Guid id) => ChangeRequest(session, id, retry: true);

    private void ChangeRequest(Guid session, Guid id, bool retry)
    {
        lock (_gate)
        {
            using var db = Open();
            var user = RequireAccess(db, session);
            var message = GetMessage(db, id);
            if (!Visible(user, message) || message.Incoming) throw new UnauthorizedAccessException("This is not your transmission request.");
            if (retry)
            {
                CheckRequester(db, message);
                if (message.Status is not (HfStatus.Failed or HfStatus.Unconfirmed))
                    throw new InvalidOperationException("Only failed or unconfirmed messages can be retried.");
                SetStatus(db, message, HfStatus.AwaitingApproval, "", user.Name);
            }
            else
            {
                if (message.Status != HfStatus.AwaitingApproval) throw new InvalidOperationException("Only unsent requests can be cancelled.");
                SetStatus(db, message, HfStatus.Cancelled, "", user.Name);
            }
        }
    }

    public async Task ProbeAsync(Guid session, bool allowLive, CancellationToken stop)
    {
        EnterModem();
        try
        {
            var station = Station(session);
            CheckMode(station, allowLive);
            using var deadline = Deadline(station, stop);
            await _adapter(station, HfSimulation.NoAcknowledgement).ProbeAsync(station, deadline.Token);
        }
        finally { _modem.Release(); }
    }

    public async Task<HfStatus> ApproveAndSendAsync(Guid session, Guid id, HfSimulation simulation, bool allowLive, CancellationToken stop)
    {
        EnterModem();
        try
        {
            HfStation station;
            HfMessage message;
            string actor;
            lock (_gate)
            {
                using var db = Open();
                actor = RequireOperator(db, session).Name;
                station = LoadStation(db);
                CheckMode(station, allowLive);
                message = GetMessage(db, id);
                if (message.Incoming || message.Status != HfStatus.AwaitingApproval)
                    throw new InvalidOperationException("Select a message awaiting approval.");
                if (message.Simulated != station.Simulation || message.From != station.Callsign)
                    throw new InvalidOperationException("Station or simulation mode changed. Restore it or queue a new message.");
                CheckRequester(db, message);
                message = message with { Attempt = Guid.NewGuid(), Sent = null };
                message = SetStatus(db, message, HfStatus.Sending, "", actor, "Approved and transmission started");
            }
            var packet = new HfPacket(HfPacket.Version, "message", id, message.Attempt, message.From, message.To, message.Text);
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
                        message = SetStatus(db, message with { Sent = DateTimeOffset.UtcNow }, HfStatus.Sent, "Written to local modem.", actor);
                    }
                }, deadline.Token);
                bool delivered = response?.Confirms(packet) == true && handedToModem;
                status = delivered ? HfStatus.Delivered : HfStatus.Unconfirmed;
                detail = delivered ? "Matching HFLink receipt received from the destination station." : "No matching destination receipt.";
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
                SetStatus(db, message, status, detail, actor);
            }
            return status;
        }
        finally { _modem.Release(); }
    }

    public async Task ReceiveAsync(Guid session, bool allowLive,
        Func<HfMessage, CancellationToken, Task<bool>> approveReceipt, CancellationToken stop)
    {
        EnterModem();
        try
        {
            HfStation station;
            string actor;
            lock (_gate)
            {
                using var db = Open();
                actor = RequireOperator(db, session).Name;
                station = LoadStation(db);
                CheckMode(station, allowLive);
                Record(db, actor, "Attended receive window opened");
            }
            using var deadline = Deadline(station, stop);
            var approved = new HashSet<Guid>();
            await _adapter(station, HfSimulation.NoAcknowledgement).ReceiveAsync(station, async (packet, token) =>
            {
                if (packet.Protocol != HfPacket.Version || packet.Type != "message" || packet.To != station.Callsign) return null;
                HfPacket.ValidateText(packet.Text);
                HfMessage message;
                string key = $"in:{station.Simulation}:{packet.To}:{packet.From}:{packet.Id:D}";
                lock (_gate)
                {
                    using var db = Open();
                    var existing = Read<HfMessage>(db, "messages", key);
                    if (existing is not null && (existing.Text != packet.Text || existing.Attempt == packet.Attempt)) return null;
                    var now = DateTimeOffset.UtcNow;
                    message = existing is null
                        ? new(packet.Id, packet.From, packet.To, packet.Text!, "", "", now, now, true, station.Simulation, HfStatus.Received, Attempt: packet.Attempt)
                        : existing with { Attempt = packet.Attempt };
                    Write(db, "messages", key, message);
                    if (existing is null) Record(db, packet.From, "Incoming message stored", packet.Id);
                }
                if (!approved.Add(packet.Attempt) || !await approveReceipt(message, token)) return null;
                token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    using var db = Open();
                    RequireOperator(db, session);
                    Record(db, actor, "Receipt transmission authorized", packet.Id);
                }
                return packet.Acknowledge();
            }, deadline.Token);
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

    private static bool Visible(HfUser user, HfMessage message) =>
        user.Role != HfRole.User || (message.Incoming ? message.To == user.Callsign : message.Requester == user.Name);

    private Guid Issue(Account account)
    {
        var token = Guid.NewGuid();
        _sessions.Add(token, account);
        return token;
    }

    private Account Require(SqliteConnection db, Guid session)
    {
        if (_sessions.TryGetValue(session, out var original) &&
            Read<Account>(db, "accounts", original.User.Name) is { } current &&
            CryptographicOperations.FixedTimeEquals(original.PasswordHash, current.PasswordHash)) return current;
        throw new UnauthorizedAccessException("Sign in to HFLink.");
    }

    private HfUser RequireAccess(SqliteConnection db, Guid session)
    {
        var user = Require(db, session).User;
        if (!user.Access && user.Role != HfRole.Administrator) throw new UnauthorizedAccessException("HFLink access is revoked or pending.");
        return user;
    }

    private void RequireAdmin(SqliteConnection db, Guid session)
    {
        if (Require(db, session).User.Role != HfRole.Administrator) throw new UnauthorizedAccessException("Administrator access is required.");
    }

    private HfUser RequireOperator(SqliteConnection db, Guid session)
    {
        var user = RequireAccess(db, session);
        if (user.Role != HfRole.Operator) throw new UnauthorizedAccessException("Sign in as a designated operator to approve transmissions.");
        return user;
    }

    private HfUser RequireRequester(SqliteConnection db, Guid session)
    {
        var user = RequireAccess(db, session);
        if (!user.Access || !user.MayRequest) throw new UnauthorizedAccessException("Transmit-request permission is required.");
        return user;
    }

    private static void CheckRequester(SqliteConnection db, HfMessage message)
    {
        var requester = Read<Account>(db, "accounts", message.Requester)?.User;
        if (requester is null || !requester.Access || !requester.MayRequest || requester.Callsign != message.RequesterCall)
            throw new UnauthorizedAccessException("The requester’s current permissions or callsign do not allow this transmission.");
    }

    private static byte[] Hash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, HashAlgorithmName.SHA256, 32);

    private static Account MakeAccount(HfUser user, string password)
    {
        if (password.Length is < 12 or > 256) throw new ArgumentException("Use a password of 12–256 characters.");
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        return new(user, salt, Hash(password, salt));
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connection);
        db.Open();
        return db;
    }

    private static HfStation LoadStation(SqliteConnection db) => Read<HfStation>(db, "settings", "station") ?? new();
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

    private static void Execute(SqliteConnection db, string sql, string? id = null, string? value = null, SqliteTransaction? transaction = null)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (id is not null) command.Parameters.AddWithValue("$id", id);
        if (value is not null) command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }
}
