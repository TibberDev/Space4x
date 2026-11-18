using System;
using System.Collections.Generic;
using System.Linq;
using GameEngine.Engine.Orders;
using Pulsar4X.Api;
using Pulsar4X.Components;
using Pulsar4X.Datablobs;
using Pulsar4X.Energy;
using Pulsar4X.Engine;
using Pulsar4X.Extensions;
using Pulsar4X.Factions;
using Pulsar4X.Industry;
using Pulsar4X.Industry.Orders;
using Pulsar4X.Interfaces;
using Pulsar4X.Logistics;
using Pulsar4X.People;
using Pulsar4X.Storage;

namespace Pulsar4X.Colonies;

/// <summary>One proposal from a colony's administrator, waiting for the player (Advise level).</summary>
public class ColonySuggestion
{
    public int Id { get; set; }
    public AutomationArea Area { get; set; }

    /// <summary>The design to assemble (construction) or produce (production).</summary>
    public string DesignId { get; set; } = "";

    /// <summary>Production only: the line and how many.</summary>
    public string LineId { get; set; } = "";
    public long Count { get; set; }

    /// <summary>Same key, same proposal: kept across reviews and used for a 30-day dismissal.</summary>
    public string Key { get; set; } = "";

    public string Text { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Proposer { get; set; } = "";
    public DateTime Made { get; set; }

    public ColonySuggestion Copy() => (ColonySuggestion)MemberwiseClone();
}

/// <summary>
/// What a colony's administrator may do on their own, and what they have proposed. Vertical slice step 3:
/// construction (Infrastructure, power, storage, mines) and production (stock targets), each at Manual,
/// Advise or Auto. With the chair vacant nothing is automated.
/// </summary>
public class ColonyAutomationDB : BaseDataBlob
{
    public static readonly ColonyNeed[] DefaultPriorities =
        { ColonyNeed.Infrastructure, ColonyNeed.Power, ColonyNeed.Storage, ColonyNeed.Mines };

    public AutomationLevel Construction { get; set; } = AutomationLevel.Advise;
    public AutomationLevel Production { get; set; } = AutomationLevel.Advise;
    public List<ColonyNeed> Priorities { get; set; } = new(DefaultPriorities);

    /// <summary>Design id to the number to keep on hand.</summary>
    public Dictionary<string, long> StockTargets { get; set; } = new();

    public List<ColonySuggestion> Suggestions { get; set; } = new();

    /// <summary>Suggestion key to the date the administrator may propose it again.</summary>
    public Dictionary<string, DateTime> DismissedUntil { get; set; } = new();

    public DateTime NextReview { get; set; }
    public int NextSuggestionId { get; set; } = 1;

    public override object Clone() => new ColonyAutomationDB
    {
        Construction = Construction,
        Production = Production,
        Priorities = new List<ColonyNeed>(Priorities),
        StockTargets = new Dictionary<string, long>(StockTargets),
        Suggestions = Suggestions.Select(s => s.Copy()).ToList(),
        DismissedUntil = new Dictionary<string, DateTime>(DismissedUntil),
        NextReview = NextReview,
        NextSuggestionId = NextSuggestionId,
    };
}

/// <summary>
/// The colony administrator's review. Each review finds what the colony needs, in the player's priority
/// order, and either proposes it (Advise) or does it (Auto). Every decision is logged with its reason.
/// Traits shape the choices: Patience sets the spare margin, Loyalty whether the player's priorities are
/// followed, Specialization how many production items the factory works at once. Construction skill sets
/// how often they review.
/// </summary>
public static class ColonyAdministration
{
    public const double SlowestReviewDays = 7;
    public const double FastestReviewDays = 1;
    public static readonly TimeSpan DismissFor = TimeSpan.FromDays(30);

    /// <summary>Most construction jobs the administrator keeps queued at once.</summary>
    public const int MaxQueuedBuilds = 2;

    /// <summary>Days between reviews: 7 at Construction skill 0, down to 1 at a full skill of 100.</summary>
    public static double ReviewEveryDays(CommanderDB admin)
    {
        double q = CommanderSkills.Quality(admin, SkillDomain.Construction); // 1 to 2
        return Math.Clamp(SlowestReviewDays - (SlowestReviewDays - FastestReviewDays) * (q - 1), FastestReviewDays, SlowestReviewDays);
    }

