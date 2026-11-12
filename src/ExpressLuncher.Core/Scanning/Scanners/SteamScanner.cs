using System.Globalization;
using System.Text;
using ExpressLuncher.Core.Models;
using Microsoft.Win32;

namespace ExpressLuncher.Core.Scanning.Scanners;

/// <summary>
/// Jeux Steam : lit libraryfolders.vdf pour trouver les bibliothèques, puis les appmanifest_*.acf de chacune.
/// Temps de jeu et illustrations viennent des données locales du client (localconfig.vdf, librarycache).
/// </summary>
public sealed class SteamScanner : IGameScanner
{
    // Outils et redistribuables qui ont un appmanifest sans être des jeux.
    private static readonly HashSet<string> IgnoredAppIds =
    [
        "228980",  // Steamworks Common Redistributables
        "250820",  // SteamVR
        "1070560", // Steam Linux Runtime 1.0 (scout)
        "1391110", // Steam Linux Runtime 2.0 (soldier)
        "1628350", // Steam Linux Runtime 3.0 (sniper)
        "1493710", // Proton Experimental
        "2180100", // Proton Hotfix
        "1826330", // Proton EasyAntiCheat Runtime
        "1161040", // Proton BattlEye Runtime
    ];

    private static readonly string[] IgnoredNamePrefixes =
    [
        "Steamworks Common Redistributables",
        "Steam Linux Runtime",
        "SteamVR",
    ];

    // Suffixes qui distinguent l'outil Proton d'un jeu dont le nom commencerait par "Proton".
    private static readonly string[] ProtonToolSuffixes =
    [
        "Experimental", "Hotfix", "EasyAntiCheat", "BattlEye", "Next", "Beta",
    ];

    // StateFlags (masque de bits) du manifeste.
    private const int StateFullyInstalled = 4;
    private const int StateUpdateMask = 2 | 256 | 512 | 1024;

    public GameSource Source => GameSource.Steam;

    public string DisplayName => "Steam";

