using ExpressLuncher.Core.Models;

namespace ExpressLuncher.Core.Scanning.Scanners;

// STUB provisoire : à remplacer par la vraie implémentation.
public sealed class XboxScanner : IGameScanner
{
    public GameSource Source => GameSource.Xbox;

    public string DisplayName => "Xbox / Microsoft Store";

    public Task<IReadOnlyList<Game>> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Game>>([]);
}
