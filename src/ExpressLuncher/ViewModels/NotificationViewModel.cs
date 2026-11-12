using System.Windows.Input;
using System.Windows.Threading;
using ExpressLuncher.Mvvm;

namespace ExpressLuncher.ViewModels;

public enum NotificationKind
{
    Info,
    Success,
    Error,
}

/// <summary>Message bref affiché en bas de la fenêtre, qui s'efface tout seul.</summary>
public sealed class NotificationViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer = new();
    private string _message = "";
    private NotificationKind _kind;
    private bool _isVisible;

    public NotificationViewModel()
    {
        _timer.Tick += (_, _) => Dismiss();
        DismissCommand = new RelayCommand(Dismiss);
    }

    public ICommand DismissCommand { get; }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        private set => SetProperty(ref _isVisible, value);
    }

    public bool IsError => _kind == NotificationKind.Error;
    public bool IsSuccess => _kind == NotificationKind.Success;

    public void Show(string message, NotificationKind kind = NotificationKind.Info)
    {
        _kind = kind;
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsSuccess));
        Message = message;
        IsVisible = true;

        // Une erreur reste plus longtemps : elle demande à être lue.
        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(kind == NotificationKind.Error ? 9 : 4.5);
        _timer.Start();
    }

    public void Dismiss()
    {
        _timer.Stop();
        IsVisible = false;
    }
}
