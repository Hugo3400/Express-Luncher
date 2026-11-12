using System.Windows.Input;
using ExpressLuncher.Core.Updates;
using ExpressLuncher.Mvvm;
using ExpressLuncher.Services;

namespace ExpressLuncher.ViewModels;

/// <summary>Vérification, téléchargement et installation des mises à jour du launcher.</summary>
public sealed class UpdateViewModel : ObservableObject
{
    private readonly IUpdateService _service;
    private readonly IShellService _shell;

    private UpdateInfo? _info;
    private bool _isBannerVisible;
    private bool _isDownloading;
    private bool _hasError;
    private bool _isChecking;
    private double _progress;
    private string _title = "";
    private string? _detail;
    private string? _checkStatus;

    public UpdateViewModel(IUpdateService service, IShellService shell)
    {
        _service = service;
        _shell = shell;

        InstallCommand = new AsyncRelayCommand(InstallAsync, () => _info is not null && !_isDownloading);
        CheckCommand = new AsyncRelayCommand(() => CheckAsync(manual: true), () => !_isChecking && !_isDownloading);
        ReleaseNotesCommand = new RelayCommand(() => { if (_info is not null) _shell.OpenUrl(_info.PageUrl); });
        DismissCommand = new RelayCommand(() => IsBannerVisible = false, () => !_isDownloading);
    }

    public AsyncRelayCommand InstallCommand { get; }
    public AsyncRelayCommand CheckCommand { get; }
    public ICommand ReleaseNotesCommand { get; }
    public RelayCommand DismissCommand { get; }

    public bool IsBannerVisible
    {
        get => _isBannerVisible;
        private set => SetProperty(ref _isBannerVisible, value);
    }

    /// <summary>« Version X.Y.Z disponible ».</summary>
    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    /// <summary>Taille du paquet, avancement ou message d'erreur.</summary>
    public string? Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (!SetProperty(ref _isDownloading, value)) return;
            InstallCommand.RaiseCanExecuteChanged();
            CheckCommand.RaiseCanExecuteChanged();
            DismissCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    /// <summary>Avancement du téléchargement, de 0 à 100.</summary>
    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            if (SetProperty(ref _isChecking, value)) CheckCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Résultat de la dernière vérification manuelle, affiché dans les paramètres.</summary>
    public string? CheckStatus
    {
        get => _checkStatus;
        private set => SetProperty(ref _checkStatus, value);
    }

    public async Task CheckAsync(bool manual)
    {
        if (IsChecking) return;
        IsChecking = true;
        if (manual) CheckStatus = "Recherche de mises à jour…";

        try
        {
            var info = await Task.Run(() => _service.CheckForUpdateAsync());
            if (info is null)
            {
                if (manual) CheckStatus = "Vous êtes à jour.";
                return;
            }

            _info = info;
            HasError = false;
            Title = $"Version {info.Version} disponible";
            Detail = info.SizeBytes > 0 ? $"Téléchargement de {Fr.Size(info.SizeBytes)}" : null;
            IsBannerVisible = true;
            InstallCommand.RaiseCanExecuteChanged();
            if (manual) CheckStatus = $"La version {info.Version} est disponible.";
        }
        catch (Exception ex)
        {
            if (manual) CheckStatus = $"Vérification impossible : {ex.Message}";
        }
        finally
        {
            IsChecking = false;
        }
    }

    private async Task InstallAsync()
    {
        if (_info is null) return;

        IsDownloading = true;
        HasError = false;
        Progress = 0;
        Detail = "Téléchargement… 0 %";
        try
        {
            var progress = new Progress<double>(value =>
            {
                if (!IsDownloading) return;
                Progress = Math.Clamp(value, 0, 1) * 100;
                Detail = $"Téléchargement… {Progress:0} %";
            });

            var package = await _service.DownloadAsync(_info, progress);
            Progress = 100;
            Detail = "Installation, le launcher va redémarrer…";
            _service.ApplyAndRestart(package);
            _shell.Shutdown();
        }
        catch (Exception ex)
        {
            IsDownloading = false;
            HasError = true;
            Detail = $"Échec de la mise à jour : {ex.Message}";
        }
        finally
        {
            IsDownloading = false;
        }
    }
}
