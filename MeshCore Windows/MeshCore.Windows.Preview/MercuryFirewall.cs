using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace MeshCore.Windows.Preview;

internal static class MercuryFirewall
{
    private const string Protection = """
        $ErrorActionPreference = 'Stop'
        $scope = @('0.0.0.0-126.255.255.255','128.0.0.0-255.255.255.255','::2-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff')
        function Test-Protection {
            if ((Get-Service MpsSvc).Status -ne 'Running') { return $false }
            $profiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore)
            if ($profiles.Count -ne 3) { return $false }
            foreach ($profile in $profiles) {
                if ($profile.Enabled -ne 'True' -or $profile.AllowLocalFirewallRules -eq 'False' -or
                    ($profile.DisabledInterfaceAliases | Where-Object { $_ -and $_ -ne 'NotConfigured' })) { return $false }
            }
            $rules = @(Get-NetFirewallRule -Name $name -PolicyStore ActiveStore -ErrorAction SilentlyContinue)
            if ($rules.Count -ne 1) { return $false }
            $rule = $rules[0]
            if ($rule.Enabled -ne 'True' -or $rule.Direction -ne 'Inbound' -or $rule.Action -ne 'Block' -or $rule.Profile -ne 'Any') { return $false }
            $program = $rule | Get-NetFirewallApplicationFilter
            $addresses = $rule | Get-NetFirewallAddressFilter
            $ports = $rule | Get-NetFirewallPortFilter
            $interfaces = $rule | Get-NetFirewallInterfaceFilter
            $types = $rule | Get-NetFirewallInterfaceTypeFilter
            $service = $rule | Get-NetFirewallServiceFilter
            return ($program.Program -ieq $exe -and $program.Package -in @('Any','', $null) -and
                $addresses.LocalAddress -eq 'Any' -and $ports.Protocol -eq 'TCP' -and
                $ports.LocalPort -eq 'Any' -and $ports.RemotePort -eq 'Any' -and
                $interfaces.InterfaceAlias -eq 'Any' -and $types.InterfaceType -eq 'Any' -and $service.Service -eq 'Any' -and
                -not (Compare-Object $scope @($addresses.RemoteAddress)))
        }
        """;

    public static async Task<bool> IsProtectedAsync(string executable, CancellationToken stop)
    {
        using var process = Process.Start(StartInfo(executable, Protection + "\nif (Test-Protection) { exit 0 }; exit 2", elevated: false))
            ?? throw new IOException("Could not check Windows Firewall.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            throw new IOException("Windows Firewall did not respond.");
        }
        finally
        {
            if (!process.HasExited)
                try { process.Kill(); } catch (InvalidOperationException) { }
        }
    }

    public static async Task ConfigureAsync(string executable, CancellationToken stop)
    {
        if (await IsProtectedAsync(executable, stop)) return;
        const string configure = """

            if (Test-Protection) { exit 0 }
            $options = @{
                Enabled='True'; Direction='Inbound'; Action='Block'; Profile='Any'; Protocol='TCP'
                Program=$exe; RemoteAddress=$scope; LocalAddress='Any'; LocalPort='Any'; RemotePort='Any'
                InterfaceAlias='Any'; InterfaceType='Any'; Service='Any'; EdgeTraversalPolicy='Block'
            }
            if (Get-NetFirewallRule -Name $name -PolicyStore PersistentStore -ErrorAction SilentlyContinue) {
                Set-NetFirewallRule -Name $name -PolicyStore PersistentStore @options | Out-Null
            } else {
                New-NetFirewallRule -Name $name -PolicyStore PersistentStore -DisplayName 'MeshCore Command Mercury (block remote TCP)' @options | Out-Null
            }
            Set-NetFirewallProfile -Profile Domain,Private,Public -Enabled True
            if (Test-Protection) { exit 0 }; exit 2
            """;
        int exitCode;
        try
        {
            using var process = Process.Start(StartInfo(executable, Protection + "\n" + configure, elevated: true))
                ?? throw new IOException("Windows did not start firewall setup.");
            await process.WaitForExitAsync(stop);
            exitCode = process.ExitCode;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            throw new IOException("Windows administrator approval was cancelled.");
        }
        if (exitCode != 0 || !await IsProtectedAsync(executable, stop))
            throw new IOException("Windows did not confirm Mercury’s firewall protection. Bundled Mercury will not start.");
    }

    private static ProcessStartInfo StartInfo(string executable, string script, bool elevated)
    {
        string encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.GetFullPath(executable)));
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executable.ToUpperInvariant())))[..16];
        string preamble = $"$exe=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{encodedPath}'));$name='MeshCore-HFLink-Mercury-{identity}'\n";
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = elevated,
            Verb = elevated ? "runas" : "",
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = !elevated
        };
        foreach (string value in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(preamble + script)) }) start.ArgumentList.Add(value);
        return start;
    }
}
