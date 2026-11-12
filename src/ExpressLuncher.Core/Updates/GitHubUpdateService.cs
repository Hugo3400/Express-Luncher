using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExpressLuncher.Core.Updates;

/// <summary>
/// Mise à jour via les Releases GitHub : interroge la dernière release publiée, télécharge le paquet
/// <see cref="DefaultAssetName"/> dans %AppData%\ExpressLuncher\cache\updates, puis confie
/// l'installation à un script externe (voir <see cref="UpdateInstaller"/>).
/// </summary>
public sealed class GitHubUpdateService : IUpdateService
{
    /// <summary>Nom exact du paquet attaché à chaque release (produit par .github/workflows/release.yml).</summary>
    public const string DefaultAssetName = "ExpressLuncher-win-x64.zip";

    private const string PackagePrefix = "ExpressLuncher-";
    private const string PackageExtension = ".zip";
    private const string PartExtension = ".part";

    /// <summary>Délai court : la vérification au démarrage ne doit jamais bloquer le launcher.</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Durée maximale sans recevoir d'octets pendant un téléchargement.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Âge à partir duquel un fichier temporaire est considéré comme abandonné.</summary>
    private static readonly TimeSpan StaleAge = TimeSpan.FromHours(1);

    private static readonly Regex VersionPattern =
        new(@"([0-9]{1,9})(?:\.([0-9]{1,9}))?(?:\.([0-9]{1,9}))?", RegexOptions.CultureInvariant);

