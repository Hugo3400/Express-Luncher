using ExpressLuncher.Core.Models;

namespace ExpressLuncher.Core.Artwork;

// STUB provisoire : API publique définitive, implémentation à venir.

/// <summary>Illustrations d'un jeu, en fichiers locaux (cache). Chaque champ peut être null.</summary>
public sealed record GameArtwork(string? CoverPath, string? HeroPath, string? LogoPath);

/// <summary>
/// Trouve et met en cache les illustrations d'un jeu : jaquette verticale (2:3), grande image de fond
/// (« hero ») et logo transparent. Thread-safe ; ne modifie jamais le Game reçu.
/// </summary>
public sealed class ArtworkService
{
    public ArtworkService(HttpClient? httpClient = null)
    {
        _ = httpClient;
    }

    /// <summary>
    /// Retourne les illustrations du jeu sous forme de fichiers locaux, en les téléchargeant si besoin.
    /// Retourne null si rien n'a été trouvé ou hors ligne — ne lève jamais (sauf annulation).
    /// Les résultats, y compris « introuvable », sont mémorisés pour ne pas réinterroger le réseau.
    /// </summary>
    public Task<GameArtwork?> ResolveAsync(Game game, CancellationToken cancellationToken = default) =>
        Task.FromResult<GameArtwork?>(null);
}
