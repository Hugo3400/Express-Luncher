using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ExpressLuncher.Mvvm;

/// <summary>Collection observable qui sait se synchroniser sur une liste cible en limitant les notifications.</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T> where T : notnull
{
    private const int MaxIncrementalChanges = 24;

    /// <summary>
    /// Aligne le contenu sur <paramref name="target"/>. Rien n'est notifié si la séquence est identique ;
    /// quelques ajouts/retraits sans changement d'ordre sont appliqués un à un (la vue garde son défilement
    /// et sa sélection) ; sinon un seul Reset est émis.
    /// </summary>
    public void SyncTo(IReadOnlyList<T> target)
    {
        if (Items.SequenceEqual(target)) return;

        var targetSet = new HashSet<T>(target);
        var currentSet = new HashSet<T>(Items);
        var kept = Items.Where(targetSet.Contains).ToList();
        var changes = (Items.Count - kept.Count) + (target.Count - kept.Count);

        if (kept.Count > 0 && changes <= MaxIncrementalChanges && kept.SequenceEqual(target.Where(currentSet.Contains)))
        {
            for (var i = Count - 1; i >= 0; i--)
                if (!targetSet.Contains(this[i])) RemoveAt(i);

            for (var i = 0; i < target.Count; i++)
                if (i >= Count || !EqualityComparer<T>.Default.Equals(this[i], target[i])) Insert(i, target[i]);
            return;
        }

        Items.Clear();
        foreach (var item in target) Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
