using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace ExpressLuncher.Core.Updates;

/// <summary>
/// Installation d'un paquet de mise à jour : extraction dans un dossier de préparation, puis lancement
/// d'un script cmd détaché et invisible qui attend la fermeture du launcher, remplace ses fichiers et
/// le relance. Tout est paramétré (dossier d'installation, PID, dossier de travail) pour être testable.
/// </summary>
internal static class UpdateInstaller
{
    public const string ExecutableName = "ExpressLuncher.exe";

    /// <summary>Journal de la dernière installation, écrit par le script dans le dossier de travail.</summary>
    public const string LogFileName = "update.log";

    private const string StagingFolderName = "staging";
    private const string ScriptFileName = "apply-update.cmd";

    /// <summary>Temps laissé au launcher pour se fermer avant que le script n'abandonne (secondes).</summary>
    private const int ExitWaitSeconds = 60;

    // Script ASCII pur : les chemins (espaces, accents, &, %...) arrivent par les variables
    // d'environnement EL_*, ce qui évite tout problème d'encodage ou d'échappement dans le fichier.
    // Pas de bloc ( ) ni d'expansion retardée, pour qu'aucun caractère d'un chemin ne soit interprété.
    private const string Script = """
        @echo off
        setlocal EnableExtensions DisableDelayedExpansion
        rem Express Luncher - installation d'une mise a jour (script genere, ne pas modifier).
        set "EL_SYS=%SystemRoot%\System32"

        > "%EL_LOG%" echo [%DATE% %TIME%] Attente de la fermeture du launcher (PID %EL_PID%).
        set /a EL_ELAPSED=0
        :wait
        "%EL_SYS%\tasklist.exe" /FI "PID eq %EL_PID%" /FO CSV /NH 2>nul | "%EL_SYS%\find.exe" ",""%EL_PID%""," >nul
        if errorlevel 1 goto copy
        if %EL_ELAPSED% GEQ %EL_WAIT% goto timeout
        "%EL_SYS%\ping.exe" -n 2 127.0.0.1 >nul
        set /a EL_ELAPSED+=1
        goto wait

        :timeout
        >> "%EL_LOG%" echo [%DATE% %TIME%] Abandon : le launcher ne s'est pas ferme apres %EL_WAIT% s.
        rd /s /q "%EL_STAGING%" >nul 2>&1
        exit /b 1

        :copy
        >> "%EL_LOG%" echo [%DATE% %TIME%] Copie des fichiers vers "%EL_INSTALL%".
        "%EL_SYS%\robocopy.exe" "%EL_SOURCE%" "%EL_INSTALL%" /E /IS /IT /R:30 /W:1 /NP /NJH /NDL >> "%EL_LOG%" 2>&1
        if errorlevel 8 goto failed
        >> "%EL_LOG%" echo [%DATE% %TIME%] Mise a jour installee.
        del /f /q "%EL_PACKAGE%" >nul 2>&1
        goto restart

        :failed
        >> "%EL_LOG%" echo [%DATE% %TIME%] ECHEC de la copie : la version presente est relancee.

        :restart
        rd /s /q "%EL_STAGING%" >nul 2>&1
        >> "%EL_LOG%" echo [%DATE% %TIME%] Relance de "%EL_EXE%".
        start "" /D "%EL_INSTALL%" "%EL_EXE%"
        exit /b 0
        """;

    /// <summary>
    /// Dossier d'installation déduit du chemin du processus. Lève si ce n'est pas un launcher publié :
    /// hôte dotnet.exe, exécutable renommé, ou ExpressLuncher.dll présent à côté de l'exe (build de
    /// développement, pas un exécutable « single-file ») — on n'écrase jamais un dossier de build.
    /// </summary>
    public static string GetInstallDirectory(string? processPath)
    {
        var directory = string.IsNullOrWhiteSpace(processPath) ? null : Path.GetDirectoryName(Path.GetFullPath(processPath));

        var isPublishedLauncher = directory is not null
            && string.Equals(Path.GetFileName(processPath), ExecutableName, StringComparison.OrdinalIgnoreCase)
            && !File.Exists(Path.Combine(directory, Path.ChangeExtension(ExecutableName, ".dll")));

        if (!isPublishedLauncher)
            throw new InvalidOperationException(
                "La mise à jour automatique n'est disponible que dans la version publiée d'Express Luncher, " +
                "pas depuis un build de développement. Téléchargez la nouvelle version depuis la page des releases.");

        return directory!;
    }

