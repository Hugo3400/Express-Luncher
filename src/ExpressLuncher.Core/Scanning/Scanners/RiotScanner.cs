using ExpressLuncher.Core.Models;

namespace ExpressLuncher.Core.Scanning.Scanners;

// STUB provisoire : à remplacer par la vraie implémentation.
public sealed class RiotScanner : IGameScanner
{
    public GameSource Source => GameSource.Riot;

    public string DisplayName => "Riot Games";

    public Task<IReadOnlyList<Game>> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Game>>([]);
}
