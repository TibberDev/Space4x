using CommunityToolkit.Mvvm.ComponentModel;
using Pulsar4X.Api;

namespace Pulsar4X.UI.Core.Colonies;

/// <summary>
/// Sends commands for a screen and remembers how the last one went, so the screen can say
/// "Queued 5 × Factory" or show why the server refused. Results come back from the server; the
/// colony's new state then arrives through the normal update stream.
/// </summary>
public sealed partial class CommandSink : ObservableObject
{
    private readonly Func<GameCommand, Task<CommandResult>> _submit;

    public CommandSink(Func<GameCommand, Task<CommandResult>> submit) => _submit = submit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = "";

    [ObservableProperty] private bool _isError;

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    /// <summary>Submits <paramref name="command"/>; on success shows <paramref name="successMessage"/>.</summary>
    public async Task<bool> SendAsync(GameCommand command, string successMessage)
    {
        CommandResult result;
        try
        {
            result = await _submit(command);
        }
        catch (Exception ex)
        {
            result = CommandResult.Reject(ex.GetBaseException().Message);
        }

        IsError = !result.Accepted;
        Message = result.Accepted ? successMessage : "Not done: " + (result.RejectionReason ?? "the server refused the order.");
        return result.Accepted;
    }

    public void Clear() => Message = "";
}
