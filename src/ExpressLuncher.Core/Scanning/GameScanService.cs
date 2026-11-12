using ExpressLuncher.Core.Models;
using ExpressLuncher.Core.Scanning.Scanners;

namespace ExpressLuncher.Core.Scanning;

/// <summary>Avancement d'un scan : un événement à chaque boutique terminée.</summary>
public sealed record ScanProgress(string ScannerName, int Completed, int Total, int GamesFound);

/// <summary>Échec d'un scanner (les autres boutiques ne sont pas affectées).</summary>
public sealed record ScanError(GameSource Source, string ScannerName, string Message);

/// <summary>
/// Résultat d'un scan complet. <see cref="CompletedSources"/> liste les boutiques scannées sans erreur :
/// seules celles-ci peuvent faire disparaître des jeux de la bibliothèque lors de la fusion.
/// </summary>
public sealed record ScanResult(
    IReadOnlyList<Game> Games,
    IReadOnlySet<GameSource> CompletedSources,
    IReadOnlyList<ScanError> Errors);

/// <summary>Lance tous les scanners en parallèle et agrège leurs résultats.</summary>
public sealed class GameScanService
{
    public GameScanService(IEnumerable<IGameScanner> scanners) => Scanners = scanners.ToList();

    public IReadOnlyList<IGameScanner> Scanners { get; }

    /// <summary>Service avec tous les scanners intégrés.</summary>
    public static GameScanService CreateDefault() => new(
    [
        new SteamScanner(),
        new EpicScanner(),
        new GogScanner(),
        new UbisoftScanner(),
        new EaScanner(),
        new BattleNetScanner(),
        new RiotScanner(),
        new XboxScanner(),
        new RockstarScanner(),
        new AmazonScanner(),
        new ItchScanner(),
        new StandaloneScanner(),
    ]);

    public async Task<ScanResult> ScanAllAsync(
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var games = new List<Game>();
        var completed = new HashSet<GameSource>();
        var errors = new List<ScanError>();
        var gate = new object();
        var done = 0;

        var tasks = Scanners.Select(scanner => Task.Run(async () =>
        {
            IReadOnlyList<Game> found = [];
            ScanError? error = null;
            try
            {
                found = await scanner.ScanAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                error = new ScanError(scanner.Source, scanner.DisplayName, ex.Message);
            }

            lock (gate)
            {
                if (error is null)
                {
                    games.AddRange(found);
                    completed.Add(scanner.Source);
                }
                else
                {
                    errors.Add(error);
                }
                done++;
                progress?.Report(new ScanProgress(scanner.DisplayName, done, Scanners.Count, games.Count));
            }
        }, cancellationToken)).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Une boutique en erreur ne doit jamais compter comme "terminée", même si un autre
        // scanner de la même source a réussi.
        foreach (var error in errors) completed.Remove(error.Source);

        var unique = games
            .Where(g => !string.IsNullOrWhiteSpace(g.Id) && !string.IsNullOrWhiteSpace(g.Name))
            .GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new ScanResult(unique, completed, errors);
    }
}
