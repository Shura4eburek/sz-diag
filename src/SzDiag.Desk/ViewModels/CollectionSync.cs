using System.Collections.ObjectModel;

namespace SzDiag.Desk.ViewModels;

/// <summary>Синхронизация коллекции по ключу: существующие элементы обновляются на месте,
/// а не пересоздаются, — иначе каждый опрос раз в 2 с сбрасывал бы выделение и мигал списком.</summary>
public static class CollectionSync
{
    public static void Sync<TVm, TSrc>(ObservableCollection<TVm> target, IEnumerable<TSrc> source,
        Func<TSrc, string> srcKey, Func<TVm, string> vmKey, Func<TSrc, TVm> create, Action<TVm, TSrc> update)
    {
        var ordered = source.ToList();
        var keys = ordered.Select(srcKey).ToHashSet(StringComparer.Ordinal);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!keys.Contains(vmKey(target[i]))) target.RemoveAt(i);

        for (var i = 0; i < ordered.Count; i++)
        {
            var key = srcKey(ordered[i]);
            var existing = -1;
            for (var j = i; j < target.Count; j++)
                if (vmKey(target[j]) == key) { existing = j; break; }

            if (existing < 0) target.Insert(i, create(ordered[i]));
            else
            {
                if (existing != i) target.Move(existing, i);
                update(target[i], ordered[i]);
            }
        }
    }
}
