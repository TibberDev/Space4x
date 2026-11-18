using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pulsar4X.Api;
using Pulsar4X.UI.Core.Colonies;

namespace Pulsar4X.UI.Core.Inbox;

/// <summary>
/// Every colony administrator's open suggestions in one list (Advise level), newest colony first by name.
/// Manage by exception: approve or dismiss here without visiting each colony.
/// </summary>
public sealed partial class InboxViewModel : ObservableObject
{
    public InboxViewModel(Func<GameCommand, Task<CommandResult>> submit) => Sink = new CommandSink(submit);

    public CommandSink Sink { get; }

    public ObservableCollection<SuggestionRow> Items { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header), nameof(IsEmpty))]
    private int _count;

    /// <summary>The tab header: "Inbox" or "Inbox (3)".</summary>
    public string Header => Count > 0 ? $"Inbox ({Count})" : "Inbox";

    public bool IsEmpty => Count == 0;

    public void Update(IClientGalaxy galaxy)
    {
        var rows = new List<SuggestionRow>();
        foreach (var summary in galaxy.KnownSystems)
        {
            if (galaxy.GetSystem(summary.SystemId) is not { } system) continue;
            foreach (var entity in system.Entities)
            {
                if (entity.Relation != OwnerRelation.Owned || entity.GetView<ColonyAutomationView>() is not { } automation) continue;
                string name = entity.GetView<NameView>()?.Name ?? $"Colony {entity.Id}";
                rows.AddRange(automation.Suggestions.Select(s => ColonyAutomationViewModel.ToRow(entity.Id, name, s)));
            }
        }
        rows.Sort((a, b) =>
        {
            int byColony = string.Compare(a.ColonyName, b.ColonyName, StringComparison.CurrentCulture);
            return byColony != 0 ? byColony : a.Id.CompareTo(b.Id);
        });
        CollectionSync.Sync(Items, rows, r => (r.ColonyId, r.Id));
        Count = Items.Count;
    }

    [RelayCommand]
    private Task Approve(SuggestionRow row)
        => Sink.SendAsync(new ApproveSuggestionCommand(row.ColonyId, row.Id), $"Approved at {row.ColonyName}: {row.Text}.");

    [RelayCommand]
    private Task Dismiss(SuggestionRow row)
        => Sink.SendAsync(new DismissSuggestionCommand(row.ColonyId, row.Id), $"Dismissed at {row.ColonyName}: {row.Text}.");
}
