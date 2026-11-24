namespace MeshCore.Windows.Core;

/// <summary>Orders an old-app upgrade without modifying legacy files or switching shortcuts before readiness.</summary>
public static class LegacyUpgradeTransaction
{
    public static async Task RunAsync(string[] arguments, string expectedLegacyExecutable,
        Func<CancellationToken, Task> prepare, Func<CancellationToken, Task> startAndConfirm,
        Func<CancellationToken, Task> switchShortcuts, TimeSpan parentTimeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(startAndConfirm);
        ArgumentNullException.ThrowIfNull(switchShortcuts);
        int pid = LegacyUpdateHandoff.Parse(arguments);
        await LegacyUpdateParentGate.WaitAsync(pid, expectedLegacyExecutable, parentTimeout, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        LegacyProcessGuard.EnsureStopped(expectedLegacyExecutable);
        await prepare(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        LegacyProcessGuard.EnsureStopped(expectedLegacyExecutable);
        await startAndConfirm(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        try { LegacyProcessGuard.EnsureStopped(expectedLegacyExecutable); }
        catch (IOException error)
        {
            throw new IOException("The old app reopened during the upgrade. Shortcuts were not changed. The native app may also be open with separate data; close both apps and do not use the profiles interchangeably.", error);
        }
        await switchShortcuts(cancellationToken);
    }
}
