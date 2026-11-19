# Agent notes

Inbox for small bugs and follow-ups that came up while doing something else. Any agent session in this repo should read this at the start and append to it.

## How to use this file

- One item, one heading. Say what happens, the file and function, and how you know. A repro or a test name is enough. Skip a story about how you found it.
- Read the open items before you edit. If you are already in that code and the fix is small, do it in the same pass, then mark the note done.
- A new problem that is unrelated to the task, or that is not a small change, gets an entry and stays unfixed.
- When you fix one, replace the body with the date and the one-line outcome, or delete the entry. An open heading means it is still open.
- Longer writeups already have a home. Link them. Do not paste them here.
- Plans own their own "later" lists, and the master plan carries the ones from deleted plans. Do not copy those into this file.
- No secrets, no save files, no logs.

## Longer notes, already written

Everything is linked from [master-plan.md](master-plan.md): the work queue, the open plans in `plans/`, and the reference notes in `design/`. This file is only the bug inbox.

## Done

### Colonies list warehouse stock

2026-10-03. A blueprint colony with a logistics office is assigned `GoalType.OfferStock` after its cargo is loaded. `OfferStockPlan` adds a Min 0 policy row for each warehouse pile that has none, largest first, up to office capacity, then `RunMarket` posts the sell. Luna Concord lists iron, titanium, silicon, and methalox. New rows ask and bid 0. `OfferStockTests`, `EarthStartTests`, and `LunaConcordTests.PlacedColony_ListsItsWarehouse` pass.

### Sol start: the first mining hour kills the clock

2026-10-03. A mine rate for a mineral the body does not have is omitted in `MiningHelper.CalculateActualMiningRates`, and `MineResourcesProcessor.MineResources` skips a rate whose deposit is missing. `TimeStopDiagnosticTests.SolStart_FirstHour_MiningDoesNotKillTheClock` passes.

### Master pulse keeps a jump interrupt forever

2026-10-17. `ProcessNextInterupt` removes the datetime key it just ran. A later key stays. `AgentWakeTests.JumpKey_IsGoneAfterThePulse_AndTheNextStepMovesOn` passes. Live jumps stay on `ShipJumpAction`.

### Geo survey: finishing a site can hang the subpulse

2026-10-17. `FleetSurvey_MarsAndMoons_ShipsSurvey_TankerMovesToParent` runs to 30 hours and Mars is complete. `FleetGravSurvey_OnSite_CompletesAssignedAnomalies` includes the outer anomaly and returns. A second pass still due at the current instant throws and names the processor. A ship parked on a `MoveTypes.None` site can plot the next warp.

### Escrow of a moving load does not finish

2026-11-08. Escrow reserves units only. Delivery frees that volume once and subtracts the mass of the units that left. A sub-unit remainder no longer sticks in escrow. Passenger load and unload do not index a colony passenger store.

### A ship that bootstraps Luna ends up inside Luna and cannot leave

2026-11-18. The parent-to-moon transfer has no mid-course correction, and its approach periapsis came out at 1,722 km against Luna's 1,737 km surface; the capture burn then circularised there. `SoiTransferAction.TryStartTrim` now raises an approach periapsis under 1.05 body radii to the planned arrival radius with one burn on entering the target's sphere (3.76 m/s in the play-test). `SoiCaptureTests.EarthToLuna_FromALaunchOrbit_CapturesAboveTheSurface` passes.

### A delivery trip leaves the source without topping up its tanks

2026-11-18. A loaded ship still at its own source colony now fills its tanks there as its own step ("Refuelling"), from stock above the power plants' reserve (`MoveGoodsPlan.TopUpFuel`, used by `MoveGoodsPlan` and `DeliverShipmentPlan`). It must be its own step: the ship agent drops queued steps when one succeeds. `MoveGoodsTests.Load_TopsUpTheTanksAtTheSource` passes; in the play-test the ship reaches Luna with 8,957 m/s.

## Open

### A founded colony's settlers all die at the first population pass

Found by the vertical-slice play-test. Part fixed 2026-11-18: loaded moons took their temperature from their distance to their planet as if it were the star (`SystemBodyFactory`, both loader paths; now `OrbitAroundStar`), so Luna was about 1,130 °C with colony cost 36 and a 200-support crate held 5 people. Luna is now 18.8 °C, cost 2 (`EarlyTechStartTests.EarlyTech_LunaIsAsWarmAsEarthsDistanceFromTheSun`), and the early-tech first outpost carries 12 infrastructure crates (1,200 people at cost 2). Then: `FoundColonyCommand` founds the colony empty and the fleet Bootstrap moves the settlers once every crate is mounted (`Goal.BootstrapSettlers`, `BootstrapColonyTests.FleetBootstrap_SettlersMove_OnceTheCratesAreMounted`), and a fleet ship can take a second trip for the same package (`DeliverShipmentPlan`, `ShipmentTests.AFleetShip_TakesTheRestOnANewTrip`); the 16 Luna crates did not fit one 12-crate hold and the last 4 never came. Not yet checked in the play-test. Still open: `PopulationProcessor.GrowPopulation` (growth -50, `@todo`) takes a colony over its support straight to 0, and the admin's Infrastructure need reads `InfrastructureDB` (3,200 against 28), not population support, so a growing colony will hit the cap.

### Bootstrap resources that need tank room cannot be unloaded at a new colony

Found by the vertical-slice play-test. City Hall's materials include 5,000 units of a `fuel-storage` mineral (uranium or hydrogen). The Starship Cargo carries them in its fuel tanks, but the new Luna colony has only a 200 m3 `general-storage` store, so `DeliverShipmentPlan.PlanUnload` waits ("Waiting to unload") forever. The first-outpost package has no tank storage, and the fleet Bootstrap does not check that the destination can store every line it carries.

