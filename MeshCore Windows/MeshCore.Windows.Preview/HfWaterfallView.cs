using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeshCore.Windows.Preview;

internal sealed class HfWaterfallView : Control
{
    private const int Columns = 400;
    private const int Rows = 200;
    private const int DemoRate = 48000;
    private static readonly Color Background = Color.FromArgb(9, 15, 20);
    private static readonly Color TextColor = Color.FromArgb(213, 231, 241);
    private static readonly int[] Palette = MakePalette();
    private readonly Bitmap _image = new(Columns, Rows, PixelFormat.Format32bppPArgb);
    private readonly int[] _row = new int[Columns];
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 / HfWaterfallSpectrum.FramesPerSecond };
    private readonly Random _demoNoise = new(450);
    private MMDevice? _device;
    private WasapiCapture? _capture;
    private HfWaterfallSpectrum? _spectrum;
    private int _newestRow, _demoCount;
    private double _phase;
    private string _status = "";
    private bool _demo;

    public HfWaterfallView()
    {
        Name = "hfLinkWaterfallPlot";
        AccessibleName = "HF receive audio waterfall";
        Dock = DockStyle.Fill;
        DoubleBuffered = true;
        _timer.Tick += (_, _) => DrawNextFrame();
    }

    public void Start(bool simulation, Func<string> getEndpoint)
    {
        Stop();
        _newestRow = _demoCount = 0;
        _phase = 0;
        using (var graphics = Graphics.FromImage(_image)) graphics.Clear(Color.FromArgb(Palette[0]));
        _demo = simulation;
        if (simulation)
        {
            _spectrum = new(DemoRate);
            ShowStatus("Simulated receive audio");
            _timer.Start();
            return;
        }

        try
        {
            string endpoint = getEndpoint();
            using var devices = new MMDeviceEnumerator();
            _device = endpoint.Length == 0
                ? devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
                : devices.GetDevice(endpoint);
            _capture = new WasapiCapture(_device, useEventSync: true, audioBufferMillisecondsLength: 50)
            {
                ShareMode = AudioClientShareMode.Shared
            };
            var format = _capture.WaveFormat;
            if (format.Channels < 1 || format.SampleRate < 8000 ||
                format.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat) ||
                format.BitsPerSample is not (8 or 16 or 24 or 32) ||
                (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample != 32))
                throw new InvalidOperationException("The selected input uses an unsupported audio format.");
            var spectrum = new HfWaterfallSpectrum(format.SampleRate);
            _spectrum = spectrum;
            _capture.DataAvailable += (_, audio) => spectrum.AddAudio(audio.Buffer.AsSpan(0, audio.BytesRecorded), format);
            _capture.RecordingStopped += CaptureStopped;
            _capture.StartRecording();
            ShowStatus(_device.FriendlyName);
            _timer.Start();
        }
        catch (Exception error) when (error is COMException or IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            Stop();
            ShowStatus($"Audio unavailable. {error.Message}");
        }
    }

    private void CaptureStopped(object? sender, StoppedEventArgs args)
    {
        if (IsDisposed || !ReferenceEquals(sender, _capture)) return;
        Stop();
        ShowStatus(args.Exception is null ? "Audio capture stopped." :
            "Audio input stopped. Check the device and reopen the waterfall.");
    }

    public void Stop()
    {
        _timer.Stop();
        var capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            capture.RecordingStopped -= CaptureStopped;
            capture.Dispose();
        }
        _device?.Dispose();
        _device = null;
        _spectrum = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Stop();
            _timer.Dispose();
            _image.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Color surface = SystemInformation.HighContrast ? SystemColors.Window : Background;
        Color foreground = SystemInformation.HighContrast ? SystemColors.WindowText : TextColor;
        e.Graphics.Clear(surface);
        var trace = new Rectangle(0, 23, ClientSize.Width, ClientSize.Height - 45);
        if (trace.Width < 2 || trace.Height < 2) return;
        TextRenderer.DrawText(e.Graphics, _status, Font, new Rectangle(2, 0, trace.Width - 4, 22),
            foreground, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        int firstRows = Rows - _newestRow;
        int firstHeight = trace.Height * firstRows / Rows;
        DrawHistory(e.Graphics, trace, _newestRow, firstRows, firstHeight);
        if (firstHeight < trace.Height && _newestRow > 0)
            DrawHistory(e.Graphics, new Rectangle(trace.X, trace.Y + firstHeight, trace.Width, trace.Height - firstHeight),
                0, _newestRow, trace.Height - firstHeight);

        using var tick = new Pen(foreground);
        int maximumHz = _spectrum?.MaximumHz ?? 4000;
        for (int hz = 0; hz <= maximumHz; hz += 1000)
        {
            int x = (trace.Width - 1) * hz / maximumHz;
            e.Graphics.DrawLine(tick, x, trace.Bottom, x, trace.Bottom + 4);
            string label = hz == 0 ? "0 Hz" : $"{hz / 1000} kHz";
            int labelWidth = TextRenderer.MeasureText(label, Font).Width;
            TextRenderer.DrawText(e.Graphics, label, Font, new Point(Math.Clamp(x - labelWidth / 2, 0,
                Math.Max(0, trace.Width - labelWidth)), trace.Bottom + 5), foreground);
        }
    }

    private void DrawHistory(Graphics graphics, Rectangle destination, int firstRow, int rowCount, int height)
    {
        if (height > 0) graphics.DrawImage(_image, new Rectangle(destination.X, destination.Y, destination.Width, height),
            new Rectangle(0, firstRow, Columns, rowCount), GraphicsUnit.Pixel);
    }

    private void ShowStatus(string status)
    {
        _status = status;
        AccessibleDescription = status;
        Invalidate();
    }

    private void DrawNextFrame()
    {
        if (_spectrum is not { } spectrum) return;
        if (_demo)
        {
            for (int index = 0; index < DemoRate / HfWaterfallSpectrum.FramesPerSecond; index++)
            {
                double seconds = _demoCount++ / (double)DemoRate;
                _phase += 2 * Math.PI * (2200 + 500 * Math.Sin(seconds)) / DemoRate;
                double burst = seconds % 3 < 1.6 ? 0.065 : 0.005;
                spectrum.Add((float)(0.05 * Math.Sin(2 * Math.PI * 760 * seconds) +
                    burst * Math.Sin(2 * Math.PI * 1500 * seconds) +
                    0.035 * Math.Sin(_phase) + 0.02 * (_demoNoise.NextDouble() - 0.5)));
            }
        }
        if (spectrum.TakeFrame() is not { } values) return;
        for (int x = 0; x < Columns; x++)
        {
            int bin = (int)Math.Round((double)x * spectrum.MaximumHz / (Columns - 1) / spectrum.BinHz);
            int color = Math.Clamp((int)(255 * (values[bin] + 95) / 70), 0, 255);
            _row[x] = Palette[color];
        }
        _newestRow = (_newestRow + Rows - 1) % Rows;
        var bits = _image.LockBits(new Rectangle(0, _newestRow, Columns, 1),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try { Marshal.Copy(_row, 0, bits.Scan0, _row.Length); }
        finally { _image.UnlockBits(bits); }
        Invalidate();
    }

    private static int[] MakePalette()
    {
        Color[] colors = [Color.FromArgb(7, 16, 30), Color.FromArgb(24, 62, 113), Color.FromArgb(10, 150, 141),
            Color.FromArgb(239, 178, 54), Color.FromArgb(255, 248, 198)];
        var palette = new int[256];
        for (int index = 0; index < palette.Length; index++)
        {
            double position = index * (colors.Length - 1d) / palette.Length;
            int step = (int)position;
            double fraction = position - step;
            var a = colors[step];
            var b = colors[step + 1];
            palette[index] = Color.FromArgb(Blend(a.R, b.R), Blend(a.G, b.G), Blend(a.B, b.B)).ToArgb();
            int Blend(int start, int end) => (int)(start + (end - start) * fraction);
        }
        return palette;
    }
}
