using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MeshCore.Windows.Core;

/// <summary>Prepares a verified native installer handoff; the caller starts it only after user confirmation.</summary>
public static class UpdateHandoff
{
    public static async Task<ProcessStartInfo> PrepareAsync(UpdaterClient updater, string currentVersion, string expectedVersion,
        string temporaryDirectory, int processId, long startUtcTicks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updater);
        if (processId <= 0 || startUtcTicks <= 0 || !Regex.IsMatch(expectedVersion ?? "", "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Invalid update handoff information.");
        string installer = await updater.DownloadAsync(currentVersion, expectedVersion!, temporaryDirectory, cancellationToken);
        var start = new ProcessStartInfo(installer) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(installer)! };
        start.ArgumentList.Add("--native-update");
        start.ArgumentList.Add("--parent-pid=" + processId.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--parent-start=" + startUtcTicks.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--expected-version=" + expectedVersion);
        return start;
    }
}
