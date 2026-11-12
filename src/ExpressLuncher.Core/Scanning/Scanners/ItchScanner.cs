using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExpressLuncher.Core.Models;

namespace ExpressLuncher.Core.Scanning.Scanners;

/// <summary>
/// itch.io : la base de l'app (butler.db) est en SQLite, on parcourt donc l'emplacement d'installation
/// par défaut %AppData%\itch\apps, un jeu par sous-dossier. Le titre vient du reçu d'installation
/// (.itch\receipt.json.gz) quand il existe, sinon du nom du dossier. Lancement par l'exécutable.
/// </summary>
public sealed class ItchScanner : IGameScanner
{
    private static readonly Regex Separators = new(@"[-_\s]+", RegexOptions.CultureInvariant);

    public GameSource Source => GameSource.Itch;

    public string DisplayName => "itch.io";

    public Task<IReadOnlyList<Game>> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Scan(cancellationToken));

    private static IReadOnlyList<Game> Scan(CancellationToken cancellationToken)
    {
        var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "itch", "apps");
        if (!Directory.Exists(apps)) return [];

        string[] directories;
        try { directories = Directory.GetDirectories(apps); }
        catch { return []; }

        var games = new List<Game>();
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var game = ReadGame(directory);
                if (game is not null) games.Add(game);
            }
            catch
            {
                // jeu illisible : ignoré
            }
        }

        return games;
    }

    private static Game? ReadGame(string directory)
    {
        var folderName = Path.GetFileName(directory);

        // "downloads" est le dossier de téléchargements en cours de l'app
        if (folderName.StartsWith('.') || folderName.Equals("downloads", StringComparison.OrdinalIgnoreCase)) return null;

        var name = ReadReceiptTitle(directory) ?? Prettify(folderName);
        if (string.IsNullOrWhiteSpace(name)) return null;

        // les jeux HTML n'ont pas d'exécutable : on ne sait pas les lancer
        var executable = ScanHelpers.FindMainExecutable(directory, name);
        if (executable is null) return null;

        return new Game
        {
            Id = ScanHelpers.MakeId(GameSource.Itch, folderName),
            Name = name,
            Source = GameSource.Itch,
            InstallPath = directory,
            ExecutablePath = executable,
        };
    }

    // Reçu écrit par butler à l'installation : { "game": { "title": "..." }, ... } compressé en gzip.
    private static string? ReadReceiptTitle(string directory)
    {
        try
        {
            var receipt = Path.Combine(directory, ".itch", "receipt.json.gz");
            if (!File.Exists(receipt)) return null;

            using var file = File.OpenRead(receipt);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var document = JsonDocument.Parse(gzip);

            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("game", out var game)
                && game.ValueKind == JsonValueKind.Object
                && game.TryGetProperty("title", out var title)
                && title.ValueKind == JsonValueKind.String)
            {
                var value = title.GetString()?.Trim();
                return string.IsNullOrEmpty(value) ? null : value;
            }
        }
        catch
        {
            // reçu absent ou illisible : on retombe sur le nom du dossier
        }
        return null;
    }

    // "my-cool_game" -> "My Cool Game" ; un nom qui contient déjà des majuscules est laissé tel quel.
    private static string Prettify(string folderName)
    {
        var text = Separators.Replace(folderName, " ").Trim();
        return text.Any(char.IsUpper) ? text : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text);
    }
}
