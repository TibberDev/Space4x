using System.Security.Cryptography;
using Newtonsoft.Json;
using Pulsar4X.Api;
using Pulsar4X.Colonies;
using Pulsar4X.DataStructures;
using Pulsar4X.Engine;
using Pulsar4X.Engine.Api;
using Pulsar4X.Extensions;
using Pulsar4X.Factions;
using Pulsar4X.Galaxy;
using Pulsar4X.Modding;
using Pulsar4X.Names;
using Pulsar4X.People;
using Pulsar4X.UI.Core;

namespace Pulsar4X.UI.Desktop.Hosting;

/// <summary>
/// Runs the engine in this process for single player: discovers mods, creates a game from the
/// default scenario, wraps it in an <see cref="EngineGameServer"/> and hands back a connected
/// in-process <see cref="IGameClient"/>.
///
/// The game-creation steps mirror <c>Pulsar4X.Client.Host.GameLifecycle</c> (Quickstart,
/// LoadModStore, CreateGameCore). That code lives in the ImGui host, which this app must not
/// reference, so it is reproduced here; keep the two in step when upstream changes it.
/// </summary>
public sealed class LocalGameHost : IGameLauncher, IDisposable
{
    private readonly string _modsPath;
    private EngineGameServer? _server;

    public LocalGameHost(string? modsPath = null)
    {
        _modsPath = modsPath ?? DefaultModsPath();
    }

    /// <summary>The running engine game, for diagnostics and tests. Null until a game starts.</summary>
    public Game? Game { get; private set; }

