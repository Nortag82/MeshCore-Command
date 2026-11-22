using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MeshCore.Windows.Core;

public sealed record BoardProfile(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("name")] string Name);

/// <summary>Reads and writes the existing version-one board profile format.</summary>
public sealed class BoardProfiles
{
    private sealed record Document(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("boards")] List<BoardProfile> Boards);
    private readonly string _path;
    private readonly object _gate = new();
    private readonly bool _allowSerial;
    private List<BoardProfile> _boards;

    public BoardProfiles(string path, bool allowSerial = false)
    {
        _path = Path.GetFullPath(path);
        _allowSerial = allowSerial;
        if (!File.Exists(_path)) { _boards = new(); return; }
        _boards = Parse(File.ReadAllText(_path), _allowSerial);
    }

    public static void ValidateSerialized(string json, bool allowSerial = false) => _ = Parse(json, allowSerial);

    private static List<BoardProfile> Parse(string json, bool allowSerial)
    {
        var doc = JsonSerializer.Deserialize<Document>(json)
                  ?? throw new InvalidDataException("Invalid board profile file.");
        if (doc.Version != 1 || doc.Boards is null) throw new InvalidDataException("Unsupported board profile file.");
        var ids = new HashSet<string>();
        var addresses = new HashSet<(string, string)>();
        foreach (var board in doc.Boards)
        {
            Validate(board, allowSerial);
            if (!ids.Add(board.Id) || !addresses.Add((board.Kind, board.Address)))
                throw new InvalidDataException("Duplicate board profile.");
        }
        return doc.Boards;
    }

    public IReadOnlyList<BoardProfile> List() { lock (_gate) return _boards.ToArray(); }

    public BoardProfile Add(string kind, string address, string name)
    {
        address = address.Trim();
        if (kind is "ble" or "serial") address = address.ToUpperInvariant();
        var board = new BoardProfile(Guid.NewGuid().ToString("N"), kind, address, name.Trim());
        Validate(board, _allowSerial);
        lock (_gate)
        {
            if (_boards.Any(p => p.Kind == kind && p.Address == address)) throw new InvalidOperationException("This connection is already saved.");
            var next = _boards.Append(board).ToList();
            Save(next); _boards = next;
        }
        return board;
    }

    public void Rename(string id, string name)
    {
        lock (_gate)
        {
            int index = _boards.FindIndex(p => p.Id == id);
            if (index < 0) throw new KeyNotFoundException("Board not found.");
            var next = _boards.ToList();
            next[index] = next[index] with { Name = name.Trim() };
            Validate(next[index], _allowSerial);
            Save(next); _boards = next;
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            var next = _boards.Where(board => board.Id != id).ToList();
            if (next.Count == _boards.Count) throw new KeyNotFoundException("Board not found.");
            Save(next); _boards = next;
        }
    }

    private void Save(List<BoardProfile> boards)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Document(1, boards), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(BoardProfile board, bool allowSerial)
    {
        if (board is null || board.Id is null || !Regex.IsMatch(board.Id, "^[0-9a-f]{32}$")) throw new InvalidDataException("Invalid board ID.");
        if (board.Address is null || board.Address.Length is < 1 or > 256 || board.Address.Trim() != board.Address) throw new InvalidDataException("Invalid board address.");
        if (board.Kind == "wifi")
        {
            if (!IPAddress.TryParse(board.Address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || ip.ToString() != board.Address)
                throw new InvalidDataException("Invalid IPv4 address.");
        }
        else if (board.Kind == "ble")
        {
            if (!Regex.IsMatch(board.Address, "^(?:[0-9A-F]{2}:){5}[0-9A-F]{2}$")) throw new InvalidDataException("Invalid Bluetooth address.");
        }
        else if (board.Kind != "serial" || !allowSerial || !Regex.IsMatch(board.Address, "^COM[1-9][0-9]*$"))
            throw new InvalidDataException("Invalid board connection.");
        if (board.Name is null || board.Name.Length is < 1 or > 64 || string.IsNullOrWhiteSpace(board.Name) || board.Name.Any(char.IsControl))
            throw new InvalidDataException("Invalid board name.");
    }
}