    private static readonly Lazy<HttpClient> SharedHttp = new(() =>
        new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = CheckTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            // Les délais sont gérés requête par requête (court pour l'API, par inactivité pour le paquet).
            Timeout = Timeout.InfiniteTimeSpan,
        });

    private readonly string _owner;
    private readonly string _repo;
    private readonly string _assetName;
    private readonly HttpClient _http;
    private readonly Version _currentVersion;
    private readonly string? _workDirectory;

    /// <summary>Empreintes SHA-256 annoncées par GitHub, par URL de téléchargement (null si absente).</summary>
    private readonly ConcurrentDictionary<string, string?> _digests = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _downloadLock = new(1, 1);

    public GitHubUpdateService()
        : this(AppInfo.GitHubOwner, AppInfo.GitHubRepo)
    {
    }

    /// <summary>Constructeur paramétrable (tests, autre dépôt). Les valeurs null reprennent celles du launcher.</summary>
    public GitHubUpdateService(
        string owner,
        string repo,
        HttpClient? httpClient = null,
        string? assetName = null,
        Version? currentVersion = null,
        string? workDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        _owner = owner;
        _repo = repo;
        _http = httpClient ?? SharedHttp.Value;
        _assetName = string.IsNullOrWhiteSpace(assetName) ? DefaultAssetName : assetName;
        _currentVersion = Normalize(currentVersion ?? AppInfo.CurrentVersion);
        _workDirectory = workDirectory;
    }

    public Version CurrentVersion => _currentVersion;

    private string WorkDirectory => _workDirectory ?? Path.Combine(AppInfo.CacheDirectory, "updates");

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Hors du thread appelant : ni E/S disque ni résolution DNS sur le thread UI.
            return await Task.Run(async () =>
            {
                CleanStaleFiles(keepPackage: null);

                // « latest » ignore déjà les brouillons et les préversions ; 404 = aucune release.
                using var release = await GetReleaseAsync("releases/latest", cancellationToken).ConfigureAwait(false);
                return release is null ? null : ParseRelease(release.RootElement);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Annulation demandée par l'appelant : la seule exception qu'on laisse remonter.
            throw;
        }
        catch
        {
            // Hors ligne, délai dépassé, quota d'API, JSON inattendu... : « pas de mise à jour ».
            return null;
        }
    }

    public async Task<string> DownloadAsync(
        UpdateInfo update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (!TryGetTrustedUri(update.DownloadUrl, out var uri))
            throw new InvalidOperationException(
                "L'adresse de téléchargement de la mise à jour n'est pas une adresse GitHub sécurisée (https).");

        // Un seul téléchargement à la fois : un second appel attend puis réutilise le paquet déjà vérifié.
        await _downloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => DownloadCoreAsync(update, uri, progress, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    public void ApplyAndRestart(string packagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);

        // Application « single-file » : le dossier d'installation est celui du processus, pas d'Assembly.Location.
        var installDirectory = UpdateInstaller.GetInstallDirectory(Environment.ProcessPath);
        UpdateInstaller.Start(packagePath, installDirectory, Environment.ProcessId, WorkDirectory);
    }

    // ───────────────────────────── Vérification ─────────────────────────────

    /// <summary>Lit une release de l'API GitHub. Retourne null pour toute réponse autre qu'un succès.</summary>
    private async Task<JsonDocument?> GetReleaseAsync(string relativePath, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckTimeout);

        var url = $"https://api.github.com/repos/{Uri.EscapeDataString(_owner)}/{Uri.EscapeDataString(_repo)}/{relativePath}";
        using var request = CreateRequest(new Uri(url), "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);

        // 404 : aucune release publiée. 403/429 : quota de l'API anonyme (60 requêtes/heure par IP).
        if (!response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Convertit le JSON d'une release en <see cref="UpdateInfo"/>, ou null si elle n'est pas plus récente / pas installable.</summary>
    private UpdateInfo? ParseRelease(JsonElement release)
    {
        if (release.ValueKind != JsonValueKind.Object) return null;
        if (GetBool(release, "draft") || GetBool(release, "prerelease")) return null;

        var tag = GetString(release, "tag_name");
        if (!TryParseVersion(tag, out var version)) return null;
        if (version <= _currentVersion) return null;

        if (FindAsset(release) is not { } asset) return null;

        var fallbackPage = $"https://github.com/{_owner}/{_repo}/releases/tag/{Uri.EscapeDataString(tag!)}";
        var page = GetString(release, "html_url");

        return new UpdateInfo(
            version,
            tag!,
            GetString(release, "body") ?? "",
            asset.Url,
            asset.Size,
            TryGetTrustedUri(page, out _) ? page! : fallbackPage);
    }

    /// <summary>Cherche le paquet par son nom exact et mémorise l'empreinte annoncée par GitHub.</summary>
    private (string Url, long Size)? FindAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object) continue;
            if (!string.Equals(GetString(asset, "name"), _assetName, StringComparison.OrdinalIgnoreCase)) continue;

            // Un envoi encore en cours ("starter"/"open") n'est pas téléchargeable.
            if (GetString(asset, "state") is { } state && state != "uploaded") return null;

            var url = GetString(asset, "browser_download_url");
            if (!TryGetTrustedUri(url, out _)) return null;

            var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) && bytes > 0 ? bytes : 0;
            _digests[url!] = ParseSha256Digest(GetString(asset, "digest"));
            return (url!, size);
        }

        return null;
    }

    /// <summary>
    /// Extrait major.minor.build d'un tag : tolère un préfixe ("v0.2.0"), un suffixe ("0.2.0-beta.1")
    /// et les composantes manquantes ("v2" vaut 2.0.0).
    /// </summary>
    internal static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;

        var match = VersionPattern.Match(tag);
        if (!match.Success) return false;

        version = new Version(Component(match.Groups[1]), Component(match.Groups[2]), Component(match.Groups[3]));
        return true;

        static int Component(Group group) => group.Success ? int.Parse(group.ValueSpan) : 0;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    // ───────────────────────────── Téléchargement ─────────────────────────────

    private async Task<string> DownloadCoreAsync(
        UpdateInfo update,
        Uri uri,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var directory = WorkDirectory;
        var packagePath = Path.Combine(directory, $"{PackagePrefix}{Normalize(update.Version)}{PackageExtension}");
        var partPath = packagePath + PartExtension;
        var completed = false;

        try
        {
            Directory.CreateDirectory(directory);
            CleanStaleFiles(keepPackage: packagePath);

            var expectedHash = await ResolveSha256Async(update, cancellationToken).ConfigureAwait(false);

            // Paquet déjà téléchargé et intact : inutile de recommencer.
            if (expectedHash is not null && File.Exists(packagePath)
                && await FileMatchesAsync(packagePath, expectedHash, cancellationToken).ConfigureAwait(false))
            {
                completed = true;
                progress?.Report(1);
                return packagePath;
            }

            progress?.Report(0);

            // Chien de garde : réarmé à chaque bloc reçu, il coupe une connexion qui ne répond plus.
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stall.CancelAfter(StallTimeout);

            using var request = CreateRequest(uri, "application/octet-stream");
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token)
                .ConfigureAwait(false);

            // github.com redirige vers son CDN : la destination finale doit rester chez GitHub, en https.
            if (response.RequestMessage?.RequestUri is not { } finalUri || !IsTrustedUri(finalUri))
                throw new InvalidOperationException(
                    "Le téléchargement de la mise à jour a été redirigé hors de GitHub : il a été annulé.");

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Le téléchargement de la mise à jour a échoué (réponse HTTP {(int)response.StatusCode}).");

            var total = response.Content.Headers.ContentLength ?? update.SizeBytes;
            long received = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            await using (var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false))
            await using (var target = new FileStream(
                partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                var lastReported = 0.0;
                int read;

                while ((read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false)) > 0)
                {
                    stall.CancelAfter(StallTimeout);

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    received += read;

                    if (progress is null || total <= 0) continue;
                    var fraction = Math.Min(1.0, (double)received / total);
                    if (fraction - lastReported < 0.01) continue;
                    lastReported = fraction;
                    progress.Report(fraction);
                }
            }

            if (update.SizeBytes > 0 && received != update.SizeBytes)
                throw new InvalidOperationException(
                    $"Le téléchargement de la mise à jour est incomplet ({received} octets reçus sur {update.SizeBytes}).");

            if (expectedHash is not null
                && !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Le paquet de mise à jour téléchargé est corrompu : son empreinte SHA-256 ne correspond pas à celle publiée sur GitHub.");

            File.Move(partPath, packagePath, overwrite: true);
            completed = true;
            progress?.Report(1);
            return packagePath;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "Le téléchargement de la mise à jour a expiré : la connexion ne répond plus.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Le téléchargement de la mise à jour a échoué. Vérifiez votre connexion Internet et l'espace disque. Détail : {ex.Message}", ex);
        }
        finally
        {
            if (!completed) TryDelete(partPath);
        }
    }

    /// <summary>
    /// Empreinte SHA-256 attendue (hexadécimal), ou null si GitHub n'en fournit pas. Si l'UpdateInfo
    /// vient d'une autre instance du service, l'empreinte est redemandée à l'API (sans échec bloquant).
    /// </summary>
    private async Task<string?> ResolveSha256Async(UpdateInfo update, CancellationToken cancellationToken)
    {
        if (_digests.TryGetValue(update.DownloadUrl, out var known)) return known;

        try
        {
            var path = $"releases/tags/{Uri.EscapeDataString(update.TagName)}";
            using var release = await GetReleaseAsync(path, cancellationToken).ConfigureAwait(false);
            if (release is not null) FindAsset(release.RootElement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Empreinte indisponible : le contrôle de taille reste appliqué.
        }

        return _digests.GetValueOrDefault(update.DownloadUrl);
    }

    /// <summary>Lit le champ "digest" d'un asset ("sha256:&lt;hex&gt;"). Tout autre format est ignoré.</summary>
    internal static string? ParseSha256Digest(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var hex = digest[prefix.Length..].Trim();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToUpperInvariant() : null;
    }

    private static async Task<bool> FileMatchesAsync(string path, string expectedHash, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return string.Equals(Convert.ToHexString(actual), expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ───────────────────────────── Outils ─────────────────────────────

    private HttpRequestMessage CreateRequest(Uri uri, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        // GitHub refuse les requêtes sans User-Agent.
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ExpressLuncher", _currentVersion.ToString()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        return request;
    }

    private static bool TryGetTrustedUri(string? url, out Uri uri)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) && IsTrustedUri(parsed))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    /// <summary>https uniquement, et seulement chez GitHub (site, API ou CDN des assets).</summary>
    internal static bool IsTrustedUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps) return false;

        var host = uri.IdnHost;
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Nettoie le dossier des mises à jour, sans jamais lever : paquets déjà installés ou remplacés,
    /// et fichiers temporaires abandonnés (téléchargement interrompu, dossier de préparation, script).
    /// </summary>
    private void CleanStaleFiles(string? keepPackage)
    {
        try
        {
            var directory = new DirectoryInfo(WorkDirectory);
            if (!directory.Exists) return;

            var now = DateTime.UtcNow;
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                try
                {
                    if (entry.Name.Equals(UpdateInstaller.LogFileName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (keepPackage is not null && entry.FullName.Equals(keepPackage, StringComparison.OrdinalIgnoreCase)) continue;

                    if (entry is FileInfo && TryGetPackageVersion(entry.Name, out var version))
                    {
                        // Paquet d'une version pas encore installée : gardé tant qu'aucun autre téléchargement ne le remplace.
                        if (keepPackage is null && version > _currentVersion) continue;
                    }
                    else if (now - entry.LastWriteTimeUtc < StaleAge)
                    {
                        // Fichier récent : peut-être encore utilisé par un téléchargement ou par le script d'installation.
                        continue;
                    }

                    if (entry is DirectoryInfo folder) folder.Delete(recursive: true);
                    else entry.Delete();
                }
                catch
                {
                    // Fichier verrouillé ou protégé : on réessaiera à la prochaine vérification.
                }
            }
        }
        catch
        {
            // Le nettoyage ne doit jamais empêcher une vérification ou un téléchargement.
        }
    }

    private static bool TryGetPackageVersion(string fileName, out Version version)
    {
        version = new Version(0, 0, 0);
        if (!fileName.StartsWith(PackagePrefix, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(PackageExtension, StringComparison.OrdinalIgnoreCase)
            || fileName.Length <= PackagePrefix.Length + PackageExtension.Length)
            return false;

        var middle = fileName[PackagePrefix.Length..^PackageExtension.Length];
        if (!Version.TryParse(middle, out var parsed)) return false;

        version = Normalize(parsed);
        return true;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* ignoré */ }
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
}
