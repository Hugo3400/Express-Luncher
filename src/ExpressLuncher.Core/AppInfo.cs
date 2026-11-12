using System.Reflection;

namespace ExpressLuncher.Core;

/// <summary>Constantes de l'application et emplacements des données utilisateur.</summary>
public static class AppInfo
{
    public const string Name = "Express Luncher";

    /// <summary>Dépôt GitHub d'où viennent les mises à jour (onglet Releases).</summary>
    public const string GitHubOwner = "Hugo3400";
    public const string GitHubRepo = "Express-Luncher";

    public static string RepositoryUrl => $"https://github.com/{GitHubOwner}/{GitHubRepo}";

    /// <summary>Version de l'exécutable en cours (propriété Version de Directory.Build.props).</summary>
    public static Version CurrentVersion
    {
        get
        {
            var v = (Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly).GetName().Version;
            return v is null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    /// <summary>%AppData%\ExpressLuncher — créé à la demande.</summary>
    public static string DataDirectory
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExpressLuncher");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string LibraryFile => Path.Combine(DataDirectory, "library.json");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>Cache local (jaquettes téléchargées, icônes...).</summary>
    public static string CacheDirectory
    {
        get
        {
            var dir = Path.Combine(DataDirectory, "cache");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
