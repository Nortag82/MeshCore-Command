using MeshCore.Windows.Core;
using System.Globalization;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace MeshCore.Windows.Bluetooth;

public sealed record BleDevice(string Address, string Name);
public sealed record BleScanDiagnostics(int Advertisements, int WithMeshCoreService, IReadOnlyList<string> MatchingNames);

/// <summary>Windows GATT implementation. Pair devices in Windows before connecting.</summary>
public sealed class WindowsBleTransport : IBlePacketTransport
{
    private readonly BluetoothLEDevice _device;
    private readonly GattDeviceService _service;
    private readonly GattCharacteristic _write, _notify;
    private int _closed;
    private readonly object _gate = new();
    private readonly Queue<byte[]> _pending = new();
    private Action<byte[]>? _packetReceived;
    private Action? _disconnected;
    private bool _linkLost;
    public event Action<byte[]>? PacketReceived
    {
        add { lock (_gate) { _packetReceived += value; while (_pending.TryDequeue(out var packet)) _packetReceived?.Invoke(packet); } }
        remove { lock (_gate) _packetReceived -= value; }
    }
    public event Action? Disconnected
    {
        add { lock (_gate) { _disconnected += value; if (_linkLost) value?.Invoke(); } }
        remove { lock (_gate) _disconnected -= value; }
    }

    private WindowsBleTransport(BluetoothLEDevice device, GattDeviceService service, GattCharacteristic write, GattCharacteristic notify)
    {
        _device = device; _service = service; _write = write; _notify = notify;
        _device.ConnectionStatusChanged += DeviceConnectionChanged;
        _notify.ValueChanged += ValueChanged;
    }

