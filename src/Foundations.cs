using System;
using System.Collections.Generic;

namespace AutoFoundations
{
    // What the mod needs from the game, taken on the sandbox thread (FoundationCapture) and read on the main thread. No
    // game types in here, so the choice of spots can be tested without the game.
    internal sealed class FoundationState
    {
        public string GameId = string.Empty;
        public int Turn;
        // Bumped on every scan, so the main thread can tell a scan taken after its orders from an older one.
        public int Scan;
        public bool IsHuman;
        // The local empire may give orders now: its turn is running and it has not ended it.
        public bool CanAct;
        public float Influence;
        public float InfluenceNet;
        public readonly List<FoundationCity> Cities = new List<FoundationCity>();
        // Spots the game would accept a foundation on now that cost influence only (or nothing).
        public readonly List<FoundationSpot> Spots = new List<FoundationSpot>();
        // Spots that would also cost dust or resources (some custom factions): left to the player.
        public int OtherCostSpots;

        public FoundationCity FindCity(ulong guid)
        {
            for (int i = 0; i < Cities.Count; i++)
            {
                if (Cities[i].Guid == guid)
                {
                    return Cities[i];
                }
            }
            return null;
        }
    }

    internal sealed class FoundationCity
    {
        public ulong Guid;
        // The game's name info (boxed EntityNameInfo); turned into text on the main thread, as it localizes.
        public object NameSource;
        // The city's job strategy weights (Balanced, Food, Industry, Science), per yield.
        public float[] Weights;
    }

    // What the player chose for one game, saved in BepInEx/config/AutoFoundations/<game id>.json.
    internal sealed class FoundationSettings
    {
        // Settlement GUIDs of the cities the mod never buys foundations in.
        public List<string> OffCities = new List<string>();
    }

    internal sealed class FoundationSpot
    {
        public ulong City;
        public int Tile;
        // Influence, as the game charges it (0 right next to an administrative district).
        public float Cost;
        // What the tile yields once exploited (Yield indices).
        public float[] Yields = new float[Yield.Count];
        // The six tiles around it (-1 off the map), to tell which spots open up next to new foundations.
        public int[] Neighbours;
    }

    internal static class Yield
    {
        public const int Food = 0;
        public const int Industry = 1;
        public const int Money = 2;
        public const int Science = 3;
        public const int Influence = 4;
        public const int Approval = 5;
        public const int Count = 6;

        public static readonly string[] Labels = { "Food", "Industry", "Dust", "Science", "Influence", "Approval" };

        // "+2 Food, +1 Industry"; "nothing" when every yield is 0.
        public static string Describe(float[] yields)
        {
            var parts = new List<string>();
            for (int f = 0; f < Count; f++)
            {
                if (Math.Abs(yields[f]) >= 0.05f)
                {
                    parts.Add((yields[f] > 0 ? "+" : string.Empty) + yields[f].ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " " + Labels[f]);
                }
            }
            return parts.Count == 0 ? "nothing" : string.Join(", ", parts.ToArray());
        }
    }

    internal static class FoundationRules
    {
        // The spots to buy now, best first: free ones, then the most yield per influence, as long as the influence left
        // stays at or above the reserve. `committed` is influence already spent by orders the game hasn't applied yet.
        public static List<FoundationSpot> Choose(FoundationState state, float reserve, float committed, Func<FoundationSpot, bool> allowed, Func<FoundationSpot, float> value)
        {
            var candidates = new List<KeyValuePair<FoundationSpot, float>>();
            foreach (FoundationSpot spot in state.Spots)
            {
                if (allowed == null || allowed(spot))
                {
                    candidates.Add(new KeyValuePair<FoundationSpot, float>(spot, Math.Max(0f, value != null ? value(spot) : Value(spot.Yields, null))));
                }
            }
            candidates.Sort((a, b) =>
            {
                bool freeA = a.Key.Cost <= 0f;
                bool freeB = b.Key.Cost <= 0f;
                if (freeA != freeB)
                {
                    return freeA ? -1 : 1;
                }
                // A spot that yields nothing still ranks by price.
                float rankA = freeA ? a.Value : (a.Value + 1f) / a.Key.Cost;
                float rankB = freeB ? b.Value : (b.Value + 1f) / b.Key.Cost;
                int byRank = rankB.CompareTo(rankA);
                if (byRank != 0)
                {
                    return byRank;
                }
                int byCost = a.Key.Cost.CompareTo(b.Key.Cost);
                return byCost != 0 ? byCost : a.Key.Tile.CompareTo(b.Key.Tile);
            });
            float budget = state.Influence - committed - Math.Max(0f, reserve);
            var chosen = new List<FoundationSpot>();
            foreach (KeyValuePair<FoundationSpot, float> candidate in candidates)
            {
                float cost = Math.Max(0f, candidate.Key.Cost);
                // Free spots are always taken, even below the reserve.
                if (cost > 0f && cost > budget)
                {
                    continue;
                }
                budget -= cost;
                chosen.Add(candidate.Key);
            }
            return chosen;
        }

        // A tile's yields weighted by the city's job strategy (null = all 1).
        public static float Value(float[] yields, float[] weights)
        {
            float total = 0f;
            for (int f = 0; f < Yield.Count; f++)
            {
                total += yields[f] * (weights != null && f < weights.Length ? weights[f] : 1f);
            }
            return total;
        }
    }
}
