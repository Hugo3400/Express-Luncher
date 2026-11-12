namespace ExpressLuncher.Core.Models;

/// <summary>Boutique / origine d'un jeu de la bibliothèque.</summary>
public enum GameSource
{
    Steam,
    Epic,
    Gog,
    Ubisoft,
    EA,
    BattleNet,
    Riot,
    Xbox,
    Rockstar,
    Amazon,
    Itch,
    /// <summary>Jeu ajouté à la main par l'utilisateur (un .exe).</summary>
    Manual,
}

/// <summary>Un jeu de la bibliothèque. Sérialisé tel quel dans library.json.</summary>
public sealed class Game
{
    // ---- Champs remplis par la détection (écrasés à chaque scan) ----

    /// <summary>Identifiant stable, de la forme "source:clé" (ex. "steam:730"). Voir ScanHelpers.MakeId.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public GameSource Source { get; set; }

    /// <summary>Dossier d'installation, si connu.</summary>
    public string? InstallPath { get; set; }

    /// <summary>Chemin complet de l'exécutable principal, si connu.</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>
    /// URI de lancement via la boutique (ex. "steam://rungameid/730"). Prioritaire sur
    /// <see cref="ExecutablePath"/> au lancement, car la boutique gère DRM, cloud et mises à jour.
    /// </summary>
    public string? LaunchUri { get; set; }

    /// <summary>Arguments passés à <see cref="ExecutablePath"/> (ignorés si LaunchUri est utilisé).</summary>
    public string? LaunchArguments { get; set; }

    /// <summary>Jaquette : chemin de fichier local OU URL http(s). Null si aucune.</summary>
    public string? CoverPath { get; set; }

    /// <summary>Grande image de fond (bannière « hero ») : fichier local OU URL http(s). Null si aucune.</summary>
    public string? HeroPath { get; set; }

    /// <summary>Logo du jeu sur fond transparent : fichier local OU URL http(s). Null si aucun.</summary>
    public string? LogoPath { get; set; }

    /// <summary>Taille sur disque en octets, si la boutique la fournit sans parcourir le disque.</summary>
    public long? SizeBytes { get; set; }

    /// <summary>Temps de jeu total connu de la boutique, en secondes (ex. compteur Steam). Null si inconnu.</summary>
    public long? StorePlaytimeSeconds { get; set; }

    /// <summary>Dernier lancement connu de la boutique. Null si inconnu.</summary>
    public DateTime? StoreLastPlayed { get; set; }

    // ---- Champs utilisateur (préservés d'un scan à l'autre) ----

    public bool IsFavorite { get; set; }

    public bool IsHidden { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.Now;

    public DateTime? LastPlayed { get; set; }

    public int PlayCount { get; set; }
}
