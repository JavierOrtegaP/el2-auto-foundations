using System;
using System.Threading;
using Amplitude.Mercury.Data.Simulation;
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Sandbox;
using Amplitude.Mercury.Simulation;
using HarmonyLib;

namespace AutoFoundations
{
    // Finds every tile the local empire could build a foundation on, using the same checks and prices as the game's own
    // "build foundation" order. Runs on the sandbox thread right after the game copies its state for the UI
    // (Snapshots.Synchronize runs there while the simulation is idle) and hands the main thread an immutable
    // FoundationState. Only when the simulation moved on since the last scan (its frame counter goes up whenever it
    // processed something: an order, a state change...), or when asked.
    [HarmonyPatch(typeof(Snapshots), nameof(Snapshots.Synchronize))]
    internal static class FoundationCapture
    {
        private static int lastFrame = -1;
        private static Sandbox lastSandbox;
        private static int scan;
        private static FoundationState latest;
        private static volatile bool refreshRequested;

        // Set by the main thread: a scan checks every tile of every city, so it only runs while something uses it.
        internal static volatile bool Wanted;

        internal static FoundationState Latest => Volatile.Read(ref latest);

        // Scan on the next sync even if the frame counter hasn't moved (after posting orders, on opening the window...).
        internal static void RequestRefresh() => refreshRequested = true;

        [HarmonyPostfix]
        private static void Postfix()
        {
            if (!Wanted || Guard.HasFailed(typeof(FoundationCapture)))
            {
                return;
            }
            try
            {
                Capture();
            }
            catch (Exception e)
            {
                Guard.Fail(typeof(FoundationCapture), e);
            }
        }

        private static void Capture()
        {
            Sandbox sandbox = SandboxManager.Sandbox;
            if (sandbox == null || !sandbox.IsInitialized)
            {
                return;
            }
            int frame = Sandbox.Frame;
            if (!refreshRequested && frame == lastFrame && ReferenceEquals(sandbox, lastSandbox))
            {
                return;
            }
            refreshRequested = false;
            lastFrame = frame;
            lastSandbox = sandbox;
            int empireIndex = sandbox.LocalEmpireIndex;
            MajorEmpire[] empires = Sandbox.MajorEmpires;
            if (empires == null || empireIndex < 0 || empireIndex >= empires.Length || ReferenceEquals(empires[empireIndex], null))
            {
                return;
            }
            MajorEmpire empire = empires[empireIndex];
            var state = new FoundationState
            {
                GameId = SandboxManager.GameID ?? string.Empty,
                Turn = sandbox.Turn,
                Scan = ++scan,
                IsHuman = empire.IsControlledByHuman,
                CanAct = sandbox.LocalPlayerJoined && empire.IsAlive && !empire.IsReady && sandbox.IsCurrentStateOfType<SandboxState_TurnMain>(),
                Influence = (float)empire.InfluenceStock.Value,
                InfluenceNet = (float)empire.InfluenceNet.Value,
            };
            DistrictDefinition foundation = empire.DepartmentOfTheInterior?.FoundationDefinition;
            bool needsResources = !ReferenceEquals(foundation, null) && foundation.ResourcePrerequisites != null && foundation.ResourcePrerequisites.Length > 0;
            for (int i = 0; i < empire.Settlements.Count; i++)
            {
                Settlement settlement = empire.Settlements[i];
                if (ReferenceEquals(settlement, null) || settlement.SettlementStatus != SettlementStatuses.City)
                {
                    continue;
                }
                state.Cities.Add(new FoundationCity
                {
                    Guid = settlement.GUID,
                    NameSource = settlement.EntityName,
                    Weights = Weights(settlement),
                });
                if (!ReferenceEquals(foundation, null))
                {
                    AddSpots(state, empire, settlement, foundation, needsResources);
                }
            }
            Volatile.Write(ref latest, state);
        }

        private static void AddSpots(FoundationState state, MajorEmpire empire, Settlement settlement, DistrictDefinition foundation, bool needsResources)
        {
            Region region = settlement.Region.Entity;
            if (ReferenceEquals(region, null))
            {
                return;
            }
            for (int t = 0; t < region.Territories.Count; t++)
            {
                Territory territory = region.Territories[t];
                int[] tiles = ReferenceEquals(territory, null) ? null : territory.TileIndexes;
                if (tiles == null)
                {
                    continue;
                }
                // The dust price doesn't depend on the tile; it is 0 for the standard factions.
                bool needsMoney = (float)empire.DepartmentOfTheTreasury.GetConstructibleMoneyInstantCost(settlement, territory, foundation) > 0f;
                for (int j = 0; j < tiles.Length; j++)
                {
                    int tileIndex = tiles[j];
                    if (!ConstructibleHelper.CanBuildFoundationAt(settlement, tileIndex) || IsQuestProtected(tileIndex))
                    {
                        continue;
                    }
                    if (needsMoney || needsResources)
                    {
                        state.OtherCostSpots++;
                        continue;
                    }
                    var spot = new FoundationSpot
                    {
                        City = settlement.GUID,
                        Tile = tileIndex,
                        Cost = (float)ConstructibleHelper.GetFoundationInfluenceCostAt(settlement, tileIndex),
                        Neighbours = new int[6],
                    };
                    Amplitude.Mercury.WorldPosition.FillNeighboursFromTileIndex(tileIndex, spot.Neighbours);
                    Tile tile = Sandbox.World.Tiles[tileIndex];
                    if (!ReferenceEquals(tile, null))
                    {
                        spot.Yields[Yield.Food] = (float)tile.FoodProduced.Value;
                        spot.Yields[Yield.Industry] = (float)tile.IndustryProduced.Value;
                        spot.Yields[Yield.Money] = (float)tile.MoneyProduced.Value;
                        spot.Yields[Yield.Science] = (float)tile.ScienceProduced.Value;
                        spot.Yields[Yield.Influence] = (float)tile.InfluenceProduced.Value;
                        spot.Yields[Yield.Approval] = (float)tile.ApprovalProduced.Value;
                    }
                    state.Spots.Add(spot);
                }
            }
        }

        // The city's job strategy, the same weights the game uses to place populations.
        private static float[] Weights(Settlement settlement)
        {
            FimsInfo weights = settlement.PopulationAssignementPonderation;
            var result = new[] { (float)weights.Food, (float)weights.Industry, (float)weights.Money, (float)weights.Science, (float)weights.Influence, (float)weights.Approval };
            foreach (float weight in result)
            {
                if (weight != 0f)
                {
                    return result;
                }
            }
            return null;
        }

        // The game refuses a foundation on a tile a quest keeps for itself.
        private static bool IsQuestProtected(int tileIndex)
        {
            // IsEntityProtected is a default interface method: call it through the interface.
            ISimulationEntityWithQuestProtection collectible = Sandbox.CollectibleManager?.TryGetCollectibleReservingTile(tileIndex);
            return collectible != null && collectible.IsEntityProtected();
        }
    }
}
