using System.Text;

namespace MeshCore.Windows.Core;

/// <summary>Bounded diagnostics that deliberately omit exception messages and user data.</summary>
public sealed class CrashLog
{
    private const long MaximumBytes = 1_048_576;
    private readonly string _path;
    private readonly object _gate = new();

    public CrashLog(string path) => _path = Path.GetFullPath(path);

    public void Record(string category, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (category is not ("thread" or "unhandled" or "task")) throw new ArgumentException("Invalid diagnostic category.");
        string type = error.GetType().FullName ?? "Exception";
        // Exception messages and source paths may contain credentials or message text.
        // Only method names from this application are retained, without arguments or paths.
        var frames = new System.Diagnostics.StackTrace(error, fNeedFileInfo: false).GetFrames() ?? [];
        string methods = string.Join(" > ", frames.Select(frame => frame.GetMethod())
            .Where(method => method?.DeclaringType?.FullName?.StartsWith("MeshCore.Windows.", StringComparison.Ordinal) == true)
            .Select(method => method!.DeclaringType!.FullName + "." + method.Name).Take(12));
        string line = $"{DateTimeOffset.UtcNow:O}\t{category}\t{type}\t0x{error.HResult:X8}\t{methods}{Environment.NewLine}";
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (File.Exists(_path) && new FileInfo(_path).Length + Encoding.UTF8.GetByteCount(line) > MaximumBytes)
                File.Move(_path, _path + ".previous", overwrite: true);
            File.AppendAllText(_path, line, new UTF8Encoding(false));
        }
    }
}
