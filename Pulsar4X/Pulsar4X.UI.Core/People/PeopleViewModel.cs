using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pulsar4X.Api;
using Pulsar4X.UI.Core.Colonies;

namespace Pulsar4X.UI.Core.People;

/// <summary>One person in the roster list.</summary>
public sealed record PersonRow(int Id, string Name, string Role, string Detail, bool IsAssigned, bool CanAdminister);

/// <summary>A skill domain with its points; Percent fills a bar against the cap.</summary>
public sealed record SkillRow(string Name, string Points, double Percent, string Line);

/// <summary>
/// One trait. Percent places the value on a 0.5-1.5 bar (50 is average). Notable when it is not average,
/// so the screen can make the traits that shape this person stand out.
/// </summary>
public sealed record TraitRow(string Name, string Level, double Percent, string Line, bool IsNotable);

/// <summary>A colony with an administrative post the selected person could take.</summary>
public sealed record PostOption(int ColonyId, string Name, string Holder, int HolderId)
{
    public string Text => string.IsNullOrEmpty(Holder) ? $"{Name} (vacant)" : $"{Name} ({Holder})";
}

/// <summary>
/// The People screen: everyone the faction employs, with each person's skills (how well they work) and
/// traits (what they choose to do), and seating a civilian as a colony's administrator.
/// </summary>
public sealed partial class PeopleViewModel : ObservableObject
{
    public const string Everyone = "Everyone", Administrators = "Administrators", Officers = "Officers", Scientists = "Scientists";

    private IReadOnlyList<CommanderSnapshot> _roster = Array.Empty<CommanderSnapshot>();
    private int? _preferredPostColonyId;
    private List<PostOption> _posts = new();

    public PeopleViewModel(Func<GameCommand, Task<CommandResult>> submit) => Sink = new CommandSink(submit);

    public CommandSink Sink { get; }

    public IReadOnlyList<string> Filters { get; } = new[] { Everyone, Administrators, Officers, Scientists };

    public ObservableCollection<PersonRow> People { get; } = new();
    public ObservableCollection<SkillRow> Skills { get; } = new();
    public ObservableCollection<TraitRow> Traits { get; } = new();
    public ObservableCollection<PostOption> Posts { get; } = new();