    /// <summary>
    /// Spare capacity wanted above need: 0 for the most impatient (build once already short), 20% for an
    /// average administrator, 40% for the most patient.
    /// </summary>
    public static double Margin(AgentDB agent) => Math.Clamp((agent.Patience - AgentDB.Lowest) * 0.4, 0, 0.4);

    /// <summary>Production items worked at once: 1 for a specialist, 2 for an average administrator, all for a generalist.</summary>
    public static int ProductionFocus(AgentDB agent) => agent.Specialization >= 1.1f ? 1 : agent.Specialization <= 0.9f ? int.MaxValue : 2;

    /// <summary>A loyal administrator follows the player's priority order; a disloyal one their own sense of urgency.</summary>
    public static bool FollowsPriorities(AgentDB agent) => agent.Loyalty > 0.9f;

    /// <summary>Runs a review now, whatever the date. Returns the number of things proposed or done.</summary>
    public static int Review(Entity colony, DateTime now)
    {
        if (!colony.TryGetDataBlob<ColonyAutomationDB>(out var automation))
            return 0;

        if (!CommanderSkills.TryGetCommander(colony, out var adminEntity, out var admin)
            || !colony.HasDataBlob<ColonyInfoDB>())
        {
            // A vacant chair: nothing is automated, and nothing stays proposed. Look again tomorrow.
            automation.Suggestions.Clear();
            automation.NextReview = now + TimeSpan.FromDays(1);
            return 0;
        }

        var agent = adminEntity.TryGetDataBlob<AgentDB>(out var a) ? a : new AgentDB();
        automation.NextReview = now + TimeSpan.FromDays(ReviewEveryDays(admin));
        foreach (var key in automation.DismissedUntil.Where(p => p.Value <= now).Select(p => p.Key).ToList())
            automation.DismissedUntil.Remove(key);

        var info = RunMarketPlan.FactionOf(colony);
        var fresh = new List<ColonySuggestion>();
        if (info != null && automation.Construction != AutomationLevel.Manual)
            fresh.AddRange(ConstructionWork(colony, info, automation, agent));
        if (info != null && automation.Production != AutomationLevel.Manual)
            fresh.AddRange(ProductionWork(colony, info, automation, agent));

        string name = adminEntity.GetName(colony.FactionOwnerID);
        var kept = new List<ColonySuggestion>();
        int count = 0;
        foreach (var suggestion in fresh)
        {
            if (automation.DismissedUntil.ContainsKey(suggestion.Key))
                continue;
            suggestion.Proposer = name;
            var level = suggestion.Area == AutomationArea.Construction ? automation.Construction : automation.Production;
            if (level == AutomationLevel.Auto)
            {
                if (Carry(colony, info!, suggestion))
                {
                    ShipLogBook.Append(colony, now, ShipLogKind.Administrator, $"{name}: {suggestion.Text}. {suggestion.Reason}");
                    CommanderSkills.Grant(admin, SkillDomain.Construction);
                    count++;
                }
                continue;
            }

            var old = automation.Suggestions.FirstOrDefault(s => s.Key == suggestion.Key);
            suggestion.Id = old?.Id ?? automation.NextSuggestionId++;
            suggestion.Made = old?.Made ?? now;
            kept.Add(suggestion);
            count++;
        }
        automation.Suggestions = kept;
        return count;
    }

    /// <summary>Runs a suggestion for the player. False when it is gone or can no longer be done.</summary>
    public static bool Approve(Entity colony, int suggestionId, DateTime now, out string reason)
    {
        reason = "";
        if (!colony.TryGetDataBlob<ColonyAutomationDB>(out var automation)
            || automation.Suggestions.FirstOrDefault(s => s.Id == suggestionId) is not { } suggestion)
        {
            reason = "That suggestion is gone.";
            return false;
        }
        var info = RunMarketPlan.FactionOf(colony);
        if (info == null || !Carry(colony, info, suggestion))
        {
            reason = "That can no longer be done here.";
            return false;
        }
        automation.Suggestions.Remove(suggestion);
        ShipLogBook.Append(colony, now, ShipLogKind.Administrator, $"Approved: {suggestion.Text}. {suggestion.Reason}");
        if (CommanderSkills.TryGetCommander(colony, out _, out var admin))
            CommanderSkills.Grant(admin, SkillDomain.Construction);
        return true;
    }

    public static bool Dismiss(Entity colony, int suggestionId, DateTime now)
    {
        if (!colony.TryGetDataBlob<ColonyAutomationDB>(out var automation)
            || automation.Suggestions.FirstOrDefault(s => s.Id == suggestionId) is not { } suggestion)
            return false;
        automation.Suggestions.Remove(suggestion);
        automation.DismissedUntil[suggestion.Key] = now + DismissFor;
        return true;
    }

