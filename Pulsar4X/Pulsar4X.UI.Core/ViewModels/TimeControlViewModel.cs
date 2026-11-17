using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pulsar4X.Api;

namespace Pulsar4X.UI.Core.ViewModels;

/// <summary>A selectable simulation step size, e.g. "1 day".</summary>
public sealed record StepSizeOption(string Label, TimeSpan Length)
{
    public override string ToString() => Label;
}

/// <summary>
/// The game clock: date display, play/pause, single step and step size. Reads the clock from
/// <see cref="IClientGalaxy.Time"/> each frame and writes through <see cref="IGameClient.SetTimeControlAsync"/>.
/// </summary>
public sealed partial class TimeControlViewModel : ObservableObject
{
    public static IReadOnlyList<StepSizeOption> StepSizes { get; } = new[]
    {
        new StepSizeOption("5 seconds", TimeSpan.FromSeconds(5)),
        new StepSizeOption("1 minute", TimeSpan.FromMinutes(1)),
        new StepSizeOption("5 minutes", TimeSpan.FromMinutes(5)),
        new StepSizeOption("1 hour", TimeSpan.FromHours(1)),
        new StepSizeOption("6 hours", TimeSpan.FromHours(6)),
        new StepSizeOption("1 day", TimeSpan.FromDays(1)),
        new StepSizeOption("5 days", TimeSpan.FromDays(5)),
        new StepSizeOption("30 days", TimeSpan.FromDays(30)),
    };

    private IGameClient? _client;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPaused))]
    [NotifyCanExecuteChangedFor(nameof(PlayPauseCommand), nameof(StepCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayPauseCommand))]
    private bool _isStopping;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayPauseCommand), nameof(StepCommand))]
    private bool _hasGame;

    [ObservableProperty] private string _dateText = "—";
    [ObservableProperty] private string _timeText = "";
    [ObservableProperty] private DateTime _gameDateTime;
    [ObservableProperty] private StepSizeOption _selectedStepSize = StepSizes[5];

    public bool IsPaused => !IsRunning;

    public void Attach(IGameClient client)
    {
        _client = client;
        HasGame = true;
        // Push our default step size so the engine and the combo box agree from the start.
        _ = _client.SetTimeControlAsync(new TimeControlRequest(TimeControlAction.SetTickLength, TickLength: SelectedStepSize.Length));
        Refresh();
    }

    /// <summary>Pulls the current clock from the client. Call once per frame after <see cref="IGameClient.Update"/>.</summary>
    public void Refresh()
    {
        if (_client == null) return;
        var time = _client.Galaxy.Time;
        IsRunning = time.IsRunning;
        IsStopping = time.IsStopping;
        if (GameDateTime != time.GameDateTime)
        {
            GameDateTime = time.GameDateTime;
            DateText = time.GameDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
            TimeText = time.GameDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }
    }

    partial void OnSelectedStepSizeChanged(StepSizeOption value)
    {
        _client?.SetTimeControlAsync(new TimeControlRequest(TimeControlAction.SetTickLength, TickLength: value.Length));
    }

    private bool CanPlayPause() => HasGame && !IsStopping;

    [RelayCommand(CanExecute = nameof(CanPlayPause))]
    private Task PlayPause()
    {
        if (_client == null) return Task.CompletedTask;
        var action = IsRunning ? TimeControlAction.Pause : TimeControlAction.Start;
        return _client.SetTimeControlAsync(new TimeControlRequest(action));
    }

    private bool CanStep() => HasGame && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStep))]
    private Task Step()
    {
        if (_client is not { } client) return Task.CompletedTask;
        // In-process the step runs synchronously inside the call; a long step would freeze the
        // window, so run it off the UI thread. Results still land via Update() on the UI thread.
        var request = new TimeControlRequest(TimeControlAction.StepOnce, StepLength: SelectedStepSize.Length);
        return Task.Run(() => client.SetTimeControlAsync(request));
    }
}
