using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MeshCore.Windows.Core;

internal static class Js8CallChecks
{
    public static async Task RunAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stop = timeout.Token;
        using var client = new Js8CallClient();
        await client.ConnectAsync(((IPEndPoint)listener.LocalEndpoint).Port, stop);
        using var peer = await listener.AcceptTcpClientAsync(stop);
        using var input = new StreamReader(peer.GetStream(), leaveOpen: true);
        using var output = new StreamWriter(peer.GetStream(), leaveOpen: true) { NewLine = "\n", AutoFlush = true };
        await client.RequestAsync("STATION.GET_CALLSIGN", "", stop);
        await client.RequestAsync("RX.GET_CALL_ACTIVITY", "", stop);
        using (var station = JsonDocument.Parse((await input.ReadLineAsync(stop))!))
        using (var contacts = JsonDocument.Parse((await input.ReadLineAsync(stop))!))
            Check(station.RootElement.GetProperty("type").GetString() == "STATION.GET_CALLSIGN" &&
                station.RootElement.GetProperty("params").GetProperty("_ID").GetInt64() !=
                contacts.RootElement.GetProperty("params").GetProperty("_ID").GetInt64(),
                "Concurrent JS8Call requests use different IDs");
        await output.WriteLineAsync("""{"type":"STATION.CALLSIGN","value":"M0ABC","params":{"_ID":1}}""");
        Check(await client.ReadAsync(stop) is { Type: "STATION.CALLSIGN", Value: "M0ABC" }, "JS8Call station reply");

        string direct = Js8CallClient.Compose("g1xyz", "Hello from HFLink", false);
        Check(direct == "G1XYZ MSG HELLO FROM HFLINK", "Direct uses the stock JS8 inbox command");
        Check(Js8CallClient.Compose("@allcall", "Hello", true) == "@ALLCALL  HELLO", "Standard public group");
        Check(Js8CallClient.Compose("G1XYZ", "Hello", false, "G2XYZ") == "G2XYZ MSG TO:G1XYZ HELLO" &&
            Js8CallClient.RelayQuery("G2XYZ") == "G2XYZ QUERY MSGS" &&
            Js8CallClient.RelayQuery("G2XYZ", 12) == "G2XYZ QUERY MSG 12", "Stock relay storage and retrieval commands");
        await client.RequestAsync("TX.SEND_MESSAGE", direct, stop);
        using (var data = JsonDocument.Parse((await input.ReadLineAsync(stop))!))
            Check(data.RootElement.GetProperty("value").GetString() == direct, "JS8Call receives the approved plain-text command");
        await output.WriteLineAsync("""{"type":"RX.DIRECTED","value":"M0ABC MSG HELLO BACK ♢","params":{"CMD":" MSG ","FROM":"G1XYZ","TO":"M0ABC","SNR":-12,"UTC":1769740328005,"TEXT":"M0ABC MSG HELLO BACK ♢"}}""");
        var evt = await client.ReadAsync(stop);
        var incoming = Js8CallClient.DecodeReceived(evt.Params, evt.Value)!;
        Check(incoming is { From: "G1XYZ", To: "M0ABC", Text: "HELLO BACK", Snr: -12 }, "Stock directed reception decodes");

        using var publicFrame = JsonDocument.Parse("""{"FROM":"G1XYZ","TO":"@ALLCALL","CMD":" ","TEXT":"@ALLCALL  HELLO EVERYONE ♢"}""");
        Check(Js8CallClient.DecodeReceived(publicFrame.RootElement, "") is { Text: "HELLO EVERYONE", Command: "" },
            "JS8Call public message strips the addressed group");
        using var relayFrame = JsonDocument.Parse("""{"FROM":"G2XYZ","TO":"M0ABC","CMD":" YES","TEXT":"M0ABC YES MSG ID 12 +1 ♢"}""");
        Check(Js8CallClient.DecodeReceived(relayFrame.RootElement, "") is { Text: "MSG ID 12 +1", Command: "YES" },
            "Stock relay response exposes its message ID");

        string database = Path.Combine(Path.GetTempPath(), $"mcc-js8-{Guid.NewGuid():N}.sqlite3");
        try
        {
            var link = new HfLink(database);
            link.SaveJs8Settings(new Js8LinkSettings(PublicGroup: "@GROUP/1", Relay: "G2XYZ"));
            link.HearJs8Stations([new(incoming.From, incoming.Time, HfMessageFormat.Js8Direct, false)]);
            Check(link.RecordJs8Received(incoming, HfMessageFormat.Js8Direct) is not null &&
                link.RecordJs8Received(incoming, HfMessageFormat.Js8Direct) is null, "Repeat receives and inbox refreshes deduplicate");
            await link.SubmitJs8Async("M0ABC", "G1XYZ", "HELLO", HfMessageFormat.Js8Direct, () => Task.CompletedTask);
            var reopened = new HfLink(database);
            Check(reopened.Js8Settings().Relay == "G2XYZ" && reopened.HeardStations(false).Single().Callsign == "G1XYZ" &&
                reopened.Messages().Any(message => message is { Status: HfStatus.Submitted, Delivered: null }),
                "JS8 settings and contacts persist and submission never claims delivery");
        }
        finally { File.Delete(database); }
        Console.WriteLine("JS8Call TCP, standard message commands and HFLink storage checks passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