    // ------------------------------------------------------------------ construction

    static IEnumerable<ColonySuggestion> ConstructionWork(Entity colony, FactionInfoDB info, ColonyAutomationDB automation, AgentDB agent)
    {
        if (!colony.TryGetDataBlob<LocalConstructionDB>(out var construction) || construction.PointsPerDay <= 0)
            yield break;
        if (construction.BuildQueue.Count >= MaxQueuedBuilds)
            yield break;

        double margin = Margin(agent);
        var needs = new List<(ColonyNeed need, ComponentDesign design, double urgency, string reason)>();
        foreach (var need in automation.Priorities)
        {
            if (Assess(colony, info, need, margin) is { } found)
                needs.Add((need, found.design, found.urgency, found.reason));
        }
        if (needs.Count == 0)
            yield break;

        var pick = needs[0];
        string disloyal = "";
        if (!FollowsPriorities(agent))
        {
            var own = needs.OrderByDescending(n => n.urgency).First();
            if (own.need != pick.need)
                disloyal = $" Put ahead of {pick.need.ToString().ToLowerInvariant()}, your first priority: they judge it more urgent.";
            pick = own;
        }

        yield return new ColonySuggestion
        {
            Area = AutomationArea.Construction,
            DesignId = pick.design.UniqueID,
            Key = "build|" + pick.need,
            Text = $"Build {pick.design.Name}",
            Reason = pick.reason + disloyal,
        };
    }

    /// <summary>Whether the colony is short of <paramref name="need"/>, and the site to build for it.</summary>
    static (ComponentDesign design, double urgency, string reason)? Assess(Entity colony, FactionInfoDB info, ColonyNeed need, double margin)
    {
        string spare = margin > 0 ? $" (wants {margin:P0} spare)" : " (builds only once short)";
        switch (need)
        {
            case ColonyNeed.Infrastructure:
            {
                if (!colony.TryGetDataBlob<InfrastructureDB>(out var infra) || infra.CapacityRequired <= 0)
                    return null;
                double wanted = infra.CapacityRequired * (1 + margin);
                if (infra.CapacityProvided >= wanted || PickSite<InfrastructureCapacityAtb>(colony, info) is not { } site)
                    return null;
                return (site, wanted / Math.Max(1, infra.CapacityProvided),
                    $"Infrastructure {infra.CapacityProvided:N0} of {infra.CapacityRequired:N0} needed{spare}.");
            }
            case ColonyNeed.Power:
            {
                if (!colony.TryGetDataBlob<ColonyPowerDB>(out var power) || power.Demand_kW <= 0)
                    return null;
                double wanted = power.Demand_kW * (1 + margin);
                if (power.Delivered_kW >= wanted || PickSite<PowerPlantAtb>(colony, info) is not { } site)
                    return null;
                return (site, wanted / Math.Max(1, power.Delivered_kW),
                    $"Power {power.Delivered_kW / 1000:N1} MW delivered for {power.Demand_kW / 1000:N1} MW of demand{spare}.");
            }
            case ColonyNeed.Storage:
            {
                if (!colony.TryGetDataBlob<CargoStorageDB>(out var storage))
                    return null;
                double fullAt = 1 - (0.05 + margin / 2);
                foreach (var (storeId, store) in storage.TypeStores.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (store.MaxVolume <= 0)
                        continue;
                    double used = 1 - store.FreeVolume / store.MaxVolume;
                    if (used < fullAt)
                        continue;
                    if (PickStorageSite(colony, info, storeId) is not { } site)
                        continue;
                    return (site, used / fullAt, $"{StoreName(info, storeId)} is {used:P0} full{spare}.");
                }
                return null;
            }
            case ColonyNeed.Mines:
            {
                if (ShortMinedResource(colony, info) is not { } shortName || PickSite<MineResourcesAtbDB>(colony, info) is not { } site)
                    return null;
                return (site, 1.0, $"Queued work waits on {shortName}, and this world has it in the ground.");
            }
        }
        return null;
    }

    /// <summary>The site design of this kind the colony already uses, else the first one known.</summary>
    static ComponentDesign? PickSite<T>(Entity colony, FactionInfoDB info) where T : IComponentDesignAttribute
        => PickSite(colony, info, d => d.HasAttribute<T>());

