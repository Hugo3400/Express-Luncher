using System.Text.Json;
using ExpressLuncher.Core.Models;
using Microsoft.Win32;

namespace ExpressLuncher.Core.Scanning.Scanners;

/// <summary>
/// Jeux Epic Games : un fichier JSON ".item" par installation dans le dossier Manifests du lanceur.
/// </summary>
public sealed class EpicScanner : IGameScanner
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public GameSource Source => GameSource.Epic;

    public string DisplayName => "Epic Games";

    public Task<IReadOnlyList<Game>> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(cancellationToken), cancellationToken);

    private static IReadOnlyList<Game> Scan(CancellationToken cancellationToken)
    {
        var games = new List<Game>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var manifestDir in GetManifestDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();

            string[] files;
            try { files = Directory.GetFiles(manifestDir, "*.item"); }
            catch { continue; }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var game = ReadManifest(file);
                    if (game is not null && seenIds.Add(game.Id)) games.Add(game);
                }
                catch
                {
                    // manifeste illisible ou JSON invalide : on passe au suivant
                }
            }
        }

        return games;
    }

    private static List<string> GetManifestDirectories()
    {
        var directories = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? dataDir)
        {
            if (string.IsNullOrWhiteSpace(dataDir)) return;
            try
            {
                var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(dataDir.Trim(), "Manifests")));
                if (Directory.Exists(path) && seen.Add(path)) directories.Add(path);
            }
            catch
            {
                // chemin invalide : ignoré
            }
        }

        // Le lanceur publie son dossier de données ; sinon emplacement par défaut.
        Add(ScanHelpers.ReadRegistryString(
            RegistryHive.LocalMachine, @"SOFTWARE\Epic Games\EpicGamesLauncher", "AppDataPath"));

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrEmpty(programData))
            Add(Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data"));

        return directories;
    }

    private static Game? ReadManifest(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(stream, JsonOptions);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        var appName = GetString(root, "AppName");
        var name = GetString(root, "DisplayName");
        if (appName is null || name is null) return null;

        if (GetBool(root, "bIsIncompleteInstall") == true) return null;
        if (!IsGame(root, appName)) return null;

        var installPath = GetString(root, "InstallLocation");
        if (installPath is null) return null;
        installPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installPath));
        if (!Directory.Exists(installPath)) return null;

        var size = GetLong(root, "InstallSize");

        return new Game
        {
            Id = ScanHelpers.MakeId(GameSource.Epic, appName),
            Name = name,
            Source = GameSource.Epic,
            InstallPath = installPath,
            ExecutablePath = ResolveExecutable(installPath, GetString(root, "LaunchExecutable"))
                             ?? ScanHelpers.FindMainExecutable(installPath, name),
            LaunchUri = BuildLaunchUri(appName, GetString(root, "CatalogNamespace"), GetString(root, "CatalogItemId")),
            CoverPath = null,
            SizeBytes = size > 0 ? size : null,
        };
    }

    /// <summary>Écarte DLC, extensions, moteur Unreal et autres contenus qui ne sont pas des jeux.</summary>
    private static bool IsGame(JsonElement root, string appName)
    {
        // Un DLC pointe vers le jeu auquel il appartient.
        var mainGame = GetString(root, "MainGameAppName");
        if (mainGame is not null && !string.Equals(mainGame, appName, StringComparison.OrdinalIgnoreCase))
            return false;

        // Versions d'Unreal Engine : "UE_5.4"...
        if (appName.StartsWith("UE_", StringComparison.OrdinalIgnoreCase)) return false;

        if (root.TryGetProperty("AppCategories", out var categories)
            && categories.ValueKind == JsonValueKind.Array
            && categories.GetArrayLength() > 0)
        {
            var isGame = false;
            foreach (var category in categories.EnumerateArray())
            {
                if (category.ValueKind != JsonValueKind.String) continue;
                var value = category.GetString();
                if (string.Equals(value, "engines", StringComparison.OrdinalIgnoreCase)) return false;
                if (string.Equals(value, "games", StringComparison.OrdinalIgnoreCase)) isGame = true;
            }
            return isGame;
        }

        // Pas de catégories (anciens manifestes) : on se fie au drapeau "application".
        return GetBool(root, "bIsApplication") != false;
    }

    private static string? ResolveExecutable(string installPath, string? launchExecutable)
    {
        if (launchExecutable is null) return null;

        // Souvent écrit avec des "/" et parfois un séparateur en tête.
        var relative = launchExecutable.Replace('/', '\\').TrimStart('\\');
        if (relative.Length == 0) return null;

        var full = Path.GetFullPath(Path.Combine(installPath, relative));
        return File.Exists(full) ? full : null;
    }

    private static string BuildLaunchUri(string appName, string? catalogNamespace, string? catalogItemId)
    {
        var target = catalogNamespace is not null && catalogItemId is not null
            ? $"{Uri.EscapeDataString(catalogNamespace)}%3A{Uri.EscapeDataString(catalogItemId)}%3A{Uri.EscapeDataString(appName)}"
            : Uri.EscapeDataString(appName);
        return $"com.epicgames.launcher://apps/{target}?action=launch&silent=true";
    }

    // ---- Lecture JSON tolérante aux types inattendus ----

    private static string? GetString(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static bool? GetBool(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static long? GetLong(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }
}
