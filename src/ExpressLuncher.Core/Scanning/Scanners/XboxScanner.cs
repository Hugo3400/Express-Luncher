using System.Text;
using System.Xml.Linq;
using ExpressLuncher.Core.Models;
using Microsoft.Win32;

namespace ExpressLuncher.Core.Scanning.Scanners;

/// <summary>
/// Xbox / Game Pass PC : les jeux installés par l'app Xbox vivent dans &lt;lecteur&gt;:\XboxGames\&lt;Jeu&gt;\Content
/// (dossier indiqué par le fichier .GamingRoot à la racine du lecteur). Le manifeste appx donne l'identité
/// du paquet ; le lancement passe par shell:AppsFolder, ce qui exige que le paquet soit enregistré pour
/// l'utilisateur courant (sinon le jeu est ignoré).
/// </summary>
public sealed class XboxScanner : IGameScanner
{
    private const string PackagesKey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public GameSource Source => GameSource.Xbox;

    public string DisplayName => "Xbox";

    public Task<IReadOnlyList<Game>> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Scan(cancellationToken));

    private static IReadOnlyList<Game> Scan(CancellationToken cancellationToken)
    {
        var games = new List<Game>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<(string FullName, string RootFolder)>? packages = null;

        foreach (var root in FindGamingRoots())
        {
            string[] gameDirs;
            try { gameDirs = Directory.GetDirectories(root); }
            catch { continue; }

            foreach (var gameDir in gameDirs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var contentDir = Path.Combine(gameDir, "Content");
                    var manifest = Path.Combine(contentDir, "appxmanifest.xml");
                    if (!File.Exists(manifest)) continue;

                    packages ??= ReadRegisteredPackages();
                    var game = ReadGame(gameDir, contentDir, manifest, packages);
                    if (game is not null && seen.Add(game.Id)) games.Add(game);
                }
                catch
                {
                    // jeu illisible : ignoré
                }
            }
        }

        return games;
    }

    // .GamingRoot : "RGBX", un compteur sur 4 octets, puis le(s) dossier(s) relatif(s) en UTF-16 terminé(s) par \0.
    private static IEnumerable<string> FindGamingRoots()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { yield break; }

        foreach (var drive in drives)
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;

            var folders = new List<string>();
            try
            {
                var marker = Path.Combine(drive.Name, ".GamingRoot");
                if (!File.Exists(marker)) continue;

                var bytes = File.ReadAllBytes(marker);
                if (bytes.Length > 8 && bytes.Length < 4096 && Encoding.ASCII.GetString(bytes, 0, 4) == "RGBX")
                {
                    var names = Encoding.Unicode.GetString(bytes, 8, bytes.Length - 8)
                        .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var name in names)
                    {
                        var folder = Path.GetFullPath(Path.Combine(drive.Name, name.TrimStart('\\', '/')));
                        if (folder.StartsWith(drive.Name, StringComparison.OrdinalIgnoreCase)) folders.Add(folder);
                    }
                }

                if (folders.Count == 0) folders.Add(Path.Combine(drive.Name, "XboxGames"));
            }
            catch
            {
                continue;
            }

            foreach (var folder in folders)
                if (Directory.Exists(folder)) yield return folder;
        }
    }

    // Paquets enregistrés pour l'utilisateur courant : nom complet (Nom_Version_Arch_Ressource_Éditeur) et dossier.
    private static List<(string FullName, string RootFolder)> ReadRegisteredPackages()
    {
        var packages = new List<(string, string)>();

        foreach (var key in ScanHelpers.OpenRegistryKeys(RegistryHive.CurrentUser, PackagesKey))
        {
            using (key)
            {
                string[] names;
                try { names = key.GetSubKeyNames(); }
                catch { continue; }

                foreach (var name in names)
                {
                    try
                    {
                        using var package = key.OpenSubKey(name);
                        packages.Add((name, package?.GetValue("PackageRootFolder")?.ToString() ?? ""));
                    }
                    catch
                    {
                        // paquet illisible : ignoré
                    }
                }
            }

            if (packages.Count > 0) break;
        }

        return packages;
    }

    private static Game? ReadGame(
        string gameDir, string contentDir, string manifestPath, List<(string FullName, string RootFolder)> packages)
    {
        var manifest = XDocument.Load(manifestPath).Root;
        if (manifest is null) return null;

        var identityName = Child(manifest, "Identity")?.Attribute("Name")?.Value.Trim();
        if (string.IsNullOrEmpty(identityName)) return null;

        var applications = Child(manifest, "Applications")?.Elements()
            .Where(e => e.Name.LocalName == "Application")
            .ToList();
        var application = applications?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Attribute("Id")?.Value));
        var applicationId = application?.Attribute("Id")?.Value.Trim();
        if (application is null || string.IsNullOrEmpty(applicationId)) return null;

        var familyName = FindPackageFamilyName(identityName, contentDir, packages);
        if (familyName is null) return null;

        XElement? config = null;
        try
        {
            var configPath = Path.Combine(contentDir, "MicrosoftGame.config");
            if (File.Exists(configPath)) config = XDocument.Load(configPath).Root;
        }
        catch
        {
            // config facultative
        }

        var name = FirstLiteral(
            Child(Child(manifest, "Properties"), "DisplayName")?.Value,
            Child(application, "VisualElements")?.Attribute("DisplayName")?.Value,
            Child(config, "ShellVisuals")?.Attribute("DefaultDisplayName")?.Value,
            Path.GetFileName(gameDir));
        if (name is null) return null;

        return new Game
        {
            Id = ScanHelpers.MakeId(GameSource.Xbox, identityName),
            Name = name,
            Source = GameSource.Xbox,
            InstallPath = contentDir,
            ExecutablePath = FindExecutable(contentDir, application, config, name),
            LaunchUri = $@"shell:AppsFolder\{familyName}!{applicationId}",
        };
    }

    // Nom de famille = Nom_Éditeur, déduit du nom complet du paquet enregistré (le nom ne contient jamais "_").
    private static string? FindPackageFamilyName(
        string identityName, string contentDir, List<(string FullName, string RootFolder)> packages)
    {
        var prefix = identityName + "_";
        var matches = packages.Where(p => p.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return null;

        var best = matches.FirstOrDefault(p => SamePath(p.RootFolder, contentDir));
        var fullName = best.FullName ?? matches[0].FullName;

        var publisherId = fullName[(fullName.LastIndexOf('_') + 1)..];
        return publisherId.Length == 0 ? null : $"{identityName}_{publisherId}";
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            UninstallRegistry.CleanDirectory(left), UninstallRegistry.CleanDirectory(right), StringComparison.OrdinalIgnoreCase);

    // Sert surtout à l'icône : le lancement passe par LaunchUri. GameLaunchHelper.exe n'est qu'un relais.
    private static string? FindExecutable(string contentDir, XElement application, XElement? config, string name)
    {
        var declared = new List<string?>();
        var executableList = Child(config, "ExecutableList");
        if (executableList is not null)
            declared.AddRange(executableList.Elements().Select(e => e.Attribute("Name")?.Value));
        declared.Add(application.Attribute("Executable")?.Value);

        foreach (var relative in declared)
        {
            if (string.IsNullOrWhiteSpace(relative)) continue;
            if (Path.GetFileName(relative).Equals("GameLaunchHelper.exe", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var path = Path.GetFullPath(Path.Combine(contentDir, relative.Trim().TrimStart('\\', '/')));
                if (path.StartsWith(contentDir + '\\', StringComparison.OrdinalIgnoreCase) && File.Exists(path)) return path;
            }
            catch
            {
                // chemin invalide : on essaie le suivant
            }
        }

        return ScanHelpers.FindMainExecutable(contentDir, name);
    }

    private static XElement? Child(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    // Premier nom utilisable : ni vide, ni référence de ressource ("ms-resource:...") non résolue.
    private static string? FirstLiteral(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var value = candidate?.Trim();
            if (string.IsNullOrEmpty(value)) continue;
            if (value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("@{", StringComparison.Ordinal))
                continue;
            return value;
        }
        return null;
    }
}