### ApiCommanderTests.Commanders_are_pushed_on_connect_and_on_clock_advance is flaky

2026-11-18, one failure in a full-suite run, passes alone. `received.Any(...)` (`API/ApiCommanderTests.cs` line 149) throws "Collection was modified": the subscription adds to the plain `List<GameEventEnvelope>` from the push thread while the test enumerates it. The test needs a lock or a concurrent collection, or to wait for the push before reading.

### Background systems lose nine tenths of their daily industry

`StarSystem.SetActivityStateInternal` sets `ManagerSubpulses.FrequencyMultiplier = 10` in Background, so a system nobody watches runs its daily hotloop processors every ten days. `IndustryProcessor` (`IndustryTools.ConstructStuff`) and `LocalConstructionProcessor` ignore `deltaSeconds` and add one day's points per run, so an unwatched colony makes a tenth of its output. Found by the vertical-slice play-test (`VerticalSlicePlaytest`, local): early-tech Earth in Background finished about one Prefab Construction Office a month on a 100-point line. A focused system (the client's `SetSystemFocus`) runs in Foreground and is fine. Other daily processors that apply a fixed day's work (power, research, population, mining) may share the problem. Fix: scale by elapsed days, or run a day per elapsed day.


### Phobos transfer completes when the departure burn finishes

2026-11-04. Orders-window plan slice 7d. A Phobos move now queues two `NewtonThrustAction` burns named `Transfer window` (depart, then arrive). When the departure succeeds, the ship agent drops the still-queued arrival and replans (`AgentProcessor.cs`, ship Active path, around line 312). A later wake with an empty queue sets the goal `Completed` (line 344) without checking that the ship has arrived. `MoveToPlanner.PlanActions` (around line 85) also returns done when every action still on the goal has succeeded, and that check does not look at the orbit. `ApplyPlanMessage` leaves the `Transfer to …` text when done carries an empty message. The old one-action circularise hid this. Do not weaken `AssertArrivedMatchingPhobos`, and do not put circularise back. Failing tests: the Phobos cases of `WarpOrder_EarthToPhobos_ArrivesOnTimeInPlaceWithWellFormedOrbit` (prograde, eccentric, retrograde), `LeftoverCircularAtMarsRetrograde`, the fueled one-day tick, and `MoveToPlan_HyperbolicAroundMarsNearPhobos_MatchesPhobosOrbit`. The Mars cases of that warp test still pass. The empty-tank long tick passes: the goal fails and the ship stays bound to Mars. Fix only when asked.

### A processor fault stops the clock quietly — the dialog is in, the crash save is not

2026-10-03. The silent half is done. `MasterTimePulse.AdvanceOneStep` catches one step, `Debugger.Break()` when a debugger is attached, and stores `LastFault` (exception text, `CurrentProcess`, system, both clocks). `NotifyWhenStopped` records a fault that escapes the task instead of dropping `t.Exception`. The client gets `GameEventType.SimulationFaulted` and shows a modal. `TimeStopDiagnosticTests.ProcessorFault_stops_the_clock_and_keeps_the_report` passes. No save is written.

The crash-save half is still unbuilt. Do not build it until asked. Two files: a `Game.Save` of the live game plus the fault text (a replay dump — the step may already be torn), and a resume file taken after `GameGlobalDateTime` is assigned, every few minutes. The half-finished step is discarded. Offer the checkpoint from the dialog. If it dies on the next unpause, say so. A data bug already in the checkpoint throws again. If `EnableMultiThreading` is on, checkpoint before the parallel pass. `DiscordCrashLogger` is not this path.

### Quickstart failure leaves a blank window

`MainMenuItems` sets `IsActive = false` after `NewGameMenu.QuickstartGame()` whether or not a game was created (`Pulsar4X.Client/Interface/Menus/MainMenuItems.cs`). `QuickstartGame` returns immediately when `GameLifecycle.Quickstart()` returns null, and does not turn the menu back on (`NewGameMenu.cs`). `Quickstart` catches failures and returns null (`Pulsar4X.Client.Host/GameLifecycle.cs`). The result is a blank viewport and no Escape menu.

### Price finding is a later economy session

2026-10-10. `OfferStockPlan` posts a new row at ask 0 and bid 0 because cargo has no catalog price. Strata Survey, Lode Mining, and Ceres Depot need non-zero prices so charts, fuel, and ore actually move cash. Do not build price finding in the faction-goal pass. What a chart, a unit of ore, and a unit of fuel are worth, and how a bid moves, belongs in an economy session. [design/colony-administrator.md](design/colony-administrator.md) already holds the bid notes. Do not implement them from this inbox item.

Interim for the Ceres trade test only. Methalox already has `WealthCost` 3 on the processed-material blueprint. Use that as the depot's fuel ask. Ore and survey charts have no catalog number, so their starting rows use fixed non-zero ask and bid, with the bid below the ask. Leave `OfferStockPlan`'s default of 0 in place. Luna Concord and Earth keep listing at 0 until that session.

### A single-ship faction cannot post its own buy or sell orders

2026-10-10. Strata Survey and Lode Mining have a ship and no colony. A market order is stored on the colony that owns the logistics office. `EngineGameServer.SubmitCommand` rejects a command against an entity the faction does not own. `BuyIntelCommand` is the exception, and it buys a chart the owner already listed. A friendly ship can fill an existing listing with `MarketExchangeAction` when `FactionStanceRules.CanTrade` is true and the ship is in cargo-transfer range. It cannot add a bid or an ask. `MarketView.CanEdit` is true only for the owner. Do not build a visitor order book unless asked.
