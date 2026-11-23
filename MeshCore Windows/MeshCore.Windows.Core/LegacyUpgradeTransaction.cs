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
        await startAndConfirm(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await switchShortcuts(cancellationToken);
    }
}
