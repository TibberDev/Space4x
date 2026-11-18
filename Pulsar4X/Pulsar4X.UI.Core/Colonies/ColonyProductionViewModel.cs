using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pulsar4X.Api;

namespace Pulsar4X.UI.Core.Colonies;

/// <summary>One production line (refinery, factory, shipyard…) and its job queue.</summary>
public sealed partial class ProductionLineViewModel : ObservableObject
{
    private readonly ColonyProductionViewModel _owner;

    public ProductionLineViewModel(string id, ColonyProductionViewModel owner)
    {
        Id = id;
        _owner = owner;
    }

    public string Id { get; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private bool _isIdle;
    public ObservableCollection<IndustryJobRow> Jobs { get; } = new();

    public void Update(ProductionLineView line)
    {
        Name = line.Name;
        IsIdle = line.Jobs.Count == 0;
        var parts = new List<string>();
        parts.Add(line.CurrentRatePerDay > 0 ? Fmt.PointsPerDay(line.CurrentRatePerDay) : "Idle");
        if (line.IsShipyard) parts.Add($"slip {Fmt.Mass(line.SlipSize)}");
        if (!string.IsNullOrEmpty(line.FacilityDesignName) && line.FacilityDesignName != line.Name) parts.Add(line.FacilityDesignName);
        Subtitle = string.Join(" · ", parts);

        var rows = line.Jobs.Select((j, i) => new IndustryJobRow(
            line.Id, j.JobId, j.Name, j.NumberCompleted, j.NumberOrdered, j.Repeat, j.Status,
            j.PercentComplete, j.ProductionPointsLeft,
            Warning(j), i == 0, i == line.Jobs.Count - 1)).ToList();
        CollectionSync.Sync(Jobs, rows, r => r.JobId);
    }

    private static string Warning(IndustryJobView job)
    {
        if (!string.IsNullOrEmpty(job.FundsReason)) return job.FundsReason;
        if (job.MissingResources)
        {
            var missing = job.RemainingRequirements.Select(r => $"{r.Name} {Fmt.Quantity(r.Amount)}");
            return "Waiting for " + string.Join(", ", missing);
        }
        return job.CrewWarning;
    }

    [RelayCommand]
    private Task MoveUp(IndustryJobRow job) => _owner.ChangePriority(job, -1);

    [RelayCommand]
    private Task MoveDown(IndustryJobRow job) => _owner.ChangePriority(job, +1);

    [RelayCommand]
    private Task Cancel(IndustryJobRow job) => _owner.CancelJob(job);
}

/// <summary>A production line as a choice in the new-job form.</summary>
public sealed record LineOption(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The Production tab: every line's queue, plus the new-job form. The form keeps its own state
/// (chosen line, item, quantity) across refreshes; item details are looked up fresh from the
/// latest data each time so cost and stock figures stay current.
/// </summary>
public sealed partial class ColonyProductionViewModel : ObservableObject
{
    private readonly CommandSink _sink;
    private readonly Dictionary<string, ProductionLineView> _lines = new();
    private int _colonyId;

    public ColonyProductionViewModel(CommandSink sink) => _sink = sink;

    public CommandSink Result => _sink;
    public ObservableCollection<ProductionLineViewModel> Lines { get; } = new();
    public ObservableCollection<LineOption> LineOptions { get; } = new();
    public ObservableCollection<ConstructibleOption> Options { get; } = new();
    public ObservableCollection<CostRow> Costs { get; } = new();

    [ObservableProperty] private LineOption? _selectedLine;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private ConstructibleOption? _selectedOption;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(QueueJobCommand))]
    private int _quantity = 1;

