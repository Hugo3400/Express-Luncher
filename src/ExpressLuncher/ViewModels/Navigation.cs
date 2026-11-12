using System.Windows.Media;
using ExpressLuncher.Core.Models;
using ExpressLuncher.Mvvm;

namespace ExpressLuncher.ViewModels;

public enum PageKind
{
    Home,
    Library,
    Detail,
    History,
    Settings,
}

public enum LibraryScope
{
    All,
    Favorites,
    Hidden,
}

/// <summary>Une destination de navigation ; l'historique « Retour » est une pile de routes.</summary>
public sealed record Route(
    PageKind Kind,
    LibraryScope Scope = LibraryScope.All,
    GameSource? Store = null,
    GameViewModel? Game = null);

/// <summary>Base des pages : une seule est active à la fois.</summary>
public abstract class PageViewModel : ObservableObject
{
    private bool _isActive;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value) && value) OnActivated();
        }
    }

    protected virtual void OnActivated()
    {
    }
}

/// <summary>Entrée de la barre latérale.</summary>
public sealed class NavItemViewModel : ObservableObject
{
    private string? _count;
    private bool _isSelected;

    public NavItemViewModel(string label, Route route, string? glyph = null, Brush? dot = null)
    {
        Label = label;
        Route = route;
        Glyph = glyph;
        Dot = dot;
    }

    public string Label { get; }
    public Route Route { get; }

    /// <summary>Glyphe Segoe Fluent Icons, ou null si l'entrée affiche une pastille de couleur.</summary>
    public string? Glyph { get; }
    public Brush? Dot { get; }

    public string? Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