    [ObservableProperty] private string _filter = Everyone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPerson))]
    [NotifyCanExecuteChangedFor(nameof(AssignCommand), nameof(ReleaseCommand))]
    private PersonRow? _selectedPerson;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AssignCommand))]
    private PostOption? _selectedPost;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _assignmentText = "";
    [ObservableProperty] private bool _canAdminister;
    [ObservableProperty] private bool _hasTraits;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReleaseCommand))]
    private int? _administeredColonyId;

    public bool HasPerson => SelectedPerson != null;
    public bool HasNobody => People.Count == 0;

    partial void OnFilterChanged(string value) => Rebuild();

    partial void OnSelectedPersonChanged(PersonRow? value) => ShowSelected();

    /// <summary>Shows the administrators and offers <paramref name="colonyId"/>'s post, for "choose an administrator".</summary>
    public void ChooseAdministratorFor(int colonyId, IClientGalaxy galaxy)
    {
        _preferredPostColonyId = colonyId;
        Filter = Administrators;
        Update(galaxy);
        if (SelectedPerson == null || !SelectedPerson.CanAdminister)
            SelectedPerson = People.FirstOrDefault(p => !p.IsAssigned) ?? People.FirstOrDefault();
        SelectedPost = Posts.FirstOrDefault(p => p.ColonyId == colonyId) ?? SelectedPost;
    }

    public void Update(IClientGalaxy galaxy)
    {
        var posts = new List<PostOption>();
        foreach (var summary in galaxy.KnownSystems)
        {
            if (galaxy.GetSystem(summary.SystemId) is not { } system) continue;
            foreach (var entity in system.Entities)
            {
                if (entity.Relation != OwnerRelation.Owned || entity.GetView<ColonyView>() is not { HasAdministrativePost: true } colony) continue;
                posts.Add(new PostOption(entity.Id, entity.GetView<NameView>()?.Name ?? $"Colony {entity.Id}", colony.AdministratorName ?? "", colony.AdministratorId));
            }
        }
        posts.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
        int? keep = SelectedPost?.ColonyId ?? _preferredPostColonyId;
        CollectionSync.Sync(Posts, posts, p => p.ColonyId);
        SelectedPost = Posts.FirstOrDefault(p => p.ColonyId == keep) ?? Posts.FirstOrDefault();
        _posts = posts;

        if (!ReferenceEquals(galaxy.Commanders, _roster))
        {
            _roster = galaxy.Commanders;
            Rebuild();
        }
        else
        {
            ShowSelected();
        }
    }

    private void Rebuild()
    {
        var rows = _roster
            .Where(c => Filter switch
            {
                Administrators => c.Kind == CommanderKind.Civilian,
                Officers => c.Kind is CommanderKind.Navy or CommanderKind.Ground,
                Scientists => c.Kind == CommanderKind.Scientist,
                _ => true,
            })
            .OrderBy(c => RoleOrder(c.Kind))
            .ThenBy(c => c.Name, StringComparer.CurrentCulture)
            .Select(c => new PersonRow(c.Id, c.Name, Role(c.Kind), c.AssignmentName ?? "Unassigned", c.IsAssigned, c.Kind == CommanderKind.Civilian))
            .ToList();
        int? keep = SelectedPerson?.Id;
        CollectionSync.Sync(People, rows, r => r.Id);
        OnPropertyChanged(nameof(HasNobody));
        SelectedPerson = People.FirstOrDefault(p => p.Id == keep) ?? People.FirstOrDefault();
        ShowSelected();
    }

    private void ShowSelected()
    {
        var person = SelectedPerson is { } row ? _roster.FirstOrDefault(c => c.Id == row.Id) : null;
        if (person == null)
        {
            Skills.Clear();
            Traits.Clear();
            Name = Subtitle = AssignmentText = "";
            CanAdminister = HasTraits = false;
            AdministeredColonyId = null;
            return;
        }

        Name = person.Name;
        string rank = person.RankName ?? (person.Rank > 0 ? $"Rank {person.Rank}" : "");
        Subtitle = string.Join(" · ", new[] { Role(person.Kind), rank }.Where(s => !string.IsNullOrEmpty(s)));
        AssignmentText = person.AssignmentName ?? "Unassigned";
        CanAdminister = person.Kind == CommanderKind.Civilian;
        AdministeredColonyId = _posts.FirstOrDefault(p => p.HolderId == person.Id)?.ColonyId;

        CollectionSync.Sync(Skills, person.Domains
            .Select(d => new SkillRow(d.Name, $"{d.Points} / {d.Cap}", d.Cap > 0 ? 100.0 * d.Points / d.Cap : 0, d.Line))
            .ToList(), s => s.Name);
        CollectionSync.Sync(Traits, person.Traits
            .Select(t => new TraitRow(t.Name, t.Level, Math.Clamp((t.Value - 0.5) * 100, 0, 100), t.Line, t.Level != "Average"))
            .ToList(), t => t.Name);
        HasTraits = Traits.Count > 0;
    }

    private bool CanAssign() => SelectedPerson is { CanAdminister: true } && SelectedPost != null;

    [RelayCommand(CanExecute = nameof(CanAssign))]
    private async Task AssignAsync()
    {
        if (SelectedPerson is not { } person || SelectedPost is not { } post) return;
        await Sink.SendAsync(new AssignAdministratorCommand(post.ColonyId, person.Id), $"{person.Name} now runs {post.Name}.");
    }

    private bool CanRelease() => SelectedPerson != null && AdministeredColonyId != null;

    [RelayCommand(CanExecute = nameof(CanRelease))]
    private async Task ReleaseAsync()
    {
        if (SelectedPerson is not { } person || AdministeredColonyId is not { } colonyId) return;
        await Sink.SendAsync(new AssignAdministratorCommand(colonyId, -1), $"{person.Name} left the post.");
    }

    private static string Role(CommanderKind kind) => kind switch
    {
        CommanderKind.Civilian => "Administrator",
        CommanderKind.Navy => "Officer",
        CommanderKind.Ground => "Ground officer",
        CommanderKind.Scientist => "Scientist",
        _ => kind.ToString(),
    };

    private static int RoleOrder(CommanderKind kind) => kind switch
    {
        CommanderKind.Civilian => 0,
        CommanderKind.Navy => 1,
        CommanderKind.Ground => 2,
        _ => 3,
    };
}