    public Task<IReadOnlyList<Game>> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(cancellationToken), cancellationToken);

    private static IReadOnlyList<Game> Scan(CancellationToken cancellationToken)
    {
        var steamRoot = FindSteamRoot();
        if (steamRoot is null) return [];

        var libraries = GetLibraries(steamRoot);

        // Le registre donne le chemin en minuscules : on préfère la casse de libraryfolders.vdf.
        steamRoot = libraries.FirstOrDefault(l => string.Equals(l, steamRoot, StringComparison.OrdinalIgnoreCase))
                    ?? steamRoot;

        var playStats = ReadPlayStats(steamRoot);
        var games = new List<Game>();
        var seenAppIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var library in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string[] manifests;
            try { manifests = Directory.GetFiles(Path.Combine(library, "steamapps"), "appmanifest_*.acf"); }
            catch { continue; }

            foreach (var manifest in manifests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var game = ReadManifest(manifest, library, steamRoot, playStats);
                    if (game is not null && seenAppIds.Add(game.Id)) games.Add(game);
                }
                catch
                {
                    // manifeste illisible ou corrompu : on passe au suivant
                }
            }
        }

        return games;
    }

    // ---- Localisation de Steam et de ses bibliothèques ----

    private static string? FindSteamRoot()
    {
        string?[] candidates =
        [
            ScanHelpers.ReadRegistryString(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            ScanHelpers.ReadRegistryString(RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
            CombineIfSet(Environment.GetEnvironmentVariable("ProgramFiles(x86)"), "Steam"),
            CombineIfSet(Environment.GetEnvironmentVariable("ProgramFiles"), "Steam"),
        ];

        foreach (var candidate in candidates)
        {
            var path = NormalizeDirectory(candidate);
            if (path is not null && Directory.Exists(Path.Combine(path, "steamapps"))) return path;
        }
        return null;
    }

    private static string? CombineIfSet(string? root, string child) =>
        string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, child);

    /// <summary>Chemin absolu, séparateurs Windows, sans séparateur final. Null si invalide.</summary>
    private static string? NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim().Trim('"')));
        }
        catch
        {
            return null;
        }
    }

    private static List<string> GetLibraries(string steamRoot)
    {
        var libraries = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? rawPath)
        {
            var path = NormalizeDirectory(rawPath);
            if (path is null || !Directory.Exists(Path.Combine(path, "steamapps"))) return;
            if (seen.Add(path)) libraries.Add(path);
        }

        string[] indexFiles =
        [
            Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
            Path.Combine(steamRoot, "config", "libraryfolders.vdf"),
        ];

        foreach (var indexFile in indexFiles)
        {
            if (!File.Exists(indexFile)) continue;
            try
            {
                foreach (var section in ParseVdf(ReadText(indexFile)).Children.Values)
                {
                    // Ancien format : "1" "D:\\SteamLibrary"
                    foreach (var (key, value) in section.Values)
                        if (IsDigits(key)) Add(value);

                    // Format actuel : "1" { "path" "D:\\SteamLibrary" ... }
                    foreach (var (key, child) in section.Children)
                        if (IsDigits(key)) Add(child.Get("path"));
                }
            }
            catch
            {
                // index illisible : on garde au moins la bibliothèque principale
            }
        }

        // L'ancien format ne liste pas le dossier de Steam lui-même.
        Add(steamRoot);
        return libraries;
    }

    // ---- Lecture d'un appmanifest ----

    private static Game? ReadManifest(
        string manifestFile, string library, string steamRoot, Dictionary<string, PlayStats> playStats)
    {
        var root = ParseVdf(ReadText(manifestFile));
        if (!root.Children.TryGetValue("AppState", out var app)) return null;

        var appId = app.Get("appid")?.Trim();
        if (string.IsNullOrEmpty(appId))
            appId = Path.GetFileNameWithoutExtension(manifestFile)["appmanifest_".Length..];
        if (!IsDigits(appId)) return null;

        // Très anciens manifestes : le nom est rangé dans UserConfig.
        var name = app.Get("name")?.Trim();
        if (string.IsNullOrEmpty(name) && app.Children.TryGetValue("UserConfig", out var userConfig))
            name = userConfig.Get("name")?.Trim();
        if (string.IsNullOrEmpty(name)) return null;

        if (IsTool(appId, name)) return null;

        var installDir = app.Get("installdir")?.Trim();
        if (string.IsNullOrEmpty(installDir)) return null;

        var installPath = Path.Combine(library, "steamapps", "common", installDir);
        if (!Directory.Exists(installPath)) return null;

        if (int.TryParse(app.Get("StateFlags"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var state)
            && (state & StateFullyInstalled) == 0)
        {
            // Ni installé ni en cours de mise à jour, ou dossier encore vide (premier téléchargement).
            if ((state & StateUpdateMask) == 0 || IsEmptyDirectory(installPath)) return null;
        }

        long? size = long.TryParse(app.Get("SizeOnDisk"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes)
                     && bytes > 0
            ? bytes
            : null;

        var artwork = FindLocalArtwork(steamRoot, appId);
        playStats.TryGetValue(appId, out var stats);

        return new Game
        {
            Id = ScanHelpers.MakeId(GameSource.Steam, appId),
            Name = name,
            Source = GameSource.Steam,
            InstallPath = installPath,
            ExecutablePath = ScanHelpers.FindMainExecutable(installPath, name),
            LaunchUri = $"steam://rungameid/{appId}",
            CoverPath = artwork.Cover
                        ?? $"https://shared.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg",
            HeroPath = artwork.Hero,
            LogoPath = artwork.Logo,
            SizeBytes = size,
            StorePlaytimeSeconds = stats.PlaytimeSeconds,
            StoreLastPlayed = stats.LastPlayed,
        };
    }

    private static bool IsTool(string appId, string name)
    {
        if (IgnoredAppIds.Contains(appId)) return true;

        foreach (var prefix in IgnoredNamePrefixes)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;

        // "Proton 9.0", "Proton Experimental", "Proton - Experimental"...
        if (name.StartsWith("Proton ", StringComparison.OrdinalIgnoreCase))
        {
            var rest = name["Proton ".Length..].TrimStart(' ', '-');
            if (rest.Length > 0 && char.IsDigit(rest[0])) return true;
            foreach (var suffix in ProtonToolSuffixes)
                if (rest.StartsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool IsEmptyDirectory(string path)
    {
        try { return !Directory.EnumerateFileSystemEntries(path).Any(); }
        catch { return true; }
    }

    // ---- Temps de jeu enregistré par le client Steam ----

    private readonly record struct PlayStats(long? PlaytimeSeconds, DateTime? LastPlayed);

    /// <summary>
    /// userdata\&lt;compte&gt;\config\localconfig.vdf → UserLocalConfigStore/Software/Valve/Steam/apps/&lt;appid&gt; :
    /// "Playtime" en minutes, "LastPlayed" en secondes Unix. Dictionnaire vide si rien n'est lisible.
    /// </summary>
    private static Dictionary<string, PlayStats> ReadPlayStats(string steamRoot)
    {
        var stats = new Dictionary<string, PlayStats>(StringComparer.Ordinal);
        try
        {
            var userData = Path.Combine(steamRoot, "userdata");
            if (!Directory.Exists(userData)) return stats;

            // Plusieurs comptes possibles : on prend celui dont la configuration a été écrite en dernier.
            var configFiles = Directory.GetDirectories(userData)
                .Select(dir => new FileInfo(Path.Combine(dir, "config", "localconfig.vdf")))
                .Where(file => file.Exists)
                .OrderByDescending(file => file.LastWriteTimeUtc);

            foreach (var configFile in configFiles)
            {
                try
                {
                    var apps = FindNode(ParseVdf(ReadText(configFile.FullName)),
                        "UserLocalConfigStore", "Software", "Valve", "Steam", "apps");
                    if (apps is null) continue;

                    foreach (var (appId, app) in apps.Children)
                    {
                        var entry = new PlayStats(ReadPlaytimeSeconds(app.Get("Playtime")), ReadUnixTime(app.Get("LastPlayed")));
                        if (entry.PlaytimeSeconds is not null || entry.LastPlayed is not null) stats[appId] = entry;
                    }
                    if (stats.Count > 0) break;
                }
                catch
                {
                    // configuration illisible : on essaie le compte suivant
                }
            }
        }
        catch
        {
            // dossier userdata inaccessible : pas de temps de jeu
        }
        return stats;
    }

    private static VdfNode? FindNode(VdfNode root, params string[] path)
    {
        var node = root;
        foreach (var key in path)
            if (!node.Children.TryGetValue(key, out node)) return null;
        return node;
    }

    private static long? ReadPlaytimeSeconds(string? minutes) =>
        long.TryParse(minutes, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        && value is >= 0 and < int.MaxValue
            ? value * 60
            : null;

    private static DateTime? ReadUnixTime(string? seconds)
    {
        if (!long.TryParse(seconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
            return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(value).LocalDateTime; }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    // ---- Illustrations dans le cache local de Steam ----

    /// <summary>Illustrations d'une application déjà téléchargées par le client Steam.</summary>
    internal sealed record SteamLocalArtwork(string? Cover, string? Hero, string? Logo);

    private static readonly Lazy<string?> KnownSteamRoot = new(FindSteamRoot);

    /// <summary>Cherche dans le cache du client Steam ; null si Steam n'est pas installé.</summary>
    internal static SteamLocalArtwork? FindLocalArtwork(string appId)
    {
        var steamRoot = KnownSteamRoot.Value;
        return steamRoot is null || !IsDigits(appId) ? null : FindLocalArtwork(steamRoot, appId);
    }

    private static SteamLocalArtwork FindLocalArtwork(string steamRoot, string appId)
    {
        string? cover = null, hero = null, logo = null;
        int coverRank = int.MaxValue, heroRank = int.MaxValue, logoRank = int.MaxValue;

        // À rang égal, le premier fichier proposé est conservé.
        void Offer(string file, string fileName)
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);

            if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase))
            {
                Keep(CoverRank(stem), ref coverRank, ref cover);
                Keep(HeroRank(stem), ref heroRank, ref hero);
            }
            else if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                Keep(LogoRank(stem), ref logoRank, ref logo);
            }

            void Keep(int rank, ref int bestRank, ref string? best)
            {
                if (rank >= bestRank) return;
                bestRank = rank;
                best = file;
            }
        }

        try
        {
            var cache = Path.Combine(steamRoot, "appcache", "librarycache");

            // Ancienne disposition : tout à plat, préfixé par l'appid.
            foreach (var name in new[] { "library_600x900.jpg", "library_hero.jpg", "logo.png" })
            {
                var flat = Path.Combine(cache, $"{appId}_{name}");
                if (File.Exists(flat)) Offer(flat, name);
            }

            // Disposition actuelle : un dossier par appid, parfois un sous-dossier nommé par hash,
            // et selon les jeux "library_600x900", "library_capsule" ou une variante localisée.
            var appDir = Path.Combine(cache, appId);
            if (Directory.Exists(appDir))
            {
                foreach (var file in Directory.EnumerateFiles(appDir, "*", SearchOption.AllDirectories))
                    Offer(file, Path.GetFileName(file));
            }
        }
        catch
        {
            // cache illisible : on garde ce qui a déjà été trouvé
        }

        return new SteamLocalArtwork(cover, hero, logo);
    }

    private static int CoverRank(string fileName)
    {
        if (fileName.Equals("library_600x900", StringComparison.OrdinalIgnoreCase)) return 0;
        if (fileName.Equals("library_capsule", StringComparison.OrdinalIgnoreCase)) return 1;
        if (fileName.StartsWith("library_600x900_", StringComparison.OrdinalIgnoreCase)) return 2;
        if (fileName.StartsWith("library_capsule_", StringComparison.OrdinalIgnoreCase)) return 3;
        return int.MaxValue;
    }

    private static int HeroRank(string fileName)
    {
        if (fileName.Equals("library_hero", StringComparison.OrdinalIgnoreCase)) return 0;
        if (fileName.StartsWith("library_hero_blur", StringComparison.OrdinalIgnoreCase)) return int.MaxValue;
        if (fileName.StartsWith("library_hero_", StringComparison.OrdinalIgnoreCase)) return 1;
        return int.MaxValue;
    }

    private static int LogoRank(string fileName)
    {
        if (fileName.Equals("logo", StringComparison.OrdinalIgnoreCase)) return 0;
        if (fileName.StartsWith("logo_", StringComparison.OrdinalIgnoreCase)) return 1;
        return int.MaxValue;
    }

    // ---- Analyseur VDF / ACF (format texte clé-valeur de Valve) ----

    private sealed class VdfNode
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Get(string key) => Values.TryGetValue(key, out var value) ? value : null;
    }

    private enum VdfToken { End, Open, Close, Text }

    /// <summary>
    /// Analyse tolérante : un fichier tronqué ou mal équilibré donne ce qui a pu être lu, sans exception.
    /// </summary>
    private static VdfNode ParseVdf(string text)
    {
        var root = new VdfNode();
        var stack = new Stack<VdfNode>();
        var current = root;
        string? pendingKey = null;
        var pos = 0;

        while (true)
        {
            var kind = NextToken(text, ref pos, out var token);
            if (kind == VdfToken.End) break;

            switch (kind)
            {
                case VdfToken.Open:
                    // Un bloc sans clé est lu mais rattaché à rien.
                    var child = new VdfNode();
                    if (pendingKey is not null) current.Children[pendingKey] = child;
                    stack.Push(current);
                    current = child;
                    pendingKey = null;
                    break;

                case VdfToken.Close:
                    pendingKey = null;
                    if (stack.Count > 0) current = stack.Pop();
                    break;

                default:
                    if (pendingKey is null)
                    {
                        pendingKey = token;
                    }
                    else
                    {
                        current.Values[pendingKey] = token;
                        pendingKey = null;
                    }
                    break;
            }
        }

        return root;
    }

    private static VdfToken NextToken(string text, ref int pos, out string token)
    {
        token = "";

        while (true)
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
            if (pos >= text.Length) return VdfToken.End;

            var c = text[pos];

            // Commentaire jusqu'à la fin de la ligne.
            if (c == '/' && pos + 1 < text.Length && text[pos + 1] == '/')
            {
                while (pos < text.Length && text[pos] != '\n') pos++;
                continue;
            }

            if (c == '{') { pos++; return VdfToken.Open; }
            if (c == '}') { pos++; return VdfToken.Close; }

            if (c == '"')
            {
                pos++;
                token = ReadQuoted(text, ref pos);
                return VdfToken.Text;
            }

            var start = pos;
            while (pos < text.Length && !char.IsWhiteSpace(text[pos]) && text[pos] is not ('{' or '}' or '"')) pos++;

            // Condition de plateforme ("[$WIN32]") : sans intérêt ici.
            if (c == '[' && text[pos - 1] == ']') continue;

            token = text[start..pos];
            return VdfToken.Text;
        }
    }

    private static string ReadQuoted(string text, ref int pos)
    {
        var sb = new StringBuilder();
        while (pos < text.Length)
        {
            var c = text[pos++];
            if (c == '"') break;

            if (c == '\\' && pos < text.Length)
            {
                var next = text[pos++];
                switch (next)
                {
                    case '\\': sb.Append('\\'); break;
                    case '"': sb.Append('"'); break;
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    default: sb.Append('\\').Append(next); break;
                }
                continue;
            }

            sb.Append(c);
        }
        return sb.ToString();
    }

    // ---- Divers ----

    /// <summary>Lit un fichier même si Steam l'a ouvert en écriture.</summary>
    private static string ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static bool IsDigits(string text)
    {
        if (text.Length == 0) return false;
        foreach (var c in text)
            if (c is < '0' or > '9') return false;
        return true;
    }
}
