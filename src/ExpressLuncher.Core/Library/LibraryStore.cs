using System.Text.Json;
using System.Text.Json.Serialization;
using ExpressLuncher.Core.Models;
using ExpressLuncher.Core.Scanning;

namespace ExpressLuncher.Core.Library;

/// <summary>Bilan d'une fusion scan → bibliothèque.</summary>
public sealed record MergeResult(IReadOnlyList<Game> Added, IReadOnlyList<Game> Removed, int Updated);

/// <summary>
/// La bibliothèque de jeux, persistée en JSON (%AppData%\ExpressLuncher\library.json).
/// Non thread-safe pour les modifications : à manipuler depuis un seul thread (le thread UI).
/// </summary>
public sealed class LibraryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;
    private readonly List<Game> _games = [];

    public LibraryStore(string? filePath = null) => _filePath = filePath ?? AppInfo.LibraryFile;

    public IReadOnlyList<Game> Games => _games;

    /// <summary>Charge la bibliothèque. Un fichier absent ou illisible donne une bibliothèque vide.</summary>
    public void Load()
    {
        _games.Clear();
        try
        {
            if (!File.Exists(_filePath)) return;
            var loaded = JsonSerializer.Deserialize<List<Game>>(File.ReadAllText(_filePath), JsonOptions);
            if (loaded is not null)
                _games.AddRange(loaded.Where(g => !string.IsNullOrWhiteSpace(g.Id)));
        }
        catch
        {
            // Fichier corrompu : on le met de côté plutôt que de l'écraser silencieusement.
            try { File.Copy(_filePath, _filePath + ".bak", overwrite: true); } catch { /* ignoré */ }
        }
    }

    /// <summary>Écrit la bibliothèque de façon atomique (fichier temporaire puis remplacement).</summary>
    public void Save()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = _filePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_games, JsonOptions));
        File.Move(temp, _filePath, overwrite: true);
    }

    /// <summary>
    /// Fusionne un scan : ajoute les nouveaux jeux, met à jour les champs de détection des jeux connus
    /// (favoris, masquage et historique sont conservés), et retire les jeux désinstallés — uniquement
    /// pour les boutiques scannées sans erreur. Les jeux ajoutés à la main ne sont jamais retirés.
    /// </summary>
    public MergeResult Merge(ScanResult scan)
    {
        var added = new List<Game>();
        var removed = new List<Game>();
        var updated = 0;

        var byId = _games.ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var found in scan.Games)
        {
            if (!seen.Add(found.Id)) continue;

            if (byId.TryGetValue(found.Id, out var existing))
            {
                existing.Name = found.Name;
                existing.Source = found.Source;
                existing.InstallPath = found.InstallPath;
                existing.ExecutablePath = found.ExecutablePath;
                existing.LaunchUri = found.LaunchUri;
                existing.LaunchArguments = found.LaunchArguments;
                existing.SizeBytes = found.SizeBytes ?? existing.SizeBytes;
                existing.CoverPath = found.CoverPath ?? existing.CoverPath;
                existing.HeroPath = found.HeroPath ?? existing.HeroPath;
                existing.LogoPath = found.LogoPath ?? existing.LogoPath;
                existing.StorePlaytimeSeconds = found.StorePlaytimeSeconds ?? existing.StorePlaytimeSeconds;
                existing.StoreLastPlayed = found.StoreLastPlayed ?? existing.StoreLastPlayed;
                if (found.StoreLastPlayed > (existing.LastPlayed ?? DateTime.MinValue))
                    existing.LastPlayed = found.StoreLastPlayed;
                updated++;
            }
            else
            {
                found.AddedAt = DateTime.Now;
                found.LastPlayed = found.StoreLastPlayed;
                _games.Add(found);
                added.Add(found);
            }
        }

        for (var i = _games.Count - 1; i >= 0; i--)
        {
            var game = _games[i];
            if (game.Source == GameSource.Manual) continue;
            if (seen.Contains(game.Id)) continue;
            if (!scan.CompletedSources.Contains(game.Source)) continue;

            removed.Add(game);
            _games.RemoveAt(i);
        }

        return new MergeResult(added, removed, updated);
    }

    /// <summary>Ajoute un jeu à la main à partir de son .exe. Retourne le jeu existant s'il y est déjà.</summary>
    public Game AddManual(string executablePath, string? name = null)
    {
        var fullPath = Path.GetFullPath(executablePath);
        var id = ScanHelpers.MakeId(GameSource.Manual, fullPath.ToLowerInvariant());

        var existing = _games.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        var game = new Game
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(fullPath) : name.Trim(),
            Source = GameSource.Manual,
            ExecutablePath = fullPath,
            InstallPath = Path.GetDirectoryName(fullPath),
            AddedAt = DateTime.Now,
        };
        _games.Add(game);
        return game;
    }

    public bool Remove(Game game) => _games.Remove(game);
}
