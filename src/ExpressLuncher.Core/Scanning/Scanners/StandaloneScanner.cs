using System.Text.RegularExpressions;
using ExpressLuncher.Core.Models;
using Microsoft.Win32;

namespace ExpressLuncher.Core.Scanning.Scanners;

/// <summary>
/// Jeux et clients de jeu installés hors de toute boutique, à partir d'une liste connue :
/// FiveM, RedM, RAGE Multiplayer, Roblox, Minecraft Launcher, osu!.
/// Un titre n'est retenu que si son exécutable existe vraiment sur le disque.
/// </summary>
public sealed partial class StandaloneScanner : IGameScanner
{
    /// <param name="Key">Clé stable de l'identifiant ("fivem" → "standalone:fivem").</param>
    /// <param name="Name">Nom affiché.</param>
    /// <param name="ExeNames">Exécutables possibles, par ordre de préférence, à la racine du dossier.</param>
    /// <param name="UninstallKeys">Noms exacts de clés Uninstall.</param>
    /// <param name="UninstallNames">Noms d'affichage exacts dans « Programmes et fonctionnalités ».</param>
    /// <param name="Registry">Valeurs HKCU (sous-clé, nom) contenant le dossier ou un de ses sous-dossiers.</param>
    /// <param name="DefaultDirectories">Emplacements par défaut (variables d'environnement acceptées).</param>
    private sealed record KnownTitle(
        string Key,
        string Name,
        string[] ExeNames,
        string[] UninstallKeys,
        string[] UninstallNames,
        (string SubKey, string Value)[] Registry,
        string[] DefaultDirectories);

    private static readonly KnownTitle[] Titles =
    [
        new("fivem", "FiveM", ["FiveM.exe"],
            ["CitizenFX_FiveM"], ["FiveM"],
            [(@"Software\CitizenFX\FiveM", "Last Run Location")],
            [@"%LOCALAPPDATA%\FiveM"]),

        new("redm", "RedM", ["RedM.exe"],
            ["CitizenFX_RedM"], ["RedM"],
            [(@"Software\CitizenFX\RedM", "Last Run Location")],
            [@"%LOCALAPPDATA%\RedM"]),

        new("ragemp", "RAGE Multiplayer", ["updater.exe", "ragemp_v.exe"],
            [], ["RAGE Multiplayer"],
            [(@"Software\RAGE-MP", "rage_path")],
            [@"C:\RAGEMP"]),

        new("roblox", "Roblox", ["RobloxPlayerBeta.exe"],
            ["roblox-player"], ["Roblox Player"],
            [],
            []),

        new("minecraft-launcher", "Minecraft Launcher", ["MinecraftLauncher.exe"],
            [], ["Minecraft Launcher"],
            [],
            [@"%ProgramFiles(x86)%\Minecraft Launcher", @"%ProgramFiles%\Minecraft Launcher"]),

        new("osu", "osu!", ["osu!.exe"],
            [], ["osu!"],
            [],
            [@"%LOCALAPPDATA%\osu!"]),
    ];

    public GameSource Source => GameSource.Standalone;

    public string DisplayName => "Autres";

    public Task<IReadOnlyList<Game>> ScanAsync(CancellationToken cancellationToken = default)
    {
        var uninstall = UninstallRegistry.ReadAll();
        var games = new List<Game>();

        foreach (var title in Titles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var exe = FindExecutable(title, uninstall);
                if (exe is null) continue;

                games.Add(new Game
                {
                    Id = ScanHelpers.MakeId(GameSource.Standalone, title.Key),
                    Name = title.Name,
                    Source = GameSource.Standalone,
                    InstallPath = Path.GetDirectoryName(exe),
                    ExecutablePath = exe,
                });
            }
            catch
            {
                // titre illisible : ignoré, les autres continuent
            }
        }

        return Task.FromResult<IReadOnlyList<Game>>(games);
    }

    private static string? FindExecutable(KnownTitle title, IReadOnlyCollection<UninstallEntry> uninstall)
    {
        foreach (var directory in CandidateDirectories(title, uninstall))
        {
            // Une valeur de registre peut pointer un sous-dossier (ex. ...\FiveM\FiveM.app\) :
            // on regarde aussi le dossier parent.
            foreach (var dir in new[] { directory, Path.GetDirectoryName(directory) })
            {
                if (string.IsNullOrEmpty(dir)) continue;
                foreach (var exeName in title.ExeNames)
                {
                    var exe = Path.Combine(dir, exeName);
                    if (File.Exists(exe)) return exe;
                }
            }
        }
        return null;
    }

    private static IEnumerable<string> CandidateDirectories(KnownTitle title, IReadOnlyCollection<UninstallEntry> uninstall)
    {
        foreach (var entry in uninstall)
        {
            var matches =
                title.UninstallKeys.Contains(entry.KeyName, StringComparer.OrdinalIgnoreCase) ||
                title.UninstallNames.Contains(entry.DisplayName, StringComparer.OrdinalIgnoreCase);
            if (!matches) continue;

            if (UninstallRegistry.CleanDirectory(entry.InstallLocation) is { } location)
                yield return location;

            // Sans InstallLocation, la commande de désinstallation cite souvent l'exécutable du jeu.
            var quoted = QuotedPath().Match(entry.UninstallString);
            if (quoted.Success && UninstallRegistry.CleanDirectory(Path.GetDirectoryName(quoted.Groups[1].Value)) is { } fromCommand)
                yield return fromCommand;
        }

        foreach (var (subKey, value) in title.Registry)
        {
            if (UninstallRegistry.CleanDirectory(ScanHelpers.ReadRegistryString(RegistryHive.CurrentUser, subKey, value)) is { } fromRegistry)
                yield return fromRegistry;
        }

        foreach (var raw in title.DefaultDirectories)
        {
            if (UninstallRegistry.CleanDirectory(Environment.ExpandEnvironmentVariables(raw)) is { } fallback)
                yield return fallback;
        }
    }

    [GeneratedRegex("^\\s*\"([^\"]+\\.exe)\"", RegexOptions.IgnoreCase)]
    private static partial Regex QuotedPath();
}
