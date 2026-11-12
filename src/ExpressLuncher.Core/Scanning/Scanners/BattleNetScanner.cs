using System.Text.RegularExpressions;
using ExpressLuncher.Core.Models;

namespace ExpressLuncher.Core.Scanning.Scanners;

/// <summary>
/// Battle.net : product.db est en protobuf, on lit donc les entrées Uninstall dont le désinstalleur
/// est celui de Battle.net ("Blizzard Uninstaller.exe" --uid=&lt;produit&gt;). Lancement par l'exécutable.
/// </summary>
public sealed class BattleNetScanner : IGameScanner
{
    private static readonly Regex UidPattern = new(
        "--uid=(?:\"(?<uid>[^\"]+)\"|(?<uid>\\S+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // le client lui-même et son agent de mise à jour
    private static readonly string[] ClientUids = ["battle.net", "bna", "agent"];

    public GameSource Source => GameSource.BattleNet;

    public string DisplayName => "Battle.net";

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
        var uninstall = entry.UninstallString;
        if (!uninstall.Contains("Battle.net", StringComparison.OrdinalIgnoreCase)
            && !uninstall.Contains("Blizzard Uninstaller", StringComparison.OrdinalIgnoreCase))
            return null;

        var match = UidPattern.Match(uninstall);
        if (!match.Success) return null;

        var uid = match.Groups["uid"].Value.Trim().ToLowerInvariant();
        if (uid.Length == 0 || ClientUids.Contains(uid)) return null;

        var name = entry.DisplayName;
        if (name.Length == 0 || name.Equals("Battle.net", StringComparison.OrdinalIgnoreCase)) return null;

        var installDir = UninstallRegistry.CleanDirectory(entry.InstallLocation);
        if (installDir is null || !Directory.Exists(installDir)) return null;
        if (File.Exists(Path.Combine(installDir, "Battle.net.exe"))) return null;

        var executable = ScanHelpers.FindMainExecutable(installDir, name);
        if (executable is null) return null;

        return new Game
        {
            Id = ScanHelpers.MakeId(GameSource.BattleNet, uid),
            Name = name,
            Source = GameSource.BattleNet,
            InstallPath = installDir,
            ExecutablePath = executable,
        };
    }
}