    static ComponentDesign? PickStorageSite(Entity colony, FactionInfoDB info, string storeId)
        => PickSite(colony, info, d => d.TryGetAttribute<CargoStorageAtb>(out var atb) && atb.StoreTypeID == storeId);

    static ComponentDesign? PickSite(Entity colony, FactionInfoDB info, Func<ComponentDesign, bool> kind)
    {
        var candidates = info.ComponentDesigns.Values
            .Where(d => d.IsValid && LocalConstructionJob.IsSiteOnly(d) && kind(d))
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0)
            return null;
        var installed = colony.TryGetDataBlob<ComponentInstancesDB>(out var instances)
            ? candidates.FirstOrDefault(d => instances.ComponentsByDesign.ContainsKey(d.UniqueID))
            : null;
        return installed ?? candidates[0];
    }

    static string StoreName(FactionInfoDB info, string storeId)
        => info.Data.CargoTypes.TryGetValue(storeId, out var type) && !string.IsNullOrEmpty(type.Name) ? type.Name : storeId;

    /// <summary>A resource queued work is short of that the colony's world has deposits of, by name.</summary>
    static string? ShortMinedResource(Entity colony, FactionInfoDB info)
    {
        if (!colony.TryGetDataBlob<ColonyInfoDB>(out var colonyInfo)
            || !colonyInfo.PlanetEntity.TryGetDataBlob<MineralsDB>(out var deposits)
            || !colony.TryGetDataBlob<CargoStorageDB>(out var storage))
            return null;

        var wanted = new Dictionary<string, long>();
        void Add(Dictionary<string, long>? needs)
        {
            if (needs == null)
                return;
            foreach (var (id, amount) in needs)
                wanted[id] = wanted.GetValueOrDefault(id) + amount;
        }
        if (colony.TryGetDataBlob<LocalConstructionDB>(out var construction))
            foreach (var job in construction.BuildQueue)
                Add(job.ResourcesRemaining);
        if (colony.TryGetDataBlob<IndustryAbilityDB>(out var industry))
            foreach (var line in industry.ProductionLines.Values)
                foreach (var job in line.Jobs)
                    Add(job.ResourcesRequiredRemaining);

        foreach (var (id, amount) in wanted.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (info.Data.CargoGoods.GetAny(id) is not Mineral mineral)
                continue;
            if (storage.GetUnitsStored(mineral, false) >= amount)
                continue;
            if (deposits.Minerals.TryGetValue(mineral.ID, out var deposit) && deposit.Amount.Actual > 0)
                return mineral.Name;
        }
        return null;
    }

    // ------------------------------------------------------------------ production

    static IEnumerable<ColonySuggestion> ProductionWork(Entity colony, FactionInfoDB info, ColonyAutomationDB automation, AgentDB agent)
    {
        var short_ = new List<(string designId, string name, long target, long have, long gap, string lineId)>();
        foreach (var (designId, target) in automation.StockTargets.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (target <= 0 || !info.IndustryDesigns.TryGetValue(designId, out var design))
                continue;
            long have = Stock(colony, designId) + RunMarketPlan.AlreadyQueued(colony, designId);
            long gap = target - have;
            if (gap <= 0 || !RunMarketPlan.TryFindLine(colony, info, designId, out var lineId))
                continue;
            short_.Add((designId, design.Name, target, have, gap, lineId));
        }

        int focus = ProductionFocus(agent);
        foreach (var item in short_.OrderByDescending(s => (double)s.gap / s.target).Take(focus))
        {
            string style = focus == 1 ? " A specialist: one item at a time." : "";
            yield return new ColonySuggestion
            {
                Area = AutomationArea.Production,
                DesignId = item.designId,
                LineId = item.lineId,
                Count = Math.Min(item.gap, ushort.MaxValue),
                Key = "make|" + item.designId,
                Text = $"Make {Math.Min(item.gap, ushort.MaxValue):N0} × {item.name}",
                Reason = $"{item.have:N0} on hand or queued, target {item.target:N0}.{style}",
            };
        }
    }

    /// <summary>Units of a design or good in the colony's stores, counting crates of a component design.</summary>
    public static long Stock(Entity colony, string designId)
    {
        if (!colony.TryGetDataBlob<CargoStorageDB>(out var storage))
            return 0;
        long sum = 0;
        foreach (var store in storage.TypeStores.Values)
        {
            foreach (var (id, units) in store.CurrentStoreInUnits)
            {
                if (!store.Cargoables.TryGetValue(id, out var cargo))
                    continue;
                if (cargo.UniqueID == designId || cargo is ComponentInstance instance && instance.Design.UniqueID == designId)
                    sum += units;
            }
        }
        return sum;
    }

