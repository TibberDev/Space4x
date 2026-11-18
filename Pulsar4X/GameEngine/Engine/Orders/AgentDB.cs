using System;
using Pulsar4X.Datablobs;
using Pulsar4X.DataStructures;

namespace GameEngine.Engine.Orders;

/// <summary>
/// The personality of a person in charge (a captain, an administrator, a scientist). It sits on the
/// commander entity and shapes what the unit they run chooses to do. Skills (CommanderDB) say how well
/// they do the work; these traits say what they choose. 1 is average, 0.5 the lowest and 1.5 the highest.
/// A missing personality reads as 1 on every trait.
/// </summary>
public class AgentDB : BaseDataBlob
{
    public const float Lowest = 0.5f;
    public const float Highest = 1.5f;

    /// <summary>Seeks battle, presses attacks, patrols far, fires first. Weights Attack, Patrol and Scout.</summary>
    public float Aggression { get; set; } = 1f;

    /// <summary>Keeps fuel and damage margins, retreats early. Weights StayAlive, DontRunOutOfFuel and RefuelAt.</summary>
    public float Caution { get; set; } = 1f;

    /// <summary>Detours to anomalies, ruins and unexplored jump points. Weights the survey and exploration goals.</summary>
    public float Curiosity { get; set; } = 1f;

    /// <summary>
    /// Waits for the better deal and builds for the long term. Low patience takes the quick gain, so
    /// trading weighs 2 - Patience (replaces the old Greed: Greed 1.3 is Patience 0.7).
    /// </summary>
    public float Patience { get; set; } = 1f;

    /// <summary>Follows policy to the letter. Low loyalty bends policy at the edges; a direct order is always obeyed.</summary>
    public float Loyalty { get; set; } = 1f;

    /// <summary>Administrators only: puts a colony's effort into one production chain it already has.</summary>
    public float Specialization { get; set; } = 1f;

    /// <summary>How much more than average this person wants to trade: 2 - Patience.</summary>
    public float TradeDrive => 2f - Patience;

    /// <summary>
    /// A personality for a new commander, rolled from its own seed so it does not draw on the game's
    /// shared random stream. Each trait is the mean of two rolls (most people are near average),
    /// rounded to 0.05. Specialization is an administrator's trait; everyone else keeps 1.
    /// </summary>
    public static AgentDB Roll(int seed, CommanderTypes type)
    {
        var rng = new Random(seed);
        float Trait()
        {
            double t = (rng.NextDouble() + rng.NextDouble()) / 2;
            return (float)Math.Round((Lowest + t * (Highest - Lowest)) * 20) / 20f;
        }

        var agent = new AgentDB
        {
            Aggression = Trait(),
            Caution = Trait(),
            Curiosity = Trait(),
            Patience = Trait(),
            Loyalty = Trait(),
        };
        if (type == CommanderTypes.Civilian)
            agent.Specialization = Trait();
        return agent;
    }

    public override object Clone()
    {
        return new AgentDB
        {
            Aggression = Aggression,
            Caution = Caution,
            Curiosity = Curiosity,
            Patience = Patience,
            Loyalty = Loyalty,
            Specialization = Specialization,
        };
    }
}
