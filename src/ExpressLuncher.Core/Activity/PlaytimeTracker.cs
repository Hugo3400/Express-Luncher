using ExpressLuncher.Core.Models;

namespace ExpressLuncher.Core.Activity;

// STUB provisoire : API publique définitive, implémentation à venir.

/// <summary>
/// Mesure le temps de jeu : après un lancement, repère le processus du jeu (un exécutable situé dans
/// son dossier d'installation), attend sa fermeture puis enregistre la session dans l'ActivityStore.
/// Les événements sont levés depuis un thread de fond : l'interface doit repasser sur son thread.
/// </summary>
public sealed class PlaytimeTracker : IDisposable
{
    private readonly ActivityStore _activity;

    public PlaytimeTracker(ActivityStore activity) => _activity = activity;

    /// <summary>Le processus du jeu a été repéré : la session commence.</summary>
    public event Action<Game>? SessionStarted;

    /// <summary>Le jeu s'est fermé ; la session a été enregistrée (si elle dépasse la durée minimale).</summary>
    public event Action<Game, PlaySession>? SessionEnded;

    /// <summary>Vrai tant qu'une session de ce jeu est en cours.</summary>
    public bool IsRunning(string gameId) => false;

    /// <summary>
    /// À appeler juste après GameLauncher.Launch. Sans effet si le jeu est déjà suivi ou si son
    /// dossier d'installation est inconnu. Ne bloque pas.
    /// </summary>
    public void Track(Game game)
    {
        _ = _activity;
        _ = SessionStarted;
        _ = SessionEnded;
    }

    public void Dispose()
    {
    }
}