    public static async Task<IReadOnlyList<BleDevice>> ScanAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(duration));
        var found = new System.Collections.Concurrent.ConcurrentDictionary<ulong, (bool Service, string Name)>();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, args) => {
            bool service = args.Advertisement.ServiceUuids.Contains(BleCompanion.ServiceId);
            string name = args.Advertisement.LocalName ?? "";
            if (!service && string.IsNullOrWhiteSpace(name)) return;
            found.AddOrUpdate(args.BluetoothAddress, (service, name), (_, existing) => (existing.Service || service, string.IsNullOrWhiteSpace(name) ? existing.Name : name));
        };
        try { watcher.Start(); }
        catch (System.Runtime.InteropServices.COMException error) when ((uint)error.HResult == 0x800710DF)
        { throw new InvalidOperationException("Windows Bluetooth is switched off or unavailable. Turn Bluetooth on and try again.", error); }
        try { await Task.Delay(duration, cancellationToken); }
        finally { watcher.Stop(); }
        return found.Where(item => item.Value.Service).Select(item => new BleDevice(
            string.Join(":", Enumerable.Range(0, 6).Reverse().Select(i => ((item.Key >> (i * 8)) & 255).ToString("X2", CultureInfo.InvariantCulture))),
            string.IsNullOrWhiteSpace(item.Value.Name) ? "MeshCore companion" : item.Value.Name)).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static async Task<BleScanDiagnostics> DiagnoseScanAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        int count = 0, service = 0;
        var names = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, args) => {
            Interlocked.Increment(ref count);
            if (args.Advertisement.ServiceUuids.Contains(BleCompanion.ServiceId)) Interlocked.Increment(ref service);
            string name = args.Advertisement.LocalName;
            if (!string.IsNullOrWhiteSpace(name) && (name.Contains("meshcore", StringComparison.OrdinalIgnoreCase) || name.Contains("heltec", StringComparison.OrdinalIgnoreCase) || name.Contains("nortag", StringComparison.OrdinalIgnoreCase))) names.TryAdd(name, 0);
        };
        try { watcher.Start(); await Task.Delay(duration, cancellationToken); }
        finally { watcher.Stop(); }
        return new BleScanDiagnostics(count, service, names.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public static async Task<WindowsBleTransport> ConnectAsync(string address, CancellationToken cancellationToken = default)
    {
        if (address is null || !System.Text.RegularExpressions.Regex.IsMatch(address, "^(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$"))
            throw new ArgumentException("Invalid Bluetooth address.", nameof(address));
        ulong numeric = ulong.Parse(address.Replace(":", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(numeric).AsTask(cancellationToken)
            ?? throw new IOException("Bluetooth device was not found.");
        GattDeviceService? service = null;
        WindowsBleTransport? transport = null;
        try
        {
            var info = await DeviceInformation.CreateFromIdAsync(device.DeviceInformation.Id).AsTask(cancellationToken);
            if (!info.Pairing.IsPaired || info.Pairing.ProtectionLevel != DevicePairingProtectionLevel.EncryptionAndAuthentication)
                throw new IOException("Pair the device using its PIN before connecting; authenticated pairing is required.");
            GattDeviceServicesResult services;
            int attempts = 0;
            do
            {
                services = await device.GetGattServicesForUuidAsync(BleCompanion.ServiceId, BluetoothCacheMode.Uncached).AsTask(cancellationToken);
                if (services.Status != GattCommunicationStatus.Unreachable || ++attempts >= 3) break;
                await Task.Delay(1000, cancellationToken);
            } while (true);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count != 1) throw new IOException($"MeshCore Bluetooth service unavailable ({services.Status}; {services.Services.Count} matches).");
            service = services.Services[0];
            var writers = await service.GetCharacteristicsForUuidAsync(BleCompanion.WriteId, BluetoothCacheMode.Uncached).AsTask(cancellationToken);
            var notifications = await service.GetCharacteristicsForUuidAsync(BleCompanion.NotifyId, BluetoothCacheMode.Uncached).AsTask(cancellationToken);
            if (writers.Status != GattCommunicationStatus.Success || writers.Characteristics.Count != 1 || notifications.Status != GattCommunicationStatus.Success || notifications.Characteristics.Count != 1)
                throw new IOException("The MeshCore Bluetooth characteristics are unavailable.");
            transport = new WindowsBleTransport(device, service, writers.Characteristics[0], notifications.Characteristics[0]);
            var status = await transport._notify.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(cancellationToken);
            if (status != GattCommunicationStatus.Success) throw new IOException("Could not enable MeshCore Bluetooth notifications.");
            var mtuDeadline = DateTime.UtcNow.AddSeconds(8);
            while (service.Session.MaxPduSize < 100 && device.ConnectionStatus == BluetoothConnectionStatus.Connected && DateTime.UtcNow < mtuDeadline)
                await Task.Delay(200, cancellationToken);
            if (service.Session.MaxPduSize < 100) throw new IOException("Bluetooth negotiated an MTU too small for MeshCore companion frames.");
            if (device.ConnectionStatus != BluetoothConnectionStatus.Connected) throw new IOException("Bluetooth disconnected during setup.");
            return transport;
        }
        catch
        {
            if (transport is not null) await transport.DisposeAsync();
            else { service?.Dispose(); device.Dispose(); }
            throw;
        }
    }

    public static async Task<(bool Paired, bool Authenticated)> PairingStatusAsync(string address, CancellationToken cancellationToken = default)
    {
        if (address is null || !System.Text.RegularExpressions.Regex.IsMatch(address, "^(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$")) throw new ArgumentException("Invalid Bluetooth address.", nameof(address));
        ulong numeric = ulong.Parse(address.Replace(":", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(numeric).AsTask(cancellationToken) ?? throw new IOException("Bluetooth device was not found.");
        var info = await DeviceInformation.CreateFromIdAsync(device.DeviceInformation.Id).AsTask(cancellationToken);
        return (info.Pairing.IsPaired, info.Pairing.ProtectionLevel == DevicePairingProtectionLevel.EncryptionAndAuthentication);
    }

    public static async Task PairAsync(string address, string pin, CancellationToken cancellationToken = default)
    {
        if (address is null || !System.Text.RegularExpressions.Regex.IsMatch(address, "^(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$")) throw new ArgumentException("Invalid Bluetooth address.", nameof(address));
        if (pin is null || (pin.Length != 0 && !System.Text.RegularExpressions.Regex.IsMatch(pin, "^[0-9]{6}$"))) throw new ArgumentException("Enter the six-digit PIN shown by the board.", nameof(pin));
        ulong numeric = ulong.Parse(address.Replace(":", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(numeric).AsTask(cancellationToken) ?? throw new IOException("Bluetooth device was not found.");
        var info = await DeviceInformation.CreateFromIdAsync(device.DeviceInformation.Id).AsTask(cancellationToken);
        if (info.Pairing.IsPaired)
        {
            if (info.Pairing.ProtectionLevel != DevicePairingProtectionLevel.EncryptionAndAuthentication) throw new IOException("Remove the unauthenticated pairing in Windows and pair again with the PIN.");
            return;
        }
        if (pin.Length != 6 || !info.Pairing.CanPair) throw new IOException("Windows cannot pair this board; check its PIN and discoverability.");
        var custom = info.Pairing.Custom;
        void Requested(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args)
        { if (args.PairingKind == DevicePairingKinds.ProvidePin) args.Accept(pin); }
        custom.PairingRequested += Requested;
        try
        {
            var result = await custom.PairAsync(DevicePairingKinds.ProvidePin, DevicePairingProtectionLevel.EncryptionAndAuthentication).AsTask(cancellationToken);
            if (result.Status is not (DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired)) throw new IOException($"Windows Bluetooth pairing failed ({result.Status}).");
        }
        finally { custom.PairingRequested -= Requested; }
        var verified = await DeviceInformation.CreateFromIdAsync(info.Id).AsTask(cancellationToken);
        if (!verified.Pairing.IsPaired || verified.Pairing.ProtectionLevel != DevicePairingProtectionLevel.EncryptionAndAuthentication)
            throw new IOException("Windows did not confirm authenticated PIN pairing.");
    }

    private void ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        using var reader = DataReader.FromBuffer(args.CharacteristicValue);
        byte[] packet = new byte[reader.UnconsumedBufferLength]; reader.ReadBytes(packet);
        lock (_gate)
        {
            if (_linkLost) return;
            if (_packetReceived is not null) _packetReceived(packet);
            else if (_pending.Count < 512) _pending.Enqueue(packet);
            else SignalDisconnected();
        }
    }

    private void DeviceConnectionChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected) lock (_gate) SignalDisconnected();
    }

    private void SignalDisconnected() { if (_linkLost) return; _linkLost = true; _disconnected?.Invoke(); }

    public async Task WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _closed) != 0) throw new IOException("Bluetooth connection is closed.");
        if (packet.Length > _service.Session.MaxPduSize - 3) throw new IOException("Companion frame exceeds the negotiated Bluetooth MTU.");
        using var writer = new DataWriter(); writer.WriteBytes(packet.ToArray());
        var result = await _write.WriteValueWithResultAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse).AsTask(cancellationToken);
        if (result.Status != GattCommunicationStatus.Success) throw new IOException("Bluetooth write failed; delivery is uncertain.");
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return ValueTask.CompletedTask;
        _notify.ValueChanged -= ValueChanged; _device.ConnectionStatusChanged -= DeviceConnectionChanged;
        _service.Dispose(); _device.Dispose();
        return ValueTask.CompletedTask;
    }
}
