using System.Collections.ObjectModel;
using System.Collections.Specialized;
using NUnit.Framework;
using Pulsar4X.UI.Core;

namespace Pulsar4X.UI.Tests;

public class CollectionSyncTests
{
    private sealed record Row(int Id, string Value);

    [Test]
    public void Unchanged_rows_are_left_alone()
    {
        var target = new ObservableCollection<Row> { new(1, "a"), new(2, "b") };
        var first = target[0];
        int changes = 0;
        target.CollectionChanged += (_, _) => changes++;

        CollectionSync.Sync(target, new[] { new Row(1, "a"), new Row(2, "b") }, r => r.Id);

        Assert.That(changes, Is.Zero);
        Assert.That(target[0], Is.SameAs(first));
    }

    [Test]
    public void Changed_rows_are_replaced_in_place()
    {
        var target = new ObservableCollection<Row> { new(1, "a"), new(2, "b") };
        var actions = new List<NotifyCollectionChangedAction>();
        target.CollectionChanged += (_, e) => actions.Add(e.Action);

        CollectionSync.Sync(target, new[] { new Row(1, "a"), new Row(2, "B") }, r => r.Id);

        Assert.That(target.Select(r => r.Value), Is.EqualTo(new[] { "a", "B" }));
        Assert.That(actions, Is.EqualTo(new[] { NotifyCollectionChangedAction.Replace }));
    }

    [Test]
    public void Inserts_removes_and_reorders()
    {
        var target = new ObservableCollection<Row> { new(1, "a"), new(2, "b"), new(3, "c") };

        CollectionSync.Sync(target, new[] { new Row(3, "c"), new Row(4, "d"), new Row(1, "a") }, r => r.Id);

        Assert.That(target.Select(r => r.Id), Is.EqualTo(new[] { 3, 4, 1 }));
    }

    private sealed class Item
    {
        public Item(int id) => Id = id;
        public int Id { get; }
        public string Value { get; set; } = "";
    }

    [Test]
    public void Mutable_items_are_updated_not_recreated()
    {
        var target = new ObservableCollection<Item>();
        CollectionSync.Sync(target, new[] { new Row(1, "a"), new Row(2, "b") }, r => r.Id, i => i.Id,
            r => new Item(r.Id) { Value = r.Value }, (i, r) => i.Value = r.Value);
        var kept = target[1];

        CollectionSync.Sync(target, new[] { new Row(2, "B") }, r => r.Id, i => i.Id,
            r => new Item(r.Id) { Value = r.Value }, (i, r) => i.Value = r.Value);

        Assert.That(target, Has.Count.EqualTo(1));
        Assert.That(target[0], Is.SameAs(kept));
        Assert.That(kept.Value, Is.EqualTo("B"));
    }
}
