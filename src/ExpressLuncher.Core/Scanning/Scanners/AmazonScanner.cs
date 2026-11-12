using System.Text.RegularExpressions;
using ExpressLuncher.Core.Models;

namespace ExpressLuncher.Core.Scanning.Scanners;

/// <summary>
/// Amazon Games : la base des jeux est en SQLite, on lit donc les entrées Uninstall dont le
/// désinstalleur est "Amazon Game Remover.exe" (-p &lt;productId&gt;). Lancement par amazon-games://.
/// </summary>
public sealed class AmazonScanner : IGameScanner
{
    private static readonly Regex ProductIdPattern = new(
        "\\s-p\\s+\"?(?<id>[A-Za-z0-9._\\-]+)\"?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public GameSource Source => GameSource.Amazon;

    public string DisplayName => "Amazon Games";

    public Task<IReadOnlyList<Game>> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Scan(cancellationToken));

    private static IReadOnlyList<Game> Scan(CancellationToken cancellationToken)
    {
        var games = new List<Game>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in UninstallRegistry.ReadAll())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var game = ReadGame(entry);
                if (game is not null && seen.Add(game.Id)) games.Add(game);
            }
            catch
            {
                // jeu illisible : ignoré
            }
        }

        return games;
    }

    private static Game? ReadGame(UninstallEntry entry)
    {
        var remover = entry.UninstallString.IndexOf("Amazon Game Remover.exe", StringComparison.OrdinalIgnoreCase);
        if (remover < 0) return null;

        // on ne cherche "-p" qu'après le chemin du désinstalleur
        var match = ProductIdPattern.Match(entry.UninstallString, remover);
        if (!match.Success) return null;
        var productId = match.Groups["id"].Value;

        var name = entry.DisplayName;
        if (name.Length == 0) return null;

        var installDir = UninstallRegistry.CleanDirectory(entry.InstallLocation);
        if (installDir is null || !Directory.Exists(installDir)) return null;

        return new Game
        {
            Id = ScanHelpers.MakeId(GameSource.Amazon, productId),
            Name = name,
            Source = GameSource.Amazon,
            InstallPath = installDir,
            ExecutablePath = ScanHelpers.FindMainExecutable(installDir, name),
            LaunchUri = $"amazon-games://play/{productId}",
        };
    }
}
