using System.Globalization;
using System.Text.Json;
using ExpressLuncher.Core.Models;

namespace ExpressLuncher.Core.Scanning.Scanners;

/// <summary>
/// Riot Games : les produits installés sont décrits dans %ProgramData%\Riot Games\Metadata\&lt;produit&gt;.&lt;patchline&gt;
/// et dans les entrées Uninstall "Riot Game &lt;produit&gt;.&lt;patchline&gt;". Tout se lance par le Riot Client
/// (RiotClientServices.exe --launch-product=... --launch-patchline=...).
/// </summary>
public sealed class RiotScanner : IGameScanner
{
    private const string UninstallPrefix = "Riot Game ";
    private const string InstallPathKey = "product_install_full_path:";

    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["valorant"] = "VALORANT",
        ["league_of_legends"] = "League of Legends",
        ["bacon"] = "Legends of Runeterra",
        ["lion"] = "2XKO",
    };

    public GameSource Source => GameSource.Riot;

    public string DisplayName => "Riot Games";

    public Task<IReadOnlyList<Game>> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Scan(cancellationToken));

    private sealed class Product
    {
        public string? InstallDir;
        public string? Name;
    }

    private static IReadOnlyList<Game> Scan(CancellationToken cancellationToken)
    {
        var riotData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games");

        // "produit.patchline" -> infos fusionnées des deux sources
        var products = new Dictionary<string, Product>(StringComparer.OrdinalIgnoreCase);
        ReadMetadata(riotData, products);
        var clientFromUninstall = ReadUninstallEntries(products);

        // sans Riot Client, aucun produit n'est lançable
        var client = FindClient(riotData) ?? clientFromUninstall;
        if (client is null) return [];

        var games = new List<Game>();
        foreach (var (key, product) in products)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var game = BuildGame(key, product, client);
                if (game is not null) games.Add(game);
            }
            catch
            {
                // produit illisible : ignoré
            }
        }

        return games;
    }

    private static Game? BuildGame(string key, Product product, string client)
    {
        var dot = key.LastIndexOf('.');
        if (dot <= 0 || dot == key.Length - 1) return null;

        var productId = key[..dot];
        var patchline = key[(dot + 1)..];
        if (!IsToken(productId) || !IsToken(patchline)) return null;
        if (productId.StartsWith("riot_client", StringComparison.OrdinalIgnoreCase)) return null;

        if (product.InstallDir is null || !Directory.Exists(product.InstallDir)) return null;

        var name = KnownNames.GetValueOrDefault(productId) ?? product.Name ?? Prettify(productId);
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (!patchline.Equals("live", StringComparison.OrdinalIgnoreCase)) name += $" ({patchline.ToUpperInvariant()})";

        return new Game
        {
            Id = ScanHelpers.MakeId(GameSource.Riot, key.ToLowerInvariant()),
            Name = name,
            Source = GameSource.Riot,
            InstallPath = product.InstallDir,
            ExecutablePath = client,
            LaunchArguments = $"--launch-product={productId} --launch-patchline={patchline}",
        };
    }

    // Metadata\<produit>.<patchline>\<produit>.<patchline>.product_settings.yaml : une ligne nous suffit.
    private static void ReadMetadata(string riotData, Dictionary<string, Product> products)
    {
        var metadata = Path.Combine(riotData, "Metadata");
        if (!Directory.Exists(metadata)) return;

        string[] directories;
        try { directories = Directory.GetDirectories(metadata); }
        catch { return; }

        foreach (var directory in directories)
        {
            try
            {
                var key = Path.GetFileName(directory);
                var settings = Path.Combine(directory, key + ".product_settings.yaml");
                if (!File.Exists(settings)) continue;

                foreach (var line in File.ReadLines(settings))
                {
                    if (!line.StartsWith(InstallPathKey, StringComparison.Ordinal)) continue;

                    var installDir = UninstallRegistry.CleanDirectory(ReadYamlScalar(line[InstallPathKey.Length..]));
                    if (installDir is not null) GetProduct(products, key).InstallDir = installDir;
                    break;
                }
            }
            catch
            {
                // produit illisible : ignoré
            }
        }
    }

    private static string ReadYamlScalar(string raw)
    {
        var value = raw.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\\\\", "\\").Replace("\\\"", "\"");
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1].Replace("''", "'");
        return value;
    }

    /// <summary>
    /// Complète <paramref name="products"/> avec les entrées Uninstall et retourne le chemin du Riot Client
    /// trouvé dans leurs commandes de désinstallation (null si aucun).
    /// </summary>
    private static string? ReadUninstallEntries(Dictionary<string, Product> products)
    {
        string? client = null;

        foreach (var entry in UninstallRegistry.ReadAll())
        {
            if (!entry.KeyName.StartsWith(UninstallPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var key = entry.KeyName[UninstallPrefix.Length..].Trim();
            if (key.Length == 0) continue;

            var product = GetProduct(products, key);
            product.InstallDir ??= UninstallRegistry.CleanDirectory(entry.InstallLocation);
            if (entry.DisplayName.Length > 0) product.Name ??= entry.DisplayName;

            client ??= ExtractClientPath(entry.UninstallString);
        }

        return client;
    }

    // "C:\Riot Games\Riot Client\RiotClientServices.exe" --uninstall-product=... --uninstall-patchline=...
    private static string? ExtractClientPath(string command)
    {
        if (command.Length < 2 || command[0] != '"') return null;
        var end = command.IndexOf('"', 1);
        if (end < 0) return null;

        var path = command[1..end];
        return path.EndsWith("RiotClientServices.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
    }

    private static Product GetProduct(Dictionary<string, Product> products, string key)
    {
        if (!products.TryGetValue(key, out var product)) products[key] = product = new Product();
        return product;
    }

    // RiotClientInstalls.json : { "rc_default": "C:/Riot Games/Riot Client/RiotClientServices.exe", ... }
    private static string? FindClient(string riotData)
    {
        try
        {
            var file = Path.Combine(riotData, "RiotClientInstalls.json");
            if (!File.Exists(file)) return null;

            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            foreach (var property in new[] { "rc_default", "rc_live", "rc_beta" })
            {
                if (!document.RootElement.TryGetProperty(property, out var value)) continue;
                if (value.ValueKind != JsonValueKind.String) continue;

                var path = value.GetString()?.Replace('/', '\\');
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
            }
        }
        catch
        {
            // fichier absent ou corrompu
        }
        return null;
    }

    private static bool IsToken(string text) =>
        text.Length > 0 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static string Prettify(string productId) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(productId.Replace('_', ' ').Replace('-', ' ').ToLowerInvariant());
}