    [ObservableProperty] private bool _repeat;
    [ObservableProperty] private bool _autoInstall;
    [ObservableProperty] private bool _canAutoInstall;
    [ObservableProperty] private string _autoInstallReason = "";
    [ObservableProperty] private string _detailText = "";
    [ObservableProperty] private string _totalPointsText = "";
    [ObservableProperty] private string _blockReason = "";
    [ObservableProperty] private bool _hasShortfall;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(QueueJobCommand))]
    private bool _canQueue;

    public bool HasLines => Lines.Count > 0;

    public void Update(int colonyId, IndustryView? industry)
    {
        if (colonyId != _colonyId)
        {
            _colonyId = colonyId;
            SelectedLine = null;
            SelectedOption = null;
            Search = "";
            Quantity = 1;
            _sink.Clear();
        }

        _lines.Clear();
        var lines = industry?.ProductionLines ?? Array.Empty<ProductionLineView>();
        foreach (var line in lines) _lines[line.Id] = line;

        CollectionSync.Sync(Lines, lines, l => l.Id, vm => vm.Id,
            l => { var vm = new ProductionLineViewModel(l.Id, this); vm.Update(l); return vm; },
            (vm, l) => vm.Update(l));
        OnPropertyChanged(nameof(HasLines));

        CollectionSync.Sync(LineOptions, lines.Select(l => new LineOption(l.Id, l.Name)).ToList(), o => o.Id);
        if (SelectedLine == null || !_lines.ContainsKey(SelectedLine.Id))
            SelectedLine = LineOptions.FirstOrDefault(o => !_lines[o.Id].IsShipyard) ?? LineOptions.FirstOrDefault();
        else
            RefreshOptions();
    }

    partial void OnSelectedLineChanged(LineOption? value)
    {
        SelectedOption = null;
        RefreshOptions();
    }

    partial void OnSearchChanged(string value) => RefreshOptions();
    partial void OnSelectedOptionChanged(ConstructibleOption? value) => RefreshDetails();
    partial void OnQuantityChanged(int value)
    {
        OnPropertyChanged(nameof(QuantityInput));
        RefreshDetails();
    }

    /// <summary><see cref="Quantity"/> as the decimal a numeric input box edits; clamped to 1–100 000.</summary>
    public decimal? QuantityInput
    {
        get => Quantity;
        set => Quantity = (int)Math.Clamp(value ?? 1, 1, 100_000);
    }

    private ProductionLineView? CurrentLine => SelectedLine != null ? _lines.GetValueOrDefault(SelectedLine.Id) : null;

    private ConstructibleItemView? CurrentItem
        => SelectedOption != null ? CurrentLine?.Constructibles.FirstOrDefault(c => c.DesignId == SelectedOption.DesignId) : null;

    private void RefreshOptions()
    {
        var line = CurrentLine;
        var options = (line?.Constructibles ?? Array.Empty<ConstructibleItemView>())
            .Select(ConstructibleOption.From)
            .Where(o => string.IsNullOrWhiteSpace(Search)
                        || o.Name.Contains(Search.Trim(), StringComparison.CurrentCultureIgnoreCase)
                        || o.Category.Contains(Search.Trim(), StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(o => o.Category, StringComparer.CurrentCulture)
            .ThenBy(o => o.Name, StringComparer.CurrentCulture)
            .ToList();
        CollectionSync.Sync(Options, options, o => o.DesignId);

        if (SelectedOption != null && Options.FirstOrDefault(o => o.DesignId == SelectedOption.DesignId) is { } same)
        {
            if (!ReferenceEquals(same, SelectedOption)) SelectedOption = same;
            else RefreshDetails();
        }
        else
        {
            SelectedOption = null;
            RefreshDetails();
        }
    }

    private void RefreshDetails()
    {
        var item = CurrentItem;
        int quantity = Math.Max(1, Quantity);
        if (item == null)
        {
            Costs.Clear();
            DetailText = "";
            TotalPointsText = "";
            BlockReason = "";
            HasShortfall = false;
            CanAutoInstall = false;
            AutoInstallReason = "";
            CanQueue = false;
            return;
        }

        var details = new List<string> { item.IsShipDesign ? "Ship design" : item.IsMaterial ? "Material" : item.ComponentType };
        if (item.OutputAmount > 1) details.Add($"makes {Fmt.Int(item.OutputAmount)} per batch");
        if (item.Mass > 0) details.Add(Fmt.Mass(item.Mass));
        if (item.CrewRequired > 0) details.Add($"crew {Fmt.Int(item.CrewRequired)}");
        DetailText = string.Join(" · ", details);
        TotalPointsText = $"{Fmt.Points(item.IndustryPointsPerUnit)} each · {Fmt.Points(item.IndustryPointsPerUnit * quantity)} total";

        var costs = item.Costs
            .Select(c => new CostRow(c.Name, c.PerUnit, c.PerUnit * quantity, c.Available, c.CanProduce))
            .ToList();
        CollectionSync.Sync(Costs, costs, c => c.Name);
        HasShortfall = costs.Any(c => c.IsShort);

        CanAutoInstall = item.CanAutoInstall;
        AutoInstallReason = item.CanAutoInstall ? "Queue a construction mount for each finished unit" : item.AutoInstallReason;
        if (!CanAutoInstall) AutoInstall = false;

        BlockReason = item.OverSlip ? item.SlipReason : "";
        CanQueue = !item.OverSlip && Quantity >= 1;
    }

    private bool CanQueueJob() => CanQueue;

    [RelayCommand(CanExecute = nameof(CanQueueJob))]
    private async Task QueueJob()
    {
        if (CurrentItem is not { } item || SelectedLine is not { } line) return;
        int quantity = Math.Max(1, Quantity);
        await _sink.SendAsync(
            new QueueIndustryJobCommand(_colonyId, line.Id, item.DesignId, quantity, Repeat, AutoInstall && CanAutoInstall),
            $"Queued {Fmt.Int(quantity)} × {item.Name} at the {line.Name}.");
    }

    internal Task ChangePriority(IndustryJobRow job, int delta)
        => _sink.SendAsync(new ChangeIndustryJobPriorityCommand(_colonyId, job.LineId, job.JobId, delta),
            delta < 0 ? $"Moved {job.Name} up." : $"Moved {job.Name} down.");

    internal Task CancelJob(IndustryJobRow job)
        => _sink.SendAsync(new CancelIndustryJobCommand(_colonyId, job.LineId, job.JobId), $"Cancelled {job.Name}.");
}
