using System.Text.Json;
using System.Text.Json.Serialization;
using ExpressLuncher.Core.Models;

namespace ExpressLuncher.Core.Activity;

/// <summary>Une session de jeu mesurée par le launcher.</summary>
public sealed class PlaySession
{
    public string GameId { get; set; } = "";

    public DateTime Start { get; set; }

    public DateTime End { get; set; }

    [JsonIgnore]
    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;
}

/// <summary>Temps de jeu cumulé sur une journée (toutes les journées de la période, même vides).</summary>
public sealed record DailyPlaytime(DateOnly Day, TimeSpan Total);

/// <summary>Temps de jeu cumulé d'un jeu sur une période.</summary>
public sealed record GamePlaytime(string GameId, TimeSpan Total, int Sessions);

/// <summary>
/// Historique des sessions de jeu, persisté en JSON (%AppData%\ExpressLuncher\activity.json),
/// et statistiques dérivées. Thread-safe : le suivi du temps de jeu y écrit depuis un thread de fond.
/// Une session est rattachée au jour de son début.
/// </summary>
public sealed class ActivityStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly List<PlaySession> _sessions = [];

    public ActivityStore(string? filePath = null) => _filePath = filePath ?? AppInfo.ActivityFile;

    /// <summary>Levé après l'ajout d'une session (sur le thread appelant de <see cref="Add"/>).</summary>
    public event Action<PlaySession>? SessionAdded;

    /// <summary>Copie des sessions, de la plus ancienne à la plus récente.</summary>
    public IReadOnlyList<PlaySession> Sessions
    {
        get { lock (_gate) return _sessions.ToList(); }
    }

    /// <summary>Charge l'historique. Un fichier absent ou illisible donne un historique vide.</summary>
    public void Load()
    {
        lock (_gate)
        {
            _sessions.Clear();
            try
            {
                if (!File.Exists(_filePath)) return;
                var loaded = JsonSerializer.Deserialize<List<PlaySession>>(File.ReadAllText(_filePath), JsonOptions);
                if (loaded is not null)
                    _sessions.AddRange(loaded.Where(s => !string.IsNullOrWhiteSpace(s.GameId) && s.End > s.Start)
                                             .OrderBy(s => s.Start));
            }
            catch
            {
                try { File.Copy(_filePath, _filePath + ".bak", overwrite: true); } catch { /* ignoré */ }
            }
        }
    }

    /// <summary>Ajoute une session et sauvegarde aussitôt.</summary>
    public void Add(PlaySession session)
    {
        if (string.IsNullOrWhiteSpace(session.GameId) || session.End <= session.Start) return;

        lock (_gate)
        {
            _sessions.Add(session);
            SaveLocked();
        }
        SessionAdded?.Invoke(session);
    }

    private void SaveLocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_sessions, JsonOptions));
            File.Move(temp, _filePath, overwrite: true);
        }
        catch
        {
            // Disque plein ou fichier verrouillé : la session reste en mémoire, on réessaiera à la prochaine.
        }
    }

    /// <summary>Temps mesuré par le launcher pour un jeu.</summary>
    public TimeSpan TrackedFor(string gameId)
    {
        lock (_gate)
            return Sum(_sessions.Where(s => SameId(s.GameId, gameId)));
    }

    /// <summary>
    /// Temps de jeu total à afficher : le compteur de la boutique quand il existe (il inclut déjà
    /// les sessions lancées d'ici), sinon le temps mesuré par le launcher.
    /// </summary>
    public TimeSpan TotalFor(Game game)
    {
        var tracked = TrackedFor(game.Id);
        var store = TimeSpan.FromSeconds(game.StorePlaytimeSeconds ?? 0);
        return store > tracked ? store : tracked;
    }

    /// <summary>Sessions d'un jeu, de la plus récente à la plus ancienne.</summary>
    public IReadOnlyList<PlaySession> SessionsFor(string gameId)
    {
        lock (_gate)
            return _sessions.Where(s => SameId(s.GameId, gameId)).OrderByDescending(s => s.Start).ToList();
    }

    public TimeSpan PlaytimeSince(DateTime since)
    {
        lock (_gate)
            return Sum(_sessions.Where(s => s.Start >= since));
    }

    public int SessionCountSince(DateTime since)
    {
        lock (_gate)
            return _sessions.Count(s => s.Start >= since);
    }

    /// <summary>
    /// Nombre de jours consécutifs avec au moins une session, en remontant depuis aujourd'hui
    /// (ou depuis hier si rien n'a encore été joué aujourd'hui).
    /// </summary>
    public int CurrentStreakDays()
    {
        HashSet<DateOnly> days;
        lock (_gate)
            days = _sessions.Select(s => DateOnly.FromDateTime(s.Start)).ToHashSet();

        var day = DateOnly.FromDateTime(DateTime.Now);
        if (!days.Contains(day)) day = day.AddDays(-1);

        var streak = 0;
        while (days.Contains(day))
        {
            streak++;
            day = day.AddDays(-1);
        }
        return streak;
    }

    /// <summary>Temps de jeu par jour sur les <paramref name="days"/> derniers jours, du plus ancien à aujourd'hui.</summary>
    public IReadOnlyList<DailyPlaytime> DailyTotals(int days)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var first = today.AddDays(-(days - 1));

        Dictionary<DateOnly, TimeSpan> totals;
        lock (_gate)
            totals = _sessions
                .Where(s => DateOnly.FromDateTime(s.Start) >= first)
                .GroupBy(s => DateOnly.FromDateTime(s.Start))
                .ToDictionary(g => g.Key, Sum);

        var result = new List<DailyPlaytime>(days);
        for (var day = first; day <= today; day = day.AddDays(1))
            result.Add(new DailyPlaytime(day, totals.GetValueOrDefault(day)));
        return result;
    }

    /// <summary>Jeux les plus joués depuis <paramref name="since"/>, par temps décroissant.</summary>
    public IReadOnlyList<GamePlaytime> TopGamesSince(DateTime since, int max)
    {
        lock (_gate)
            return _sessions
                .Where(s => s.Start >= since)
                .GroupBy(s => s.GameId, StringComparer.OrdinalIgnoreCase)
                .Select(g => new GamePlaytime(g.Key, Sum(g), g.Count()))
                .OrderByDescending(g => g.Total)
                .Take(max)
                .ToList();
    }

    private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static TimeSpan Sum(IEnumerable<PlaySession> sessions) =>
        TimeSpan.FromTicks(sessions.Sum(s => s.Duration.Ticks));
}