    /// <summary>
    /// Prépare la mise à jour puis démarre le script d'installation. Toutes les vérifications (paquet,
    /// droits d'écriture) sont faites ici, avant la fermeture du launcher : en cas de problème, une
    /// InvalidOperationException explique quoi faire et rien n'est modifié.
    /// </summary>
    public static void Start(string packagePath, string installDirectory, int processId, string workDirectory)
    {
        packagePath = Path.GetFullPath(packagePath);
        installDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory));
        workDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workDirectory));

        if (!File.Exists(packagePath))
            throw new InvalidOperationException($"Le paquet de mise à jour est introuvable : « {packagePath} ».");
        if (!Directory.Exists(installDirectory))
            throw new InvalidOperationException($"Le dossier d'installation est introuvable : « {installDirectory} ».");

        EnsureWritable(installDirectory);

        var stagingDirectory = Path.Combine(workDirectory, StagingFolderName);
        try
        {
            Directory.CreateDirectory(workDirectory);
            var sourceDirectory = Extract(packagePath, stagingDirectory);

            var scriptPath = Path.Combine(workDirectory, ScriptFileName);
            File.WriteAllText(scriptPath, Script.ReplaceLineEndings("\r\n") + "\r\n", Encoding.ASCII);

            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                // /s + guillemets doublés : cmd retire la paire extérieure et garde le chemin entre guillemets.
                Arguments = $"/d /s /c \"\"{scriptPath}\"\"",
                // Ni le dossier d'installation ni celui de préparation : le script doit pouvoir les modifier.
                WorkingDirectory = Environment.SystemDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.Environment["EL_PID"] = processId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            startInfo.Environment["EL_WAIT"] = ExitWaitSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            startInfo.Environment["EL_SOURCE"] = ForCommandLine(sourceDirectory);
            startInfo.Environment["EL_STAGING"] = ForCommandLine(stagingDirectory);
            startInfo.Environment["EL_INSTALL"] = ForCommandLine(installDirectory);
            startInfo.Environment["EL_EXE"] = Path.Combine(installDirectory, ExecutableName);
            startInfo.Environment["EL_PACKAGE"] = packagePath;
            startInfo.Environment["EL_LOG"] = Path.Combine(workDirectory, LogFileName);

            // Le script survit à la fermeture du launcher : Windows ne tue pas les processus enfants.
            using var helper = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Impossible de démarrer le script d'installation de la mise à jour.");
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(stagingDirectory);

            if (ex is InvalidOperationException) throw;
            if (ex is InvalidDataException)
                throw new InvalidOperationException("Le paquet de mise à jour est corrompu : ce n'est pas une archive zip valide.", ex);
            if (ex is IOException or UnauthorizedAccessException or Win32Exception or NotSupportedException)
                throw new InvalidOperationException($"Impossible de préparer la mise à jour : {ex.Message}", ex);
            throw;
        }
    }

    /// <summary>Vérifie avant de fermer le launcher qu'on peut écrire dans son dossier (ex. Program Files sans élévation).</summary>
    private static void EnsureWritable(string installDirectory)
    {
        try
        {
            var probe = Path.Combine(installDirectory, $".update-{Guid.NewGuid():N}.tmp");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(
                $"Impossible d'écrire dans le dossier d'installation « {installDirectory} ». " +
                "Relancez Express Luncher en tant qu'administrateur, ou déplacez-le dans un dossier qui vous appartient, puis réessayez.",
                ex);
        }
    }

    /// <summary>
    /// Extrait le paquet dans le dossier de préparation et retourne le dossier qui contient
    /// ExpressLuncher.exe (la racine, ou l'unique sous-dossier d'un zip créé à la main).
    /// </summary>
    private static string Extract(string packagePath, string stagingDirectory)
    {
        if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
        Directory.CreateDirectory(stagingDirectory);

        var root = Path.GetFullPath(stagingDirectory) + Path.DirectorySeparatorChar;

        using (var archive = ZipFile.OpenRead(packagePath))
        {
            foreach (var entry in archive.Entries)
            {
                // Anti « zip-slip » : aucune entrée (.., chemin absolu) ne doit sortir du dossier de préparation.
                var destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Le paquet de mise à jour est invalide : l'entrée « {entry.FullName} » sort du dossier d'extraction.");

                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }
        }

        var source = stagingDirectory;
        if (!File.Exists(Path.Combine(source, ExecutableName)))
        {
            var entries = Directory.GetFileSystemEntries(stagingDirectory);
            if (entries.Length == 1 && Directory.Exists(entries[0])) source = entries[0];
        }

        var executable = Path.Combine(source, ExecutableName);
        if (!File.Exists(executable) || !LooksLikeExecutable(executable))
            throw new InvalidOperationException(
                $"Le paquet de mise à jour est invalide : il ne contient pas {ExecutableName}.");

        return source;
    }

    /// <summary>Un exécutable Windows commence par « MZ » : écarte un fichier vide ou une page d'erreur.</summary>
    private static bool LooksLikeExecutable(string path)
    {
        using var stream = File.OpenRead(path);
        return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
    }

    /// <summary>"C:\" entre guillemets se termine par \" que robocopy lit comme un guillemet échappé : on écrit "C:\.".</summary>
    private static string ForCommandLine(string directory) =>
        directory.EndsWith('\\') || directory.EndsWith('/') ? directory + "." : directory;

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* ignoré */ }
    }
}
