using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace ExpressLuncher.Services;

/// <summary>Ce que les view models demandent à la fenêtre et au système, sans les connaître.</summary>
public interface IShellService
{
    /// <summary>Fait choisir un ou plusieurs .exe. Liste vide si l'utilisateur annule.</summary>
    IReadOnlyList<string> PickExecutables();

    void MinimizeMainWindow();

    /// <summary>Ouvre une URL dans le navigateur par défaut.</summary>
    void OpenUrl(string url);

    void Shutdown();
}

public sealed class ShellService : IShellService
{
    public IReadOnlyList<string> PickExecutables()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Ajouter un jeu",
            Filter = "Exécutables (*.exe)|*.exe|Tous les fichiers (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
            DereferenceLinks = true,
        };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileNames : [];
    }

    public void MinimizeMainWindow()
    {
        if (Application.Current?.MainWindow is { } window) window.WindowState = WindowState.Minimized;
    }

    public void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return;

        using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public void Shutdown() => Application.Current?.Shutdown();
}
