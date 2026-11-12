using System.IO;
using ExpressLuncher.Core.Artwork;
using ExpressLuncher.Core.Models;

namespace ExpressLuncher.Services;

/// <summary>
/// Demande à <see cref="ArtworkService"/> les illustrations locales des jeux qui en manquent, quelques
/// jeux à la fois, puis applique le résultat au Game sur le thread appelant (le thread UI).
/// </summary>
public sealed class ArtworkCoordinator : IDisposable
{
    private const int MaxParallel = 4;

    private readonly ArtworkService _service;
    private readonly SemaphoreSlim _gate = new(MaxParallel);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _requested = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GameArtwork> _resolved = new(StringComparer.OrdinalIgnoreCase);

    public ArtworkCoordinator(ArtworkService service) => _service = service;

    /// <summary>Les chemins d'illustration d'un jeu viennent de changer (levé sur le thread UI).</summary>
    public event Action<Game>? Applied;

    /// <summary>
    /// À appeler depuis le thread UI, dans l'ordre de priorité. Un jeu déjà résolu pendant cette session
    /// retrouve aussitôt ses chemins locaux (un scan a pu les écraser) ; les autres sont mis en file.
    /// </summary>
    public void Request(IEnumerable<Game> games)
    {
        foreach (var game in games)
        {
            if (_resolved.TryGetValue(game.Id, out var known))
            {
                if (Apply(game, known)) Applied?.Invoke(game);
            }
            else if (_requested.Add(game.Id))
            {
                _ = ResolveAsync(game);
            }
        }
    }

    private async Task ResolveAsync(Game game)
    {
        var token = _lifetime.Token;
        try
        {
            await _gate.WaitAsync(token);
            try
            {
                var paths = new[] { game.CoverPath, game.HeroPath, game.LogoPath };
                var artwork = await Task.Run(
                    async () => paths.All(IsLocalFile) ? null : await _service.ResolveAsync(game, token).ConfigureAwait(false),
                    token);

                if (artwork is null) return;
                _resolved[game.Id] = artwork;
                if (Apply(game, artwork)) Applied?.Invoke(game);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // fermeture de l'application
        }
        catch (ObjectDisposedException)
        {
            // idem
        }
    }

    private static bool Apply(Game game, GameArtwork artwork)
    {
        var changed = false;
        if (artwork.CoverPath is not null && artwork.CoverPath != game.CoverPath) { game.CoverPath = artwork.CoverPath; changed = true; }
        if (artwork.HeroPath is not null && artwork.HeroPath != game.HeroPath) { game.HeroPath = artwork.HeroPath; changed = true; }
        if (artwork.LogoPath is not null && artwork.LogoPath != game.LogoPath) { game.LogoPath = artwork.LogoPath; changed = true; }
        return changed;
    }

    private static bool IsLocalFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || ImageLoader.IsRemote(path)) return false;
        try { return File.Exists(path); }
        catch { return false; }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
