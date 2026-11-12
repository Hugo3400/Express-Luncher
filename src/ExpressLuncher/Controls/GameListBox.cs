using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ExpressLuncher.Controls;

/// <summary>
/// ListBox dont le panneau est un <see cref="VirtualizingTilePanel"/> : navigation au clavier par index
/// (flèches, Début/Fin, Page préc./suiv.) et activation de l'élément sélectionné par Entrée.
/// </summary>
public sealed class GameListBox : ListBox
{
    public static readonly DependencyProperty ActivateCommandProperty = DependencyProperty.Register(
        nameof(ActivateCommand), typeof(ICommand), typeof(GameListBox));

    private VirtualizingTilePanel? _panel;

    /// <summary>Commande exécutée avec l'élément sélectionné quand l'utilisateur appuie sur Entrée.</summary>
    public ICommand? ActivateCommand
    {
        get => (ICommand?)GetValue(ActivateCommandProperty);
        set => SetValue(ActivateCommandProperty, value);
    }

    private VirtualizingTilePanel? Panel => _panel ??= FindPanel(this);

    public void ScrollToTop() => Panel?.ScrollToTop();

    /// <summary>Donne le focus clavier à l'élément sélectionné (ou au premier), en le rendant visible.</summary>
    public void FocusSelected()
    {
        if (Items.Count == 0) return;
        var index = SelectedIndex >= 0 ? SelectedIndex : 0;
        MoveTo(index);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            if (e.Key == Key.Enter && SelectedItem is { } item && ActivateCommand is { } command)
            {
                if (command.CanExecute(item)) command.Execute(item);
                e.Handled = true;
                return;
            }

            if (TryNavigate(e.Key))
            {
                e.Handled = true;
                return;
            }
        }
        base.OnKeyDown(e);
    }

    private bool TryNavigate(Key key)
    {
        var panel = Panel;
        if (panel is null) return false;

        var count = Math.Min(Items.Count, panel.VisibleItemCount);
        if (count == 0) return false;

        var columns = panel.Columns;
        var current = SelectedIndex;
        if (current < 0 || current >= count)
        {
            if (key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
            {
                MoveTo(0);
                return true;
            }
            return false;
        }

        int target;
        switch (key)
        {
            case Key.Left: target = current - 1; break;
            case Key.Right: target = current + 1; break;
            case Key.Up: target = current - columns; break;
            case Key.Down:
                target = current + columns;
                // Dernière ligne incomplète : on descend quand même sur son dernier élément.
                if (target >= count && current / columns < (count - 1) / columns) target = count - 1;
                break;
            case Key.Home: target = 0; break;
            case Key.End: target = count - 1; break;
            case Key.PageUp: target = Math.Max(current % columns, current - columns * panel.RowsPerPage); break;
            case Key.PageDown: target = Math.Min(count - 1, current + columns * panel.RowsPerPage); break;
            default: return false;
        }

        // En bout de rangée « en ligne », on laisse les flèches verticales quitter la liste.
        if (target < 0 || target >= count) return key is Key.Left or Key.Right || panel.ScrollOwner is not null;

        MoveTo(target);
        return true;
    }

    private void MoveTo(int index)
    {
        SelectedIndex = index;
        Panel?.ScrollIndexIntoView(index);
        UpdateLayout();
        (ItemContainerGenerator.ContainerFromIndex(index) as UIElement)?.Focus();
    }

    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);
        if (e.Action != NotifyCollectionChangedAction.Reset || !IsVisible) return;

        // Un Reset détruit le conteneur qui avait le focus : on le rend à l'élément sélectionné,
        // seulement si le focus n'est pas parti ailleurs (champ de recherche, bouton...).
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!IsVisible || SelectedIndex < 0) return;
            var focused = Keyboard.FocusedElement;
            if (focused is null || focused is Window) MoveTo(SelectedIndex);
        });
    }

    private static VirtualizingTilePanel? FindPanel(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is VirtualizingTilePanel panel) return panel;
            if (FindPanel(child) is { } nested) return nested;
        }
        return null;
    }
}
