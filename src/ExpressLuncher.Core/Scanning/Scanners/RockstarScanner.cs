using ExpressLuncher.Core.Models;

namespace ExpressLuncher.Core.Scanning.Scanners;

// STUB provisoire : à remplacer par la vraie implémentation.
public sealed class RockstarScanner : IGameScanner
{
    public GameSource Source => GameSource.Rockstar;

    public string DisplayName => "Rockstar Games";

    public Task<IReadOnlyList<Game>> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Game>>([]);
}
