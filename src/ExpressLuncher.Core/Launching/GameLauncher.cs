using System.Diagnostics;
using ExpressLuncher.Core.Models;

namespace ExpressLuncher.Core.Launching;

/// <summary>Lance les jeux et ouvre leurs dossiers.</summary>
public static class GameLauncher
{
    /// <summary>
    /// Lance le jeu : via l'URI de sa boutique si elle existe, sinon via son exécutable.
    /// Met à jour LastPlayed et PlayCount (à l'appelant de sauvegarder la bibliothèque).
    /// Lève InvalidOperationException si le jeu n'a aucun moyen de lancement valide.
    /// </summary>
    public static void Launch(Game game)
    {
        ProcessStartInfo startInfo;

        if (!string.IsNullOrWhiteSpace(game.LaunchUri))
        {
            startInfo = new ProcessStartInfo(game.LaunchUri) { UseShellExecute = true };
        }
        else if (!string.IsNullOrWhiteSpace(game.ExecutablePath) && File.Exists(game.ExecutablePath))
        {
            startInfo = new ProcessStartInfo(game.ExecutablePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(game.ExecutablePath) ?? "",
                Arguments = game.LaunchArguments ?? "",
            };
        }
        else
        {
            throw new InvalidOperationException($"Impossible de lancer « {game.Name} » : exécutable introuvable.");
        }

        using var process = Process.Start(startInfo);

        game.LastPlayed = DateTime.Now;
        game.PlayCount++;
    }

    /// <summary>Vrai si le dossier d'installation (ou celui de l'exécutable) existe.</summary>
    public static bool CanOpenInstallFolder(Game game) => ResolveFolder(game) is not null;

    /// <summary>Ouvre le dossier du jeu dans l'Explorateur. Sans effet si le dossier est inconnu.</summary>
    public static void OpenInstallFolder(Game game)
    {
        var folder = ResolveFolder(game);
        if (folder is null) return;
        using var process = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private static string? ResolveFolder(Game game)
    {
        if (!string.IsNullOrWhiteSpace(game.InstallPath) && Directory.Exists(game.InstallPath))
            return game.InstallPath;

        var exeDir = string.IsNullOrWhiteSpace(game.ExecutablePath) ? null : Path.GetDirectoryName(game.ExecutablePath);
        return !string.IsNullOrWhiteSpace(exeDir) && Directory.Exists(exeDir) ? exeDir : null;
    }
}
