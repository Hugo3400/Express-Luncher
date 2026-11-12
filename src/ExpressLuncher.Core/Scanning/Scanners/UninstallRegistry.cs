using Microsoft.Win32;

namespace ExpressLuncher.Core.Scanning.Scanners;

/// <summary>Une entrée « Programmes et fonctionnalités » (clé Uninstall du registre).</summary>
internal sealed record UninstallEntry(
    string KeyName,
    string DisplayName,
    string Publisher,
    string InstallLocation,
    string UninstallString);

/// <summary>Lecture des clés Uninstall et petits outils de chemins communs aux scanners de boutiques.</summary>
internal static class UninstallRegistry
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Toutes les entrées Uninstall (machine 64/32 bits et utilisateur courant), sans doublon.</summary>
    public static IReadOnlyCollection<UninstallEntry> ReadAll()
    {
        var entries = new HashSet<UninstallEntry>();

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var root in ScanHelpers.OpenRegistryKeys(hive, UninstallKey))
            {
                using (root)
                {
                    string[] names;
                    try { names = root.GetSubKeyNames(); }
                    catch { continue; }

                    foreach (var name in names)
                    {
                        try
                        {
                            using var key = root.OpenSubKey(name);
                            if (key is null) continue;

                            entries.Add(new UninstallEntry(
                                name,
                                ReadValue(key, "DisplayName"),
                                ReadValue(key, "Publisher"),
                                ReadValue(key, "InstallLocation"),
                                ReadValue(key, "UninstallString")));
                        }
                        catch
                        {
                            // entrée illisible : ignorée
                        }
                    }
                }
            }
        }

        return entries;
    }

    private static string ReadValue(RegistryKey key, string name) =>
        key.GetValue(name)?.ToString()?.Trim() ?? "";

    /// <summary>
    /// Nettoie un chemin de dossier lu dans le registre ou un fichier de config (guillemets, "/",
    /// séparateur final). Retourne null si le chemin est vide, relatif ou invalide.
    /// </summary>
    public static string? CleanDirectory(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var path = raw.Trim().Trim('"').Trim().Replace('/', '\\');
        if (path.Length == 0 || !Path.IsPathFullyQualified(path)) return null;

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Vrai si le dossier est géré par Steam ou Epic : ces jeux s'enregistrent aussi auprès de la
    /// boutique de leur éditeur, mais c'est le scanner Steam/Epic qui doit les lister.
    /// </summary>
    public static bool BelongsToAnotherStore(string directory)
    {
        if (directory.Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase)) return true;

        try { return Directory.Exists(Path.Combine(directory, ".egstore")); }
        catch { return false; }
    }
}
