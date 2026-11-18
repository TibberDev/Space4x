using System.Collections.ObjectModel;

namespace Pulsar4X.UI.Core;

/// <summary>
/// Updates a bound collection in place from fresh data. Game state is re-pushed every tick, and
/// clearing and refilling a list would reset the table's scroll position and selection each time;
/// this keeps unchanged items untouched, replaces changed ones and only inserts, removes or moves
/// what actually differs.
/// </summary>
public static class CollectionSync
{
    /// <summary>Makes <paramref name="target"/> equal to <paramref name="source"/>, matching items by
    /// <paramref name="key"/> and replacing an item only when it is not equal to the new one (rows
    /// are flat records, so equality is by value).</summary>
    public static void Sync<T, TKey>(ObservableCollection<T> target, IReadOnlyList<T> source, Func<T, TKey> key)
        where TKey : notnull
    {
        var wanted = new HashSet<TKey>(source.Select(key));
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(key(target[i])))
                target.RemoveAt(i);
        }

        for (int i = 0; i < source.Count; i++)
        {
            var item = source[i];
            var k = key(item);
            if (i < target.Count && EqualityComparer<TKey>.Default.Equals(key(target[i]), k))
            {
                if (!EqualityComparer<T>.Default.Equals(target[i], item))
                    target[i] = item;
                continue;
            }

            int existing = -1;
            for (int j = i + 1; j < target.Count; j++)
            {
                if (EqualityComparer<TKey>.Default.Equals(key(target[j]), k))
                {
                    existing = j;
                    break;
                }
            }

            if (existing >= 0)
            {
                target.Move(existing, i);
                if (!EqualityComparer<T>.Default.Equals(target[i], item))
                    target[i] = item;
            }
            else
            {
                target.Insert(i, item);
            }
        }
    }

    /// <summary>Like <see cref="Sync{T,TKey}"/> for mutable view models: existing ones are updated in place
    /// with <paramref name="update"/>, missing ones created with <paramref name="create"/>.</summary>
    public static void Sync<TSource, TItem, TKey>(ObservableCollection<TItem> target, IReadOnlyList<TSource> source,
        Func<TSource, TKey> sourceKey, Func<TItem, TKey> itemKey, Func<TSource, TItem> create, Action<TItem, TSource> update)
        where TKey : notnull
    {
        var wanted = new HashSet<TKey>(source.Select(sourceKey));
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(itemKey(target[i])))
                target.RemoveAt(i);
        }

        for (int i = 0; i < source.Count; i++)
        {
            var k = sourceKey(source[i]);
            int existing = -1;
            for (int j = i; j < target.Count; j++)
            {
                if (EqualityComparer<TKey>.Default.Equals(itemKey(target[j]), k))
                {
                    existing = j;
                    break;
                }
            }

            if (existing < 0)
            {
                target.Insert(i, create(source[i]));
                continue;
            }
            if (existing != i) target.Move(existing, i);
            update(target[i], source[i]);
        }
    }
}
