using Pulsar4X.Modding;

namespace Pulsar4X.Tests;

/// <summary>
/// Basemod has no processed materials and no refining (mined-only resources, 2026-11-18). The
/// engine still runs any processed material a mod defines, so tests of that machinery (refining
/// batches, job energy, offer stock) load a test-only mod with the old refinery, its stainless
/// steel, plastic and hydrolox recipes, and a refinery site and steel stock at Earth. Load it
/// after the scenario, before the game is created.
/// </summary>
static class TestMaterials
{
    public static void AddSteel(ModDataStore store)
        => new ModLoader().LoadModManifest("Data/testmods/refining/modInfo.json", store);
}
