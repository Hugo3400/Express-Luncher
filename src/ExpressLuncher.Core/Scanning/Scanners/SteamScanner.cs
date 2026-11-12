using ExpressLuncher.Core.Models;

namespace ExpressLuncher.Core.Scanning.Scanners;

// STUB provisoire : à remplacer par la vraie implémentation.
public sealed class SteamScanner : IGameScanner
{
    public GameSource Source => GameSource.Steam;

    public string DisplayName => "Steam";

    public Task<IReadOnlyList<Game>> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Game>>([]);
}
