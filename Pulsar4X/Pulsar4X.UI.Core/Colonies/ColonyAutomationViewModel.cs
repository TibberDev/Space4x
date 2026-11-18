using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pulsar4X.Api;

namespace Pulsar4X.UI.Core.Colonies;

/// <summary>One construction need in the player's priority order.</summary>
public sealed record PriorityRow(ColonyNeed Need, int Rank)
{
    public string Name => Need.ToString();
    public string RankText => $"{Rank}.";
}

/// <summary>A stock target with what the colony holds and has queued.</summary>
public sealed record StockTargetRow(string DesignId, string Name, long Target, long Stock, long Queued)
{
    public string TargetText => Fmt.Int(Target);
    public string StockText => Fmt.Int(Stock);
    public string QueuedText => Queued > 0 ? Fmt.Int(Queued) : "—";
    public bool IsShort => Stock + Queued < Target;
}

/// <summary>An administrator's proposal; also used by the empire-wide inbox.</summary>
public sealed record SuggestionRow(int ColonyId, string ColonyName, int Id, string Area, string Text, string Reason, string Proposer, string Made);

/// <summary>A line from the colony log: when and what.</summary>
public sealed record DecisionRow(string When, string Text);

/// <summary>
/// The colony screen's Administrator section: who runs the colony, how much they do on their own
/// (Manual, Advise or Auto per area), the construction priorities, the stock targets, their open
/// suggestions, and their decisions with reasons (the "why?").
/// </summary>
public sealed partial class ColonyAutomationViewModel : ObservableObject
{
    private int _colonyId;
    private string _colonyName = "";
    private bool _updating;

    public ColonyAutomationViewModel(CommandSink sink) => Sink = sink;

    public CommandSink Sink { get; }

    public IReadOnlyList<AutomationLevel> Levels { get; } = new[] { AutomationLevel.Manual, AutomationLevel.Advise, AutomationLevel.Auto };

    public ObservableCollection<PriorityRow> Priorities { get; } = new();
    public ObservableCollection<StockTargetRow> Targets { get; } = new();
    public ObservableCollection<SuggestionRow> Suggestions { get; } = new();
    public ObservableCollection<DecisionRow> Decisions { get; } = new();
    public ObservableCollection<ConstructibleOption> TargetOptions { get; } = new();