    // ------------------------------------------------------------------ wants (what supply ships carry)

    /// <summary>
    /// What the colony's queued work is short of: construction and factory jobs' remaining resources, less
    /// what is already in the warehouse. Cargo id to units. A freighter on Supply colonies brings these.
    /// </summary>
    public static Dictionary<string, long> Wants(Entity colony)
    {
        var needed = new Dictionary<string, long>();
        void Add(Dictionary<string, long>? remaining)
        {
            if (remaining == null)
                return;
            foreach (var (id, amount) in remaining)
                if (amount > 0)
                    needed[id] = needed.GetValueOrDefault(id) + amount;
        }
        if (colony.TryGetDataBlob<LocalConstructionDB>(out var construction))
            foreach (var job in construction.BuildQueue)
                Add(job.ResourcesRemaining);
        if (colony.TryGetDataBlob<IndustryAbilityDB>(out var industry))
            foreach (var line in industry.ProductionLines.Values)
                foreach (var job in line.Jobs)
                    Add(job.ResourcesRequiredRemaining);

        var wants = new Dictionary<string, long>();
        foreach (var (id, amount) in needed)
        {
            long gap = amount - Stock(colony, id);
            if (gap > 0)
                wants[id] = gap;
        }
        return wants;
    }

    /// <summary>Units of a good the colony can give away: its stock less what its own queued work needs.</summary>
    public static long Spare(Entity colony, string cargoId)
    {
        long stock = Stock(colony, cargoId);
        if (stock <= 0)
            return 0;
        long ownNeed = 0;
        if (colony.TryGetDataBlob<LocalConstructionDB>(out var construction))
            foreach (var job in construction.BuildQueue)
                ownNeed += job.ResourcesRemaining.GetValueOrDefault(cargoId);
        if (colony.TryGetDataBlob<IndustryAbilityDB>(out var industry))
            foreach (var line in industry.ProductionLines.Values)
                foreach (var job in line.Jobs)
                    ownNeed += job.ResourcesRequiredRemaining?.GetValueOrDefault(cargoId) ?? 0;
        // Plant fuel keeps its runway.
        ownNeed += ColonyPowerProcessor.FuelReserveUnits(colony, cargoId);
        return Math.Max(0, stock - ownNeed);
    }

    // ------------------------------------------------------------------ doing it

    static bool Carry(Entity colony, FactionInfoDB info, ColonySuggestion suggestion)
    {
        var game = colony.Manager?.Game;
        if (game == null)
            return false;

        if (suggestion.Area == AutomationArea.Construction)
        {
            if (!info.ComponentDesigns.TryGetValue(suggestion.DesignId, out var design)
                || !colony.TryGetDataBlob<LocalConstructionDB>(out var construction) || construction.PointsPerDay <= 0)
                return false;
            return game.OrderHandler.HandleOrder(AddToConstructionQueueOrder.CreateAssemble(colony, design));
        }

        if (!info.IndustryDesigns.ContainsKey(suggestion.DesignId) || suggestion.Count < 1)
            return false;
        var job = new IndustryJob(info, suggestion.DesignId);
        job.InitialiseJob((ushort)Math.Min(suggestion.Count, ushort.MaxValue), false);
        return game.OrderHandler.HandleOrder(IndustryOrder2.CreateNewJobOrder(colony.FactionOwnerID, colony, suggestion.LineId, job));
    }
}

/// <summary>Daily: each colony whose administrator is due runs a review.</summary>
public class ColonyAdministratorProcessor : IHotloopProcessor
{
    public TimeSpan RunFrequency => TimeSpan.FromDays(1);

    public TimeSpan FirstRunOffset => TimeSpan.FromHours(0.75);

    public Type GetParameterType => typeof(ColonyAutomationDB);

    public void Init(Game game) { }

    public void ProcessEntity(Entity entity, int deltaSeconds)
    {
        var automation = entity.GetDataBlob<ColonyAutomationDB>();
        var now = entity.StarSysDateTime;
        if (automation.NextReview <= now)
            ColonyAdministration.Review(entity, now);
    }

    public int ProcessManager(EntityManager manager, int deltaSeconds)
    {
        var entities = manager.GetAllEntitiesWithDataBlob<ColonyAutomationDB>();
        foreach (var entity in entities)
            ProcessEntity(entity, deltaSeconds);
        return entities.Count;
    }
}
