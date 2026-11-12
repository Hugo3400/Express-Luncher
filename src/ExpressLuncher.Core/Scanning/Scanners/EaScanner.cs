using System.Xml.Linq;
using ExpressLuncher.Core.Models;
using Microsoft.Win32;

namespace ExpressLuncher.Core.Scanning.Scanners;

/// <summary>
/// EA app / Origin : les fiches de l'EA app sont chiffrées, on repère donc les dossiers de jeux via le
/// registre, les entrées Uninstall et les bibliothèques par défaut. Un dossier n'est retenu que s'il
/// contient "__Installer" (posé par EA), dont installerdata.xml donne le titre et l'exécutable.
/// Lancement par l'exécutable : le jeu démarre lui-même l'EA app si nécessaire.
/// </summary>
public sealed class EaScanner : IGameScanner
{
    private static readonly string[] RegistryRoots = [@"SOFTWARE\EA Games", @"SOFTWARE\Electronic Arts"];
    private static readonly string[] ClientExecutables = ["EADesktop.exe", "EALauncher.exe", "Origin.exe"];

    public GameSource Source => GameSource.EA;

    public string DisplayName => "EA app";

    public Task<IReadOnlyList<Game>> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Scan(cancellationToken));

    private static IReadOnlyList<Game> Scan(CancellationToken cancellationToken)
    {
        // dossier d'installation -> nom indicatif (peut être null)
        var candidates = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        CollectFromRegistry(candidates);
        CollectFromUninstall(candidates);
        CollectFromLibraryFolders(candidates);

        var games = new List<Game>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (directory, hintName) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var game = ReadGame(directory, hintName);
                if (game is not null && seen.Add(game.Id)) games.Add(game);
            }
            catch
            {
                // jeu illisible : ignoré
            }
        }

        return games;
    }

    private static void AddCandidate(Dictionary<string, string?> candidates, string? rawDirectory, string? hintName)
    {
        var directory = UninstallRegistry.CleanDirectory(rawDirectory);
        if (directory is null) return;

        if (!candidates.TryGetValue(directory, out var existing) || string.IsNullOrWhiteSpace(existing))
            candidates[directory] = string.IsNullOrWhiteSpace(hintName) ? null : hintName.Trim();
    }

    // HKLM\SOFTWARE\EA Games\<jeu> et HKLM\SOFTWARE\Electronic Arts\[<groupe>\]<jeu> : valeur "Install Dir".
    private static void CollectFromRegistry(Dictionary<string, string?> candidates)
    {
        foreach (var rootPath in RegistryRoots)
        {
            foreach (var root in ScanHelpers.OpenRegistryKeys(RegistryHive.LocalMachine, rootPath))
            {
                using (root) CollectFromKey(root, candidates, depth: 2);
            }
        }
    }

    private static void CollectFromKey(RegistryKey parent, Dictionary<string, string?> candidates, int depth)
    {
        string[] names;
        try { names = parent.GetSubKeyNames(); }
        catch { return; }

        foreach (var name in names)
        {
            try
            {
                using var key = parent.OpenSubKey(name);
                if (key is null) continue;

                AddCandidate(candidates, key.GetValue("Install Dir")?.ToString(), name);
                if (depth > 1) CollectFromKey(key, candidates, depth - 1);
            }
            catch
            {
                // clé illisible : ignorée
            }
        }
    }

    private static void CollectFromUninstall(Dictionary<string, string?> candidates)
    {
        foreach (var entry in UninstallRegistry.ReadAll())
        {
            if (entry.InstallLocation.Length == 0) continue;

            var isEa = entry.Publisher.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase)
                || entry.UninstallString.Contains("EAInstaller", StringComparison.OrdinalIgnoreCase)
                || entry.UninstallString.Contains("__Installer", StringComparison.OrdinalIgnoreCase);
            if (isEa) AddCandidate(candidates, entry.InstallLocation, entry.DisplayName);
        }
    }

    // Bibliothèques par défaut de l'EA app et d'Origin, plus celle choisie par l'utilisateur dans l'EA app.
    private static void CollectFromLibraryFolders(Dictionary<string, string?> candidates)
    {
        var libraries = new List<string?>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EA Games"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Origin Games"),
        };

        try
        {
            var settingsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Electronic Arts", "EA Desktop");
            if (Directory.Exists(settingsDir))
            {
                foreach (var file in Directory.GetFiles(settingsDir, "user_*.ini"))
                {
                    foreach (var line in File.ReadLines(file))
                    {
                        const string key = "user.downloadinplacedir=";
                        if (line.StartsWith(key, StringComparison.OrdinalIgnoreCase)) libraries.Add(line[key.Length..]);
                    }
                }
            }
        }
        catch
        {
            // réglages illisibles : on garde les bibliothèques par défaut
        }

        foreach (var library in libraries)
        {
            var directory = UninstallRegistry.CleanDirectory(library);
            if (directory is null || !Directory.Exists(directory)) continue;

            try
            {
                foreach (var gameDir in Directory.GetDirectories(directory)) AddCandidate(candidates, gameDir, null);
            }
            catch
            {
                // bibliothèque inaccessible : ignorée
            }
        }
    }

    private static Game? ReadGame(string directory, string? hintName)
    {
        if (!Directory.Exists(Path.Combine(directory, "__Installer"))) return null;
        if (UninstallRegistry.BelongsToAnotherStore(directory)) return null;
        if (ClientExecutables.Any(exe => File.Exists(Path.Combine(directory, exe)))) return null;

        var data = ReadInstallerData(directory);
        var folderName = Path.GetFileName(directory);

        var name = data.Title ?? hintName ?? folderName;
        if (string.IsNullOrWhiteSpace(name)) return null;

        var executable = data.Executable ?? ScanHelpers.FindMainExecutable(directory, name);
        if (executable is null) return null;

        return new Game
        {
            Id = ScanHelpers.MakeId(GameSource.EA, data.ContentId ?? folderName),
            Name = name,
            Source = GameSource.EA,
            InstallPath = directory,
            ExecutablePath = executable,
        };
    }

    // Manifeste d'installation EA : titres localisés, contentIDs et lanceurs (<runtime><launcher><filePath>).
    private static (string? Title, string? ContentId, string? Executable) ReadInstallerData(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "__Installer", "installerdata.xml");
            if (!File.Exists(path)) return default;

            var root = XDocument.Load(path).Root;
            if (root is null) return default;

            var titles = Named(root.Descendants(), "gameTitle").ToList();
            if (titles.Count == 0)
                titles = Named(root.Descendants(), "localeInfo").SelectMany(info => Named(info.Elements(), "title")).ToList();

            var title = titles.FirstOrDefault(IsEnglish) ?? titles.FirstOrDefault();
            var contentId = Named(root.Descendants(), "contentID").Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);

            return (CleanTitle(title?.Value), contentId, FindLauncher(root, directory));
        }
        catch
        {
            return default;
        }
    }

    private static IEnumerable<XElement> Named(IEnumerable<XElement> elements, string localName) =>
        elements.Where(e => string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    private static bool IsEnglish(XElement element) =>
        string.Equals(
            (element.Attribute("locale") ?? element.Parent?.Attribute("locale"))?.Value,
            "en_US",
            StringComparison.OrdinalIgnoreCase);

    private static string? CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var cleaned = title.Replace("™", "").Replace("®", "").Replace("©", "").Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    // filePath a la forme "[HKEY_LOCAL_MACHINE\...\Install Dir]sous\chemin\jeu.exe" : on garde ce qui suit "]".
    private static string? FindLauncher(XElement root, string directory)
    {
        string? fallback = null;

        foreach (var launcher in Named(root.Descendants(), "launcher"))
        {
            var filePath = Named(launcher.Elements(), "filePath").FirstOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(filePath)) continue;
            if (Flag(launcher, "trial")) continue;

            var relative = filePath[(filePath.LastIndexOf(']') + 1)..].Trim().Replace('/', '\\').TrimStart('\\');
            if (relative.Length == 0 || !relative.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

            var fullPath = Path.GetFullPath(Path.Combine(directory, relative));
            if (!fullPath.StartsWith(directory + '\\', StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath)) continue;

            // version 64 bits d'abord quand le jeu propose les deux
            if (Flag(launcher, "requires64BitOS")) return fullPath;
            fallback ??= fullPath;
        }

        return fallback;
    }

    private static bool Flag(XElement launcher, string localName) =>
        Named(launcher.Elements(), localName).FirstOrDefault()?.Value.Trim() == "1";
}
