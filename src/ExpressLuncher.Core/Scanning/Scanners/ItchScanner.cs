using ExpressLuncher.Core.Models;

namespace ExpressLuncher.Core.Scanning.Scanners;

// STUB provisoire : à remplacer par la vraie implémentation.
public sealed class ItchScanner : IGameScanner
{
    public GameSource Source => GameSource.Itch;

    public string DisplayName => "itch.io";

    public Task<IReadOnlyList<Game>> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Game>>([]);
}
