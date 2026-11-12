using ExpressLuncher.Core.Models;
using Microsoft.Win32;

namespace ExpressLuncher.Core.Scanning.Scanners;

/// <summary>
/// Jeux GOG : une sous-clé de registre par jeu installé (avec ou sans GOG Galaxy).
/// Sans DRM, ils se lancent directement par leur exécutable.
/// </summary>
public sealed class GogScanner : IGameScanner
{
    private const string GamesKey = @"SOFTWARE\GOG.com\Games";

    public GameSource Source => GameSource.Gog;

    public string DisplayName => "GOG";

    public Task<IReadOnlyList<Game>> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(cancellationToken), cancellationToken);

    private static IReadOnlyList<Game> Scan(CancellationToken cancellationToken)
    {
        var games = new List<Game>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var gamesKey in ScanHelpers.OpenRegistryKeys(RegistryHive.LocalMachine, GamesKey))
        {
            using (gamesKey)
            {
                string[] subKeyNames;
                try { subKeyNames = gamesKey.GetSubKeyNames(); }
                catch { continue; }

                foreach (var subKeyName in subKeyNames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        using var gameKey = gamesKey.OpenSubKey(subKeyName);
                        if (gameKey is null) continue;

                        var game = ReadGame(gameKey, subKeyName);
                        if (game is not null && seenIds.Add(game.Id)) games.Add(game);
                    }
                    catch
                    {
                        // entrée illisible : on passe à la suivante
                    }
                }
            }
        }

        return games;
    }

    private static Game? ReadGame(RegistryKey key, string subKeyName)
    {
        // Un DLC référence le jeu dont il dépend.
        if (ReadValue(key, "dependsOn") is not null) return null;

        var gameId = ReadValue(key, "gameID") ?? subKeyName.Trim();
        var name = ReadValue(key, "gameName");
        if (gameId.Length == 0 || name is null) return null;

        var installPath = ReadPath(key, "path");
        if (installPath is null) return null;
        installPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installPath));
        if (!Directory.Exists(installPath)) return null;

        var executable = ResolveExecutable(installPath, ReadPath(key, "exe"))
                         ?? ScanHelpers.FindMainExecutable(installPath, name);

        // Pas d'URI de boutique : sans exécutable, le jeu ne serait pas lançable.
        if (executable is null) return null;

        return new Game
        {
            Id = ScanHelpers.MakeId(GameSource.Gog, gameId),
            Name = name,
            Source = GameSource.Gog,
            InstallPath = installPath,
            ExecutablePath = executable,
            LaunchUri = null,
            LaunchArguments = ReadValue(key, "launchParam"),
            CoverPath = null,
        };
    }

    private static string? ResolveExecutable(string installPath, string? exe)
    {
        if (exe is null) return null;

        // Normalement un chemin complet ; par prudence on accepte aussi un chemin relatif au jeu.
        var full = Path.GetFullPath(Path.IsPathRooted(exe) ? exe : Path.Combine(installPath, exe));
        return File.Exists(full) ? full : null;
    }

    /// <summary>Valeur de registre sans espaces superflus ; null si absente ou vide.</summary>
    private static string? ReadValue(RegistryKey key, string valueName)
    {
        var text = key.GetValue(valueName)?.ToString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>Comme <see cref="ReadValue"/>, pour un chemin éventuellement entre guillemets.</summary>
    private static string? ReadPath(RegistryKey key, string valueName)
    {
        var text = ReadValue(key, valueName)?.Trim('"').Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
