using System.Diagnostics;
using MeshCore.Windows.Core;

namespace MeshCore.Windows.Launcher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        bool test = args.Contains("--installer-test");
        string[] forwarded = args.Where(argument => argument != "--installer-test").ToArray();
        if (!test && forwarded.Length == 0) forwarded = ["--native-candidate"];
        try
        {
            string executable = VersionedInstall.ResolveCurrent(AppContext.BaseDirectory);
            var start = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false };
            foreach (string argument in forwarded) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the application.");
            if (test) { if (!process.WaitForExit(60000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("The installer test timed out."); } Environment.ExitCode = process.ExitCode; }
        }
        catch (Exception error)
        {
            if (test) { Environment.ExitCode = 1; return; }
            if (VersionedInstall.CanRecoverPrevious(AppContext.BaseDirectory) &&
                MessageBox.Show("MeshCore Command could not start. Try the previous installed version? Your saved data will not be changed.\n\n" + error.Message,
                    "MeshCore Command", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                try
                {
                    VersionedInstall.RecoverPreviousAsync(AppContext.BaseDirectory).GetAwaiter().GetResult();
                    string executable = VersionedInstall.ResolveCurrent(AppContext.BaseDirectory);
                    var start = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false };
                    foreach (string argument in forwarded) start.ArgumentList.Add(argument);
                    using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the previous application.");
                    return;
                }
                catch (Exception recoveryError) { error = recoveryError; }
            }
            MessageBox.Show("MeshCore Command could not start. Your saved data was not changed.\n\n" + error.Message,
                "MeshCore Command", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.ExitCode = 1;
        }
    }
}
