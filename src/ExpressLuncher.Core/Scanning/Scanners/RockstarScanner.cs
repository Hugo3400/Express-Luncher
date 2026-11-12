using ExpressLuncher.Core.Models;
using Microsoft.Win32;

namespace ExpressLuncher.Core.Scanning.Scanners;

/// <summary>
/// Rockstar Games Launcher : un jeu par sous-clé de HKLM\SOFTWARE\Rockstar Games (valeur InstallFolder),
/// complété par les entrées Uninstall publiées par Rockstar Games. Lancement par l'exécutable, qui
/// démarre lui-même le launcher.
/// </summary>
public sealed class RockstarScanner : IGameScanner
{
    private const string RockstarKey = @"SOFTWARE\Rockstar Games";

    // le launcher et ses composants ne sont pas des jeux
    private static readonly HashSet<string> ExcludedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Launcher",
        "Rockstar Games Launcher",
        "Social Club",
        "Rockstar Games Social Club",
        "Rockstar Games SDK",
    };

    public GameSource Source => GameSource.Rockstar;

    public string DisplayName => "Rockstar Games";

    public Task<IReadOnlyList<Game>> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Scan(cancellationToken));

    private sealed class Candidate
    {
        public string? RegistryTitle;
        public string? UninstallKey;
        public string? UninstallName;
    }

    private static IReadOnlyList<Game> Scan(CancellationToken cancellationToken)
    {
        // dossier d'installation -> infos fusionnées des deux sources
        var candidates = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        CollectFromRegistry(candidates);
        CollectFromUninstall(candidates);

        var games = new List<Game>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (directory, candidate) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var game = BuildGame(directory, candidate);
                if (game is not null && seen.Add(game.Id)) games.Add(game);
            }
            catch
            {
                // jeu illisible : ignoré
            }
        }

        return games;
    }

    private static void CollectFromRegistry(Dictionary<string, Candidate> candidates)
    {
        foreach (var root in ScanHelpers.OpenRegistryKeys(RegistryHive.LocalMachine, RockstarKey))
        {
            using (root)
            {
                string[] titles;
                try { titles = root.GetSubKeyNames(); }
                catch { continue; }

                foreach (var title in titles)
                {
                    if (ExcludedNames.Contains(title)) continue;
                    try
                    {
                        using var key = root.OpenSubKey(title);
                        var directory = UninstallRegistry.CleanDirectory(key?.GetValue("InstallFolder")?.ToString());
                        if (directory is not null) GetCandidate(candidates, directory).RegistryTitle ??= title;
                    }
                    catch
                    {
                        // clé illisible : ignorée
                    }
                }
            }
        }
    }

    private static void CollectFromUninstall(Dictionary<string, Candidate> candidates)
    {
        foreach (var entry in UninstallRegistry.ReadAll())
        {
            if (!entry.Publisher.Equals("Rockstar Games", StringComparison.OrdinalIgnoreCase)) continue;
            if (ExcludedNames.Contains(entry.KeyName) || ExcludedNames.Contains(entry.DisplayName)) continue;

            var directory = UninstallRegistry.CleanDirectory(entry.InstallLocation);
            if (directory is null) continue;

            var candidate = GetCandidate(candidates, directory);
            candidate.UninstallKey ??= entry.KeyName;
            if (entry.DisplayName.Length > 0) candidate.UninstallName ??= entry.DisplayName;
        }
    }

    private static Candidate GetCandidate(Dictionary<string, Candidate> candidates, string directory)
    {
        if (!candidates.TryGetValue(directory, out var candidate)) candidates[directory] = candidate = new Candidate();
        return candidate;
    }

    private static Game? BuildGame(string directory, Candidate candidate)
    {
        if (!Directory.Exists(directory)) return null;
        if (UninstallRegistry.BelongsToAnotherStore(directory)) return null;

        var folderName = Path.GetFileName(directory);
        if (ExcludedNames.Contains(folderName)) return null;

        var name = candidate.UninstallName ?? candidate.RegistryTitle ?? folderName;
        var key = candidate.RegistryTitle ?? candidate.UninstallKey ?? folderName;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(key)) return null;

        var executable = FindPlayExecutable(directory) ?? ScanHelpers.FindMainExecutable(directory, name);
        if (executable is null) return null;

        return new Game
        {
            Id = ScanHelpers.MakeId(GameSource.Rockstar, key),
            Name = name,
            Source = GameSource.Rockstar,
            InstallPath = directory,
            ExecutablePath = executable,
        };
    }

    // "PlayGTAV.exe", "PlayRDR2.exe"... : le point d'entrée que visent les raccourcis créés par le launcher.
    private static string? FindPlayExecutable(string directory)
    {
        try
        {
            var matches = Directory.GetFiles(directory, "Play*.exe");
            return matches.Length == 1 ? matches[0] : null;
        }
        catch
        {
            return null;
        }
    }
}
