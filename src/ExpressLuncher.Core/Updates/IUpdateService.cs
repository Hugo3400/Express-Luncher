namespace ExpressLuncher.Core.Updates;

/// <summary>Une version plus récente disponible dans les Releases GitHub.</summary>
/// <param name="Version">Version publiée (tag sans le "v").</param>
/// <param name="TagName">Tag Git de la release (ex. "v0.2.0").</param>
/// <param name="ReleaseNotes">Corps de la release (Markdown), éventuellement vide.</param>
/// <param name="DownloadUrl">URL de téléchargement du paquet de mise à jour.</param>
/// <param name="SizeBytes">Taille du paquet en octets (0 si inconnue).</param>
/// <param name="PageUrl">Page web de la release, pour l'ouvrir dans le navigateur.</param>
public sealed record UpdateInfo(
    Version Version,
    string TagName,
    string ReleaseNotes,
    string DownloadUrl,
    long SizeBytes,
    string PageUrl);

/// <summary>Mise à jour du launcher via GitHub Releases.</summary>
public interface IUpdateService
{
    Version CurrentVersion { get; }

    /// <summary>
    /// Interroge GitHub. Retourne null si le launcher est à jour, s'il n'existe aucune release,
    /// ou si la vérification échoue (hors ligne, quota d'API...) — ne lève jamais pour ces cas.
    /// </summary>
    Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Télécharge le paquet de la mise à jour et retourne son chemin local.
    /// <paramref name="progress"/> reçoit une valeur de 0 à 1. Lève en cas d'échec.
    /// </summary>
    Task<string> DownloadAsync(
        UpdateInfo update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Prépare l'installation du paquet téléchargé : démarre un processus externe qui attend la
    /// fermeture du launcher, remplace ses fichiers puis le relance. L'appelant doit fermer
    /// l'application juste après cet appel.
    /// </summary>
    void ApplyAndRestart(string packagePath);
}
