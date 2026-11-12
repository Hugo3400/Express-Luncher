using System.Windows.Input;
using System.Windows.Media;
using ExpressLuncher.Core.Launching;
using ExpressLuncher.Core.Models;
using ExpressLuncher.Mvvm;
using ExpressLuncher.Services;

namespace ExpressLuncher.ViewModels;

/// <summary>Actions sur un jeu, fournies par la coquille de l'application.</summary>
public interface IGameActions
{
    void Play(GameViewModel game);
    void OpenDetails(GameViewModel game);
    void ToggleFavorite(GameViewModel game);
    void ToggleHidden(GameViewModel game);
    void OpenFolder(GameViewModel game);
    void Remove(GameViewModel game);
}

/// <summary>Un jeu tel qu'affiché : carte, bandeau, fiche. Une seule instance par jeu de la bibliothèque.</summary>
public sealed class GameViewModel : ObservableObject
{
    private static readonly TimeSpan RecentWindow = TimeSpan.FromDays(30);

    private readonly ImageLoader _images;
    private readonly LazyImage _cover;
    private readonly LazyImage _hero;
    private readonly LazyImage _logo;

    private Brush? _tileBrush;
    private ImageSource? _icon;
    private string? _iconSource;
    private bool? _canOpenFolder;
    private bool _isRunning;
    private TimeSpan _playtime;

    public GameViewModel(Game game, IGameActions actions, ImageLoader images)
    {
        Game = game;
        _images = images;

        _cover = new LazyImage(images, ImageKind.Cover, () => Game.CoverPath, () =>
        {
            OnPropertyChanged(nameof(Cover));
            OnPropertyChanged(nameof(ShowFallback));
            OnPropertyChanged(nameof(Icon));
        });
        _hero = new LazyImage(images, ImageKind.Hero, () => Game.HeroPath, () => OnPropertyChanged(nameof(Hero)));
        _logo = new LazyImage(images, ImageKind.Logo, () => Game.LogoPath, () => OnPropertyChanged(nameof(Logo)));

        PlayCommand = new RelayCommand(() => actions.Play(this));
        OpenDetailsCommand = new RelayCommand(() => actions.OpenDetails(this));
        ToggleFavoriteCommand = new RelayCommand(() => actions.ToggleFavorite(this));
        ToggleHiddenCommand = new RelayCommand(() => actions.ToggleHidden(this));
        OpenFolderCommand = new RelayCommand(() => actions.OpenFolder(this), () => CanOpenFolder);
        RemoveCommand = new RelayCommand(() => actions.Remove(this), () => IsManual);
    }

    public Game Game { get; }

    public ICommand PlayCommand { get; }
    public ICommand OpenDetailsCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand ToggleHiddenCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public ICommand RemoveCommand { get; }

    public string Name => Game.Name;
    public GameSource Source => Game.Source;
    public string StoreName => StoreCatalog.Get(Game.Source).Name;
    public Brush StoreBrush => StoreCatalog.Get(Game.Source).Brush;

    public bool IsFavorite => Game.IsFavorite;
    public bool IsHidden => Game.IsHidden;
    public bool IsManual => Game.Source == GameSource.Manual;

    public string FavoriteActionText => IsFavorite ? "Retirer des favoris" : "Ajouter aux favoris";
    public string HiddenActionText => IsHidden ? "Afficher dans la bibliothèque" : "Masquer";

