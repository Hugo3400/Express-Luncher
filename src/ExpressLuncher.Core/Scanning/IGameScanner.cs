using ExpressLuncher.Core.Models;

namespace ExpressLuncher.Core.Scanning;

/// <summary>
/// Détecte les jeux installés d'une boutique. Une implémentation par boutique, dans Scanning/Scanners.
/// Contrat : ne jamais afficher d'UI, ne rien écrire sur le disque, et retourner une liste vide
/// (sans lever d'exception) quand la boutique n'est pas installée.
/// </summary>
public interface IGameScanner
{
    GameSource Source { get; }

    /// <summary>Nom lisible de la boutique (ex. "Steam", "Epic Games").</summary>
    string DisplayName { get; }

    /// <summary>
    /// Retourne les jeux installés. Chaque Game doit avoir Id (via ScanHelpers.MakeId), Name, Source,
    /// et au moins LaunchUri ou ExecutablePath. Les champs utilisateur (favori, etc.) sont ignorés.
    /// </summary>
    Task<IReadOnlyList<Game>> ScanAsync(CancellationToken cancellationToken = default);
}