    /// <summary>Where the engine looks for mods: the same app-data folder the ImGui client uses.</summary>
    public static string DefaultModsPath()
    {
        var root = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "Pulsar4X", "Pulsar4X", "Mods");
    }

    public async Task<IGameClient> QuickstartAsync(CancellationToken cancellationToken = default)
    {
        var game = CreateQuickstartGame(out var playerFaction);
        cancellationToken.ThrowIfCancellationRequested();

        _server?.Dispose();
        Game = game;
        _server = new EngineGameServer(game);
        var client = ClientFactory.CreateLocalClient(_server);

        var connect = await client.ConnectAsync(
            new ConnectRequest { PlayerName = "Player", FactionId = playerFaction.Id }, cancellationToken);
        if (!connect.Success)
            throw new InvalidOperationException("Could not connect to the new game: " + connect.FailureReason);

        return client;
    }

    public void Dispose()
    {
        if (Game?.TimePulse.IsRunning == true)
        {
            Game.TimePulse.PauseTime();
            Game.TimePulse.WaitUntilStopped();
        }
        _server?.Dispose();
        _server = null;
    }

    // ---- game creation (mirrors GameLifecycle) ----

    private sealed record ModInfo(string ManifestPath, ModManifest Manifest);

    private List<ModInfo> DiscoverMods()
    {
        if (!Directory.Exists(_modsPath))
            throw new InvalidOperationException(
                $"No game data found at {_modsPath}. Build the solution once so the mods are copied there.");

        var mods = new List<ModInfo>();
        foreach (var directory in Directory.GetDirectories(_modsPath).OrderBy(d => d, StringComparer.Ordinal))
        {
            var manifestPath = Path.Combine(directory, "modInfo.json");
            if (!File.Exists(manifestPath)) continue;
            var manifest = JsonConvert.DeserializeObject<ModManifest>(File.ReadAllText(manifestPath));
            if (manifest != null)
                mods.Add(new ModInfo(manifestPath, manifest));
        }
        return mods;
    }

    private Game CreateQuickstartGame(out Entity playerFaction)
    {
        var available = DiscoverMods();
        var enabled = available.Where(m => m.Manifest.DefaultEnabled).ToList();
        var enabledNamespaces = new HashSet<string>(enabled.Select(m => m.Manifest.Namespace));
        var allPaths = available.Select(m => m.ManifestPath).ToList();

        var loader = new ModLoader();
        var scenario = loader.DiscoverScenarios(SiblingRoots(allPaths, "scenarios")).Values
            .Where(s => s.DefaultEnabled && (s.RequiredMods ?? new List<string>()).All(enabledNamespaces.Contains))
            .OrderBy(s => s.Namespace, StringComparer.Ordinal)
            .FirstOrDefault();

        var modPaths = enabled.Select(m => m.ManifestPath).ToList();
        foreach (var ns in scenario?.RequiredMods ?? new List<string>())
        {
            var mod = available.FirstOrDefault(m => m.Manifest.Namespace == ns);
            if (mod != null && !modPaths.Contains(mod.ManifestPath))
                modPaths.Add(mod.ManifestPath);
        }

        var store = LoadModStore(modPaths, allPaths, scenario?.Namespace);
        var start = store.ScenarioDefaultStart;
        if (start == null
            || string.IsNullOrEmpty(start.Species)
            || string.IsNullOrEmpty(start.Colony)
            || string.IsNullOrEmpty(start.System)
            || (string.IsNullOrEmpty(start.Body) && start.System != "random")
            || start.StartingFunds == null || start.MaxSystems == null || start.EleStart == null)
        {
            throw new InvalidOperationException("The default scenario has no complete default start.");
        }

        var includedSystems = store.Systems
            .Where(kvp => store.LoadedSystems.Contains(kvp.Value.ModNamespace))
            .Select(kvp => kvp.Key)
            .ToList();

        return CreateGame(store, start, includedSystems, RandomNumberGenerator.GetInt32(999_999_999), out playerFaction);
    }

    private static ModDataStore LoadModStore(List<string> requested, List<string> available, string? scenarioId)
    {
        var loader = new ModLoader();
        var store = new ModDataStore();
        loader.LoadModManifests(requested, available, store);

        var modPaths = requested.Concat(available).ToList();
        var discovered = loader.DiscoverScenarios(SiblingRoots(modPaths, "scenarios"));
        var systems = loader.DiscoverSystems(SiblingRoots(modPaths, "systems"));
        var chosen = string.IsNullOrEmpty(scenarioId)
            ? ModLoader.ChooseDefaultScenario(discovered.Values, store)
            : scenarioId;
        if (!string.IsNullOrEmpty(chosen))
            loader.ApplyScenario(chosen, null, discovered, store, systems);
        return store;
    }

    /// <summary>Folders named <paramref name="name"/> next to each mod folder, existing ones only, each once.</summary>
    private static List<string> SiblingRoots(IEnumerable<string> manifestPaths, string name)
    {
        var roots = new List<string>();
        foreach (var path in manifestPaths)
        {
            var parent = Path.GetDirectoryName(Path.GetDirectoryName(path));
            if (string.IsNullOrEmpty(parent)) continue;
            var root = Path.Combine(parent, name);
            if (Directory.Exists(root) && !roots.Contains(root))
                roots.Add(root);
        }
        return roots;
    }

    private static Game CreateGame(ModDataStore store, ScenarioManifest.ScenarioStart start,
        List<string> includedSystems, int seed, out Entity playerFaction)
    {
        int maxSystems = start.MaxSystems!.Value;
        var settings = new NewGameSettings
        {
            MaxSystems = Math.Min(maxSystems, NewGameSettings.DEFAULT_MAX_SYSTEMS),
            SMPassword = "",
            CreatePlayerFaction = true,
            DefaultFactionName = start.FactionName ?? "",
            DefaultPlayerPassword = "",
            DefaultSolStart = true,
            MasterSeed = seed,
            EleStart = start.EleStart!.Value,
        };
        if (!string.IsNullOrEmpty(start.Theme))
            settings.CurrentTheme = start.Theme;

        var game = GameFactory.CreateGame(store, settings);

        for (int i = 0; i < maxSystems - includedSystems.Count; i++)
            game.GalaxyGen.GenerateSystem(game, NameFactory.GetSystemName(game), game.GlobalManager.RNG.Next());

        foreach (var id in includedSystems)
            StarSystemFactory.LoadFromBlueprint(game, store.Systems[id]);

        StarSystem? system = null;
        Entity? body = null;
        if (start.System == "random")
        {
            var candidates = game.Systems
                .SelectMany(s => s.GetAllDataBlobsOfType<SystemBodyInfoDB>()
                    .Where(b => b.BodyType == BodyType.Terrestrial && b.OwningEntity != null)
                    .Select(b => (s, b.OwningEntity!)))
                .ToList();
            if (candidates.Count == 0)
                throw new InvalidOperationException("No terrestrial planet to start on.");
            (system, body) = candidates[RandomNumberGenerator.GetInt32(candidates.Count)];
        }
        else
        {
            var bodyName = store.SystemBodies[start.Body].Name;
            system = game.Systems.FirstOrDefault(s => s.ManagerID == start.System);
            body = system?.GetAllDataBlobsOfType<SystemBodyInfoDB>()
                .Select(b => b.OwningEntity)
                .LastOrDefault(e => e?.GetDefaultName()?.Equals(bodyName) == true);
        }
        if (system == null || body == null)
            throw new InvalidOperationException("Could not create the game: the starting system or body was not found.");

        var faction = FactionFactory.CreateBasicFaction(
            game, start.FactionName ?? "", start.FactionAbbreviation ?? "", start.StartingFunds!.Value)
            ?? throw new InvalidOperationException("Could not create the player faction.");
        game.LocalHumanFactionId = faction.Id;
        if (!faction.TryGetDataBlob<FactionInfoDB>(out var info))
            throw new InvalidOperationException("Missing FactionInfoDB on the player's faction.");

        faction.FactionOwnerID = faction.Id;
        info.KnownSystems.Add(system.ID);

        var species = SpeciesFactory.CreateFromBlueprint(system, store.Species[start.Species]);
        species.FactionOwnerID = faction.Id;
        info.Species.Add(species);
        SpeciesCargo.Register(info.Data, info.Species);

        ColonyFactory.CreateFromBlueprint(game, faction, species, system, body, store.Colonies[start.Colony]);
        ColonyFactory.PlacePlayerColonies(game, store, faction, species, start.Colony);
        if (store.Species.TryGetValue(start.Species, out var speciesBlueprint))
            ColonyFactory.PlaceOwnedColonies(game, store, faction, speciesBlueprint);
        FactionFactory.PlaceFactions(game, store);
        if (start.EleStart!.Value && start.System != "random")
            AsteroidFactory.CreateAsteroid(system, body, game.TimePulse.GameGlobalDateTime + TimeSpan.FromDays(365));

        var scientist = CommanderFactory.Create(system, faction.Id, CommanderFactory.CreateScientist(game));
        CommanderFactory.Create(system, faction.Id, CommanderFactory.CreateAdmin(game));
        if (scientist.TryGetDataBlob<BonusesDB>(out var bonuses))
        {
            bonuses.Bonuses.Add(new Bonus("Research Points", 0.1, BonusType.Perentage,
                BonusCategory.ResearchPoints, "tech-category-power-propulsion"));
        }

        game.PostNewGameInitialization();
        playerFaction = faction;
        return game;
    }
}
