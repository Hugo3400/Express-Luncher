using System.Text;
using ExpressLuncher.Core.Models;
using Microsoft.Win32;

namespace ExpressLuncher.Core.Scanning;

/// <summary>Outils partagés par tous les scanners.</summary>
public static class ScanHelpers
{
    /// <summary>Construit l'identifiant stable d'un jeu : "steam:730", "epic:Fortnite"...</summary>
    public static string MakeId(GameSource source, string key) =>
        $"{source.ToString().ToLowerInvariant()}:{key.Trim()}";

    /// <summary>
    /// Lit une valeur chaîne du registre en essayant les vues 64 puis 32 bits.
    /// <paramref name="subKey"/> sans préfixe de ruche ni "WOW6432Node". Retourne null si absente.
    /// </summary>
    public static string? ReadRegistryString(RegistryHive hive, string subKey, string valueName)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(subKey);
                var value = key?.GetValue(valueName)?.ToString();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            catch
            {
                // clé inaccessible : on essaie la vue suivante
            }
        }
        return null;
    }

    /// <summary>
    /// Ouvre une sous-clé en lecture dans les vues 64 puis 32 bits et retourne celles qui existent.
    /// L'appelant doit libérer (Dispose) les clés retournées.
    /// </summary>
    public static IEnumerable<RegistryKey> OpenRegistryKeys(RegistryHive hive, string subKey)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            RegistryKey? key = null;
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                key = baseKey.OpenSubKey(subKey);
            }
            catch
            {
                // ignoré
            }
            if (key is not null) yield return key;
        }
    }

    private static readonly string[] IgnoredExeFragments =
    [
        "unins", "uninstall", "setup", "install", "redist", "vcredist", "vc_redist", "dxsetup", "dxwebsetup",
        "crash", "report", "helper", "updater", "update", "patcher", "launcherpatcher", "touchup", "cleanup",
        "activation", "dotnet", "directx", "physx", "easyanticheat", "battleye", "beservice", "anticheat",
        "cefprocess", "subprocess", "webhelper", "overlay", "diagnostic", "benchmark", "config", "settings",
        "notification", "service", "7z", "ffmpeg", "python", "prereq", "bootstrap", "quicksfv", "oalinst",
    ];

    /// <summary>Vrai pour les exécutables qui ne sont manifestement pas le jeu (désinstalleur, anti-triche...).</summary>
    public static bool IsIgnoredExecutable(string fileNameWithoutExtension)
    {
        var name = fileNameWithoutExtension.ToLowerInvariant();
        foreach (var fragment in IgnoredExeFragments)
            if (name.Contains(fragment)) return true;
        return false;
    }

    /// <summary>
    /// Devine l'exécutable principal d'un jeu dans son dossier d'installation : écarte les utilitaires,
    /// favorise les noms proches de <paramref name="hintName"/>, les gros fichiers et les dossiers peu profonds.
    /// Retourne null si rien de plausible.
    /// </summary>
    public static string? FindMainExecutable(string? directory, string? hintName = null, int maxDepth = 3)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;

        var hint = Normalize(hintName);
        string? best = null;
        var bestScore = double.MinValue;

        foreach (var (file, depth) in EnumerateExecutables(directory, maxDepth))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (IsIgnoredExecutable(name)) continue;

            long size;
            try { size = new FileInfo(file).Length; }
            catch { continue; }

            var score = Math.Log10(Math.Max(size, 1)) - depth * 1.5;
            var normalized = Normalize(name);
            if (hint.Length > 0 && normalized.Length > 0)
            {
                if (normalized == hint) score += 10;
                else if (hint.Contains(normalized) || normalized.Contains(hint)) score += 5;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = file;
            }
        }

        return best;
    }

    private static IEnumerable<(string File, int Depth)> EnumerateExecutables(string root, int maxDepth)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;

        while (queue.Count > 0 && visited < 2000)
        {
            var (dir, depth) = queue.Dequeue();
            visited++;

            string[] files;
            try { files = Directory.GetFiles(dir, "*.exe"); }
            catch { continue; }

            foreach (var file in files) yield return (file, depth);

            if (depth >= maxDepth) continue;

            string[] subDirs;
            try { subDirs = Directory.GetDirectories(dir); }
            catch { continue; }

            foreach (var sub in subDirs) queue.Enqueue((sub, depth + 1));
        }
    }

    /// <summary>Minuscule, lettres et chiffres uniquement — pour comparer des noms de jeux/fichiers.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
