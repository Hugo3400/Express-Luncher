using System.Runtime.InteropServices;
using ExpressLuncher.Core.Models;
using ExpressLuncher.Core.Scanning;
using Microsoft.Win32.SafeHandles;

namespace ExpressLuncher.Core.Activity;

/// <summary>
/// Mesure le temps de jeu : après un lancement, repère le processus du jeu (un exécutable situé dans
/// son dossier d'installation), attend sa fermeture puis enregistre la session dans l'ActivityStore.
/// Les événements sont levés depuis un thread de fond : l'interface doit repasser sur son thread.
/// </summary>
public sealed class PlaytimeTracker : IDisposable
{
    private readonly ActivityStore _activity;
    private readonly object _gate = new();
    private readonly Dictionary<string, TrackedGame> _tracked = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private bool _loopRunning;
    private bool _disposed;

    public PlaytimeTracker(ActivityStore activity) => _activity = activity;

    // Réglages internes, raccourcis par les tests.

    /// <summary>Intervalle entre deux relevés des processus.</summary>
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2.5);

    /// <summary>Durée pendant laquelle on guette l'apparition du jeu après son lancement.</summary>
    internal TimeSpan DetectionWindow { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Délai sans aucun processus du jeu avant de considérer la session terminée.</summary>
    internal TimeSpan ExitGrace { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Durée minimale d'une session pour être enregistrée.</summary>
    internal TimeSpan MinimumSession { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Le processus du jeu a été repéré : la session commence.</summary>
    public event Action<Game>? SessionStarted;

    /// <summary>Le jeu s'est fermé ; la session a été enregistrée (si elle dépasse la durée minimale).</summary>
    public event Action<Game, PlaySession>? SessionEnded;

    /// <summary>Vrai tant qu'une session de ce jeu est en cours.</summary>
    public bool IsRunning(string gameId)
    {
        if (string.IsNullOrEmpty(gameId)) return false;
        lock (_gate)
            return _tracked.TryGetValue(gameId, out var tracked) && tracked.Started;
    }

    /// <summary>
    /// À appeler juste après GameLauncher.Launch. Sans effet si le jeu est déjà suivi ou si son
    /// dossier d'installation est inconnu. Ne bloque pas.
    /// </summary>
    public void Track(Game game)
    {
        if (game is null || string.IsNullOrWhiteSpace(game.Id)) return;

        var tracked = TrackedGame.Create(game);
        if (tracked is null) return;

        lock (_gate)
        {
            if (_disposed || !_tracked.TryAdd(tracked.Id, tracked)) return;
            if (_loopRunning) return;
            _loopRunning = true;
        }

        _ = Task.Run(RunAsync);
    }

    /// <summary>
    /// Arrête le suivi. Les sessions en cours sont enregistrées jusqu'à maintenant (sans lever
    /// SessionEnded : l'application se ferme).
    /// </summary>
    public void Dispose()
    {
        List<TrackedGame> open;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            open = _tracked.Values.Where(t => t.Started).ToList();
            _tracked.Clear();
        }

        _stop.Cancel();
        if (open.Count == 0) return;

        // Jeu encore ouvert : il compte jusqu'à maintenant ; sinon jusqu'au dernier relevé où il vivait.
        var alive = FindAlive(open);
        var now = DateTime.Now;
        foreach (var tracked in open)
        {
            var session = tracked.ToSession(alive.Contains(tracked) ? now : tracked.LastSeen);
            if (session.Duration >= MinimumSession) Record(session);
        }
    }

    // ---- Boucle de fond : une seule pour tous les jeux suivis ----

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                try { Poll(); }
                catch { /* un relevé raté ne doit pas arrêter le suivi */ }

                lock (_gate)
                {
                    if (_disposed || _tracked.Count == 0)
                    {
                        _loopRunning = false;
                        return;
                    }
                }

                await Task.Delay(PollInterval, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose
        }
    }

    private void Poll()
    {
        TrackedGame[] targets;
        lock (_gate)
        {
            if (_disposed) return;
            targets = [.. _tracked.Values];
        }
        if (targets.Length == 0) return;

        var alive = FindAlive(targets);
        var now = DateTime.Now;
        var tick = Environment.TickCount64;

        List<Game>? started = null;
        List<(Game Game, PlaySession Session)>? ended = null;

        lock (_gate)
        {
            if (_disposed) return;

            foreach (var tracked in targets)
            {
                if (alive.Contains(tracked))
                {
                    if (!tracked.Started)
                    {
                        tracked.Started = true;
                        tracked.FirstSeen = now;
                        (started ??= []).Add(tracked.Game);
                    }
                    tracked.LastSeen = now;
                    tracked.LastSeenTick = tick;
                }
                else if (tracked.Started)
                {
                    // Un amorceur peut se fermer avant que le vrai jeu n'apparaisse : on laisse un délai.
                    if (tick - tracked.LastSeenTick < ExitGrace.TotalMilliseconds) continue;

                    var session = tracked.ToSession(tracked.LastSeen);
                    if (session.Duration >= MinimumSession)
                    {
                        _tracked.Remove(tracked.Id);
                        (ended ??= []).Add((tracked.Game, session));
                    }
                    else if (tick - tracked.TrackedAtTick < DetectionWindow.TotalMilliseconds)
                    {
                        // Trop court pour être une partie (lanceur refermé, mise à jour...) : on guette encore.
                        tracked.Started = false;
                    }
                    else
                    {
                        _tracked.Remove(tracked.Id);
                    }
                }
                else if (tick - tracked.TrackedAtTick >= DetectionWindow.TotalMilliseconds)
                {
                    _tracked.Remove(tracked.Id);
                }
            }
        }

        // Hors verrou : les abonnés peuvent rappeler IsRunning ou Track.
        if (started is not null)
            foreach (var game in started)
                Raise(SessionStarted, handler => handler(game));

        if (ended is not null)
        {
            foreach (var (game, session) in ended)
            {
                Record(session);
                Raise(SessionEnded, handler => handler(game, session));
            }
        }
    }

    private void Record(PlaySession session)
    {
        try { _activity.Add(session); }
        catch { /* un abonné de SessionAdded a levé : la session est déjà enregistrée */ }
    }

    private static void Raise<T>(T? handlers, Action<T> invoke) where T : Delegate
    {
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try { invoke((T)handler); }
            catch { /* un abonné défaillant ne doit ni arrêter la boucle ni priver les autres */ }
        }
    }

    private static HashSet<TrackedGame> FindAlive(IReadOnlyList<TrackedGame> targets)
    {
        var alive = new HashSet<TrackedGame>();
        foreach (var path in RunningProcesses.ImagePaths())
        {
            foreach (var tracked in targets)
                if (tracked.Owns(path)) alive.Add(tracked);

            if (alive.Count == targets.Count) break;
        }
        return alive;
    }

    // ---- Un jeu suivi ----

    private sealed class TrackedGame
    {
        private readonly string _root;            // dossier du jeu, séparateur final inclus
        private readonly string? _mainExecutable; // exécutable principal, s'il est dans ce dossier
        private readonly bool _mainExecutableOnly;

        private TrackedGame(Game game, string root, string? mainExecutable, bool mainExecutableOnly)
        {
            Game = game;
            Id = game.Id;
            _root = root;
            _mainExecutable = mainExecutable;
            _mainExecutableOnly = mainExecutableOnly;
        }

        public Game Game { get; }

        public string Id { get; }

        public long TrackedAtTick { get; } = Environment.TickCount64;

        public bool Started { get; set; }

        public DateTime FirstSeen { get; set; }

        public DateTime LastSeen { get; set; }

        public long LastSeenTick { get; set; }

        /// <summary>Null si rien ne permet de reconnaître les processus du jeu.</summary>
        public static TrackedGame? Create(Game game)
        {
            var executable = FullPath(game.ExecutablePath);
            var root = FullPath(game.InstallPath);

            if (root is null)
            {
                // Les jeux Riot se lancent par le Riot Client : le dossier de l'exécutable n'est pas celui du jeu.
                if (executable is null || game.Source == GameSource.Riot) return null;
                root = Path.GetDirectoryName(executable);
                if (string.IsNullOrEmpty(root)) return null;
            }

            root = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;

            var mainExecutable = executable is not null && executable.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? executable
                : null;

            // Dossier fourre-tout (racine de disque, Bureau, Windows...) : seul l'exécutable exact fait foi.
            var tooBroad = IsTooBroad(root);
            if (tooBroad && mainExecutable is null) return null;

            return new TrackedGame(game, root, mainExecutable, tooBroad);
        }

        public bool Owns(string processPath)
        {
            if (_mainExecutable is not null && processPath.Equals(_mainExecutable, StringComparison.OrdinalIgnoreCase))
                return true;
            if (_mainExecutableOnly || !processPath.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
                return false;

            // Rapporteurs de plantage, anti-triche, navigateurs embarqués : ils survivent parfois au jeu.
            return !ScanHelpers.IsIgnoredExecutable(Path.GetFileNameWithoutExtension(processPath));
        }

        public PlaySession ToSession(DateTime end) => new() { GameId = Id, Start = FirstSeen, End = end };

        private static string? FullPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetFullPath(path.Trim().Trim('"')); }
            catch { return null; }
        }

        private static bool IsTooBroad(string rootWithSeparator)
        {
            var root = Path.TrimEndingDirectorySeparator(rootWithSeparator);
            if (root.Length == 0 || Path.GetPathRoot(rootWithSeparator) == rootWithSeparator) return true;

            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (windows.Length > 0 &&
                (root.Equals(windows, StringComparison.OrdinalIgnoreCase) ||
                 root.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                return true;

            Environment.SpecialFolder[] sharedFolders =
            [
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolder.DesktopDirectory,
                Environment.SpecialFolder.CommonDesktopDirectory,
            ];
            foreach (var folder in sharedFolders)
                if (root.Equals(Environment.GetFolderPath(folder), StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }
    }

    // ---- Chemins des processus en cours ----

    /// <summary>
    /// QueryFullProcessImageName avec un accès limité : bien plus rapide que Process.MainModule, et
    /// fonctionne aussi pour les processus élevés ou d'une autre architecture.
    /// </summary>
    private static class RunningProcesses
    {
        private const int ProcessQueryLimitedInformation = 0x1000;

        /// <summary>Chemin de l'exécutable de chaque processus lisible ; les autres sont ignorés.</summary>
        public static IEnumerable<string> ImagePaths()
        {
            var ids = new int[1024];
            int count;
            while (true)
            {
                if (!EnumProcesses(ids, ids.Length * sizeof(int), out var bytes)) yield break;
                count = bytes / sizeof(int);
                if (count < ids.Length) break;
                ids = new int[ids.Length * 2];
            }

            var buffer = new char[1024];
            for (var i = 0; i < count; i++)
            {
                if (ids[i] == 0) continue;

                // Processus système, d'un autre utilisateur ou déjà terminé : l'ouverture échoue, on passe.
                using var process = OpenProcess(ProcessQueryLimitedInformation, false, ids[i]);
                if (process.IsInvalid) continue;

                var length = buffer.Length;
                if (QueryFullProcessImageName(process, 0, buffer, ref length) && length > 0)
                    yield return new string(buffer, 0, length);
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "K32EnumProcesses", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumProcesses([Out] int[] processIds, int sizeInBytes, out int bytesReturned);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(
            int access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(
            SafeProcessHandle process, int flags, [Out] char[] buffer, ref int size);
    }
}
