using ExpressLuncher.Core.Models;
using Microsoft.Win32;

namespace ExpressLuncher.Core.Scanning.Scanners;

/// <summary>
/// Ubisoft Connect : un jeu par sous-clé de HKLM\SOFTWARE\Ubisoft\Launcher\Installs (valeur InstallDir).
/// Le nom vient de l'entrée Uninstall "Uplay Install {id}", le lancement passe par uplay://.
/// </summary>
public sealed class UbisoftScanner : IGameScanner
{
    private const string InstallsKey = @"SOFTWARE\Ubisoft\Launcher\Installs";
    private const string UninstallPrefix = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Uplay Install ";

    public GameSource Source => GameSource.Ubisoft;

    public string DisplayName => "Ubisoft Connect";

    public Task<IReadOnlyList<Game>> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Scan(cancellationToken));

    private static IReadOnlyList<Game> Scan(CancellationToken cancellationToken)
    {
        var games = new List<Game>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var installs in ScanHelpers.OpenRegistryKeys(RegistryHive.LocalMachine, InstallsKey))
        {
            using (installs)
            {
                string[] gameIds;
                try { gameIds = installs.GetSubKeyNames(); }
                catch { continue; }

                foreach (var gameId in gameIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var game = ReadGame(installs, gameId);
                        if (game is not null && seen.Add(game.Id)) games.Add(game);
                    }
                    catch
                    {
                        // jeu illisible : ignoré
                    }
                }
            }
        }

        return games;
    }

    private static Game? ReadGame(RegistryKey installs, string gameId)
    {
        // l'URI de lancement n'accepte que l'identifiant numérique
        if (gameId.Length == 0 || !gameId.All(char.IsAsciiDigit)) return null;

        using var key = installs.OpenSubKey(gameId);
        var installDir = UninstallRegistry.CleanDirectory(key?.GetValue("InstallDir")?.ToString());
        if (installDir is null || !Directory.Exists(installDir)) return null;
        if (UninstallRegistry.BelongsToAnotherStore(installDir)) return null;

        var name = ScanHelpers.ReadRegistryString(RegistryHive.LocalMachine, UninstallPrefix + gameId, "DisplayName")?.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileName(installDir);
        if (string.IsNullOrWhiteSpace(name)) return null;

        return new Game
        {
            Id = ScanHelpers.MakeId(GameSource.Ubisoft, gameId),
            Name = name,
            Source = GameSource.Ubisoft,
            InstallPath = installDir,
            ExecutablePath = ScanHelpers.FindMainExecutable(installDir, name),
            LaunchUri = $"uplay://launch/{gameId}/0",
        };
    }
}
