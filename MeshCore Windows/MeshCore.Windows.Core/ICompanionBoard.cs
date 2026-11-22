namespace MeshCore.Windows.Core;

public interface ICompanionBoard : IAsyncDisposable
{
    FrameQueue Received { get; }
    bool Connected { get; }
    Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
}