    /// <summary>Vrai tant qu'une session de ce jeu est en cours (suivi du temps de jeu).</summary>
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (!SetProperty(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(SecondaryLine));
        }
    }

    /// <summary>Temps de jeu total (compteur de la boutique ou temps mesuré ici), fourni par la coquille.</summary>
    public TimeSpan Playtime
    {
        get => _playtime;
        set
        {
            if (!SetProperty(ref _playtime, value)) return;
            OnPropertyChanged(nameof(PlaytimeText));
            OnPropertyChanged(nameof(SecondaryLine));
        }
    }

    public bool HasPlaytime => _playtime >= TimeSpan.FromMinutes(1);

    public string PlaytimeText => HasPlaytime ? Fr.Duration(_playtime) : "—";

    public DateTime? LastPlayed
    {
        get
        {
            var launcher = Game.LastPlayed;
            var store = Game.StoreLastPlayed;
            if (launcher is null) return store;
            return store > launcher ? store : launcher;
        }
    }

    public bool WasPlayedRecently => LastPlayed is { } date && DateTime.Now - date <= RecentWindow;

    public string LastPlayedText => LastPlayed is { } date ? Fr.Relative(date) : "Jamais";

    /// <summary>Ligne sous le nom d'une carte : boutique, puis temps de jeu ou dernier lancement.</summary>
    public string SecondaryLine
    {
        get
        {
            if (_isRunning) return "En cours";

            var detail = (HasPlaytime, LastPlayed) switch
            {
                (true, { } date) => $"{Fr.Duration(_playtime)} · {Fr.Relative(date)}",
                (true, null) => Fr.Duration(_playtime),
                (false, { } date) => Fr.Relative(date),
                _ => null,
            };
            return detail is null ? StoreName : $"{StoreName} · {detail}";
        }
    }

    public string? SizeText => Game.SizeBytes is > 0 ? Fr.Size(Game.SizeBytes.Value) : null;

    public string? InstallPath => string.IsNullOrWhiteSpace(Game.InstallPath) ? null : Game.InstallPath;

    public bool CanOpenFolder => _canOpenFolder ??= GameLauncher.CanOpenInstallFolder(Game);

    // ---- Illustrations (chargées à la demande, quand une vue les lit) ----

    public ImageSource? Cover => _cover.Value;

    public ImageSource? Hero => _hero.Value;

    public ImageSource? Logo => _logo.Value;

    /// <summary>Vrai quand il n'y a pas de jaquette exploitable : la tuile montre l'icône et le nom.</summary>
    public bool ShowFallback => _cover.IsMissing;

    /// <summary>Icône de l'exécutable, pour la tuile de repli uniquement.</summary>
    public ImageSource? Icon
    {
        get
        {
            var executable = Game.ExecutablePath;
            if (ShowFallback && !string.IsNullOrWhiteSpace(executable) && _iconSource != executable)
            {
                _iconSource = executable;
                LoadIcon(executable);
            }
            return _icon;
        }
    }

    private async void LoadIcon(string executable)
    {
        var icon = await _images.LoadIconAsync(executable);
        if (_iconSource != executable) return;
        _icon = icon;
        OnPropertyChanged(nameof(Icon));
    }

    /// <summary>Dégradé propre au jeu (stable d'une exécution à l'autre) pour la tuile de repli.</summary>
    public Brush TileBrush => _tileBrush ??= CreateTileBrush(Game.Id.Length > 0 ? Game.Id : Game.Name);

    /// <summary>À appeler après toute modification du Game sous-jacent : rafraîchit toutes les liaisons.</summary>
    public void Refresh()
    {
        _canOpenFolder = null;
        if (_iconSource != Game.ExecutablePath) _icon = null;
        OnPropertyChanged(string.Empty);
        OpenFolderCommand.RaiseCanExecuteChanged();
    }

    private static Brush CreateTileBrush(string seed)
    {
        // FNV-1a : string.GetHashCode change à chaque lancement.
        var hash = 2166136261u;
        foreach (var c in seed)
        {
            hash ^= c;
            hash *= 16777619u;
        }

        var hue = hash % 360;
        var brush = new LinearGradientBrush(
            FromHsl(hue, 0.42, 0.30),
            FromHsl((hue + 36) % 360, 0.50, 0.13),
            new System.Windows.Point(0, 0),
            new System.Windows.Point(1, 1));
        brush.Freeze();
        return brush;
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        var c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = lightness - c / 2;
        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    /// <summary>
    /// Image chargée à la première lecture et tenue par référence faible : seules les vues qui
    /// l'affichent (et le cache du chargeur) la gardent en mémoire. Si le chemin change, l'ancienne
    /// image reste affichée jusqu'à l'arrivée de la nouvelle.
    /// </summary>
    private sealed class LazyImage(ImageLoader loader, ImageKind kind, Func<string?> path, Action changed)
    {
        private WeakReference<ImageSource>? _image;
        private string? _resolvedPath;
        private bool _missing;
        private bool _loading;

        public bool IsMissing
        {
            get
            {
                var current = path();
                return string.IsNullOrWhiteSpace(current) || (_missing && _resolvedPath == current);
            }
        }

        public ImageSource? Value
        {
            get
            {
                var current = path();
                if (string.IsNullOrWhiteSpace(current)) return null;

                ImageSource? image = null;
                _image?.TryGetTarget(out image);

                if (_resolvedPath == current)
                {
                    if (image is not null || _missing) return image;
                }
                Load(current);
                return image;
            }
        }

        private async void Load(string target)
        {
            if (_loading) return;
            _loading = true;

            ImageSource? image = null;
            try
            {
                image = await loader.LoadAsync(target, kind);
            }
            catch
            {
                // image illisible : traitée comme absente
            }

            _loading = false;
            _resolvedPath = target;
            _missing = image is null;
            _image = image is null ? null : new WeakReference<ImageSource>(image);
            changed();
            GC.KeepAlive(image);
        }
    }
}