    [ObservableProperty] private bool _hasAdministrator;
    [ObservableProperty] private string _administratorText = "";
    [ObservableProperty] private string _reviewText = "";
    [ObservableProperty] private AutomationLevel _construction;
    [ObservableProperty] private AutomationLevel _production;
    [ObservableProperty] private bool _hasSuggestions;
    [ObservableProperty] private bool _hasDecisions;
    [ObservableProperty] private bool _hasTargets;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetTargetCommand))]
    private ConstructibleOption? _newTargetOption;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetTargetCommand))]
    private decimal? _newTargetCount = 5;

    public void Update(EntitySnapshot colony, string colonyName)
    {
        _updating = true;
        try
        {
            if (colony.Id != _colonyId) Sink.Clear();
            _colonyId = colony.Id;
            _colonyName = colonyName;
            var view = colony.GetView<ColonyAutomationView>();

            HasAdministrator = view?.AdministratorName != null;
            AdministratorText = view?.AdministratorName is { } name
                ? name
                : "Vacant: nothing is automated until someone takes the post.";
            ReviewText = view?.AdministratorName == null ? ""
                : $"Reviews the colony every {view.ReviewEveryDays:0.#} day{(view.ReviewEveryDays == 1 ? "" : "s")}"
                  + (view.NextReview is { } next ? $", next on {next:d MMM yyyy}." : ".");
            Construction = view?.Construction ?? AutomationLevel.Manual;
            Production = view?.Production ?? AutomationLevel.Manual;

            var priorities = (view?.Priorities ?? Array.Empty<ColonyNeed>()).Select((n, i) => new PriorityRow(n, i + 1)).ToList();
            CollectionSync.Sync(Priorities, priorities, p => p.Need);

            var targets = (view?.StockTargets ?? Array.Empty<StockTargetView>())
                .Select(t => new StockTargetRow(t.DesignId, t.Name, t.Target, t.Stock, t.Queued)).ToList();
            CollectionSync.Sync(Targets, targets, t => t.DesignId);
            HasTargets = Targets.Count > 0;

            var suggestions = (view?.Suggestions ?? Array.Empty<SuggestionView>())
                .Select(s => ToRow(colony.Id, colonyName, s)).ToList();
            CollectionSync.Sync(Suggestions, suggestions, s => s.Id);
            HasSuggestions = Suggestions.Count > 0;

            var decisions = (colony.GetView<ShipLogView>()?.Lines ?? Array.Empty<ShipLogLine>())
                .Where(l => l.Kind == ShipLogKind.Administrator)
                .Reverse()
                .Take(20)
                .Select(l => new DecisionRow(l.At.ToString("d MMM yyyy"), l.Text))
                .ToList();
            CollectionSync.Sync(Decisions, decisions, d => d.When + "|" + d.Text);
            HasDecisions = Decisions.Count > 0;

            var options = (colony.GetView<IndustryView>()?.ProductionLines ?? Array.Empty<ProductionLineView>())
                .Where(l => !l.IsShipyard)
                .SelectMany(l => l.Constructibles)
                .Where(c => !c.IsShipDesign)
                .GroupBy(c => c.DesignId)
                .Select(g => ConstructibleOption.From(g.First()))
                .OrderBy(o => o.Name, StringComparer.CurrentCulture)
                .ToList();
            CollectionSync.Sync(TargetOptions, options, o => o.DesignId);
            if (NewTargetOption != null && TargetOptions.All(o => o.DesignId != NewTargetOption.DesignId))
                NewTargetOption = null;
        }
        finally
        {
            _updating = false;
        }
    }

    public static SuggestionRow ToRow(int colonyId, string colonyName, SuggestionView s)
        => new(colonyId, colonyName, s.Id, s.Area.ToString(), s.Text, s.Reason, s.Proposer, s.Made.ToString("d MMM yyyy"));

    partial void OnConstructionChanged(AutomationLevel value)
    {
        if (!_updating)
            _ = Sink.SendAsync(new SetColonyAutomationCommand(_colonyId, AutomationArea.Construction, value), $"Construction set to {value}.");
    }

    partial void OnProductionChanged(AutomationLevel value)
    {
        if (!_updating)
            _ = Sink.SendAsync(new SetColonyAutomationCommand(_colonyId, AutomationArea.Production, value), $"Production set to {value}.");
    }

    [RelayCommand]
    private Task RaisePriority(PriorityRow row) => MovePriority(row, -1);

    [RelayCommand]
    private Task LowerPriority(PriorityRow row) => MovePriority(row, +1);

    private Task MovePriority(PriorityRow row, int delta)
    {
        var order = Priorities.Select(p => p.Need).ToList();
        int i = order.IndexOf(row.Need), j = i + delta;
        if (i < 0 || j < 0 || j >= order.Count) return Task.CompletedTask;
        (order[i], order[j]) = (order[j], order[i]);
        return Sink.SendAsync(new SetColonyPrioritiesCommand(_colonyId, order), $"{row.Name} moved {(delta < 0 ? "up" : "down")}.");
    }

    private bool CanSetTarget() => NewTargetOption != null && NewTargetCount is >= 0;

    [RelayCommand(CanExecute = nameof(CanSetTarget))]
    private Task SetTarget()
    {
        var option = NewTargetOption!;
        long count = (long)NewTargetCount!.Value;
        return Sink.SendAsync(new SetStockTargetCommand(_colonyId, option.DesignId, count),
            count == 0 ? $"Cleared the target for {option.Name}." : $"Keep {Fmt.Int(count)} × {option.Name} on hand.");
    }

    [RelayCommand]
    private Task ClearTarget(StockTargetRow row)
        => Sink.SendAsync(new SetStockTargetCommand(_colonyId, row.DesignId, 0), $"Cleared the target for {row.Name}.");

    [RelayCommand]
    private Task Approve(SuggestionRow row)
        => Sink.SendAsync(new ApproveSuggestionCommand(row.ColonyId, row.Id), $"Approved: {row.Text}.");

    [RelayCommand]
    private Task Dismiss(SuggestionRow row)
        => Sink.SendAsync(new DismissSuggestionCommand(row.ColonyId, row.Id), $"Dismissed: {row.Text}.");
}
