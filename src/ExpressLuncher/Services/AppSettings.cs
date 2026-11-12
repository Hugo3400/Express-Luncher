using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExpressLuncher.Core;

namespace ExpressLuncher.Services;

public enum SortMode
{
    Name,
    LastPlayed,
    Playtime,
    RecentlyAdded,
}

/// <summary>Dernière position connue de la fenêtre (en unités WPF).</summary>
public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsMaximized { get; set; }
}

/// <summary>Préférences de l'utilisateur, sérialisées dans settings.json.</summary>
public sealed class AppSettings
{
    public bool ScanOnStartup { get; set; } = true;

    public bool CheckUpdatesOnStartup { get; set; } = true;

    public bool MinimizeOnLaunch { get; set; }

    public bool DownloadArtwork { get; set; } = true;

    public SortMode SortMode { get; set; } = SortMode.Name;

    public WindowPlacement? Window { get; set; }
}

/// <summary>Charge et enregistre les préférences. Un fichier absent ou corrompu donne les valeurs par défaut.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;

    public SettingsStore(string? filePath = null) => _filePath = filePath ?? AppInfo.SettingsFile;

    public AppSettings Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(_filePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_filePath), JsonOptions) ?? new AppSettings();
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    /// <summary>Écriture atomique ; un échec (disque plein, fichier verrouillé) est sans gravité et ignoré.</summary>
    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, JsonOptions));
            File.Move(temp, _filePath, overwrite: true);
        }
        catch
        {
            // ignoré
        }
    }
}
