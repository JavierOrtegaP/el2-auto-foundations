using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoFoundations.Tests
{
    internal static class Program
    {
        private static int failures;
        private static int checks;

        private static int Main()
        {
            Run(nameof(KeepsTheReserve), KeepsTheReserve);
            Run(nameof(FreeSpotsAlwaysTaken), FreeSpotsAlwaysTaken);
            Run(nameof(BestValueFirst), BestValueFirst);
            Run(nameof(FollowsCityStrategy), FollowsCityStrategy);
            Run(nameof(CountsPendingAndExclusions), CountsPendingAndExclusions);
            Run(nameof(BuysEverythingWithEnoughInfluence), BuysEverythingWithEnoughInfluence);
            Run(nameof(DescribesYields), DescribesYields);
            Console.WriteLine();
            Console.WriteLine(failures == 0 ? $"All {checks} checks passed." : $"{failures} of {checks} checks FAILED.");
            return failures == 0 ? 0 : 1;
        }

        // Only influence above the reserve is spent.
        private static void KeepsTheReserve()
        {
            FoundationState state = State(1000f, Spot(1, 10, 300f, 2f), Spot(1, 11, 300f, 2f), Spot(2, 12, 0f, 0f));
            List<FoundationSpot> chosen = FoundationRules.Choose(state, 500f, 0f, null, null);
            Expect(Tiles(chosen).SequenceEqual(new[] { 12, 10 }), $"free spot, then one at 300 (1000 - 500 kept), got {Show(chosen)}");
        }

        private static void FreeSpotsAlwaysTaken()
        {
            List<FoundationSpot> chosen = FoundationRules.Choose(State(100f, Spot(1, 10, 300f, 2f), Spot(2, 12, 0f, 0f)), 1000f, 0f, null, null);
            Expect(Tiles(chosen).SequenceEqual(new[] { 12 }), $"below the reserve: only the free spot, got {Show(chosen)}");
        }

        // Most yield per influence first; one too expensive doesn't stop cheaper ones.
        private static void BestValueFirst()
        {
            FoundationState state = State(700f, Spot(1, 10, 500f, 6f), Spot(1, 11, 150f, 4f), Spot(1, 12, 50f, 0f), Spot(1, 13, 300f, 3f));
            List<FoundationSpot> chosen = FoundationRules.Choose(state, 0f, 0f, null, null);
            // Per influence: 11 = 5/150, 12 = 1/50, 10 = 7/500, 13 = 4/300. 11 + 12 + 10 = 700; 13 no longer fits.
            Expect(Tiles(chosen).SequenceEqual(new[] { 11, 12, 10 }), $"11, 12, 10, got {Show(chosen)}");
            chosen = FoundationRules.Choose(State(400f, Spot(1, 10, 500f, 6f), Spot(1, 13, 300f, 3f)), 0f, 0f, null, null);
            Expect(Tiles(chosen).SequenceEqual(new[] { 13 }), $"the 500 one doesn't fit, the 300 one does, got {Show(chosen)}");
        }

        // The city's job strategy weighs the yields.
        private static void FollowsCityStrategy()
        {
            FoundationSpot food = Spot(1, 10, 100f, 0f);
            food.Yields[Yield.Food] = 3f;
            FoundationSpot industry = Spot(1, 11, 100f, 0f);
            industry.Yields[Yield.Industry] = 2f;
            float[] industryFirst = { 1f, 2f, 1f, 1f, 1f, 1f };
            List<FoundationSpot> chosen = FoundationRules.Choose(State(100f, food, industry), 0f, 0f, null, s => FoundationRules.Value(s.Yields, industryFirst));
            Expect(Tiles(chosen).SequenceEqual(new[] { 11 }), "industry strategy: 2 Industry beats 3 Food");
            chosen = FoundationRules.Choose(State(100f, food, industry), 0f, 0f, null, s => FoundationRules.Value(s.Yields, null));
            Expect(Tiles(chosen).SequenceEqual(new[] { 10 }), "balanced: 3 Food beats 2 Industry");
        }

        // Orders already sent count against the budget; excluded spots (cities off, pending, refused) are skipped.
        private static void CountsPendingAndExclusions()
        {
            FoundationState state = State(1000f, Spot(1, 10, 400f, 2f), Spot(2, 11, 400f, 2f), Spot(1, 12, 400f, 2f));
            List<FoundationSpot> chosen = FoundationRules.Choose(state, 0f, 400f, s => s.Tile != 10, null);
            Expect(Tiles(chosen).SequenceEqual(new[] { 11 }), $"400 already committed for tile 10: one more fits, got {Show(chosen)}");
            chosen = FoundationRules.Choose(state, 0f, 0f, s => s.City != 1, null);
            Expect(Tiles(chosen).SequenceEqual(new[] { 11 }), $"city 1 off, got {Show(chosen)}");
        }

        // The user's case: tens of thousands of influence, every spot bought in one go.
        private static void BuysEverythingWithEnoughInfluence()
        {
            var spots = new List<FoundationSpot>();
            for (int i = 0; i < 60; i++)
            {
                spots.Add(Spot((ulong)(i % 12), 100 + i, i % 5 == 0 ? 0f : 50f * (1 + i % 10), i % 4));
            }
            FoundationState state = State(78000f, spots.ToArray());
            List<FoundationSpot> chosen = FoundationRules.Choose(state, 1000f, 0f, null, null);
            Expect(chosen.Count == 60, $"all 60 bought, got {chosen.Count}");
            Expect(chosen.Sum(s => s.Cost) <= 77000f, "the 1000 kept");
        }

        private static void DescribesYields()
        {
            var yields = new float[Yield.Count];
            Expect(Yield.Describe(yields) == "nothing", "no yields");
            yields[Yield.Food] = 2f;
            yields[Yield.Science] = 1.5f;
            Expect(Yield.Describe(yields) == "+2 Food, +1.5 Science", $"got {Yield.Describe(yields)}");
        }

        private static FoundationState State(float influence, params FoundationSpot[] spots)
        {
            var state = new FoundationState { Influence = influence };
            state.Spots.AddRange(spots);
            return state;
        }

        // A spot whose tile yields `industry` Industry.
        private static FoundationSpot Spot(ulong city, int tile, float cost, float industry)
        {
            var spot = new FoundationSpot { City = city, Tile = tile, Cost = cost };
            spot.Yields[Yield.Industry] = industry;
            return spot;
        }

        private static int[] Tiles(List<FoundationSpot> spots) => spots.Select(s => s.Tile).ToArray();

        private static string Show(List<FoundationSpot> spots) => string.Join(",", Tiles(spots));

        private static void Expect(bool condition, string what)
        {
            checks++;
            if (!condition)
            {
                failures++;
                Console.WriteLine("    FAIL: " + what);
            }
        }

        private static void Run(string name, Action test)
        {
            int before = failures;
            try
            {
                test();
            }
            catch (Exception e)
            {
                failures++;
                checks++;
                Console.WriteLine($"    EXCEPTION: {e}");
            }
            Console.WriteLine((failures == before ? "ok    " : "FAIL  ") + name);
        }
    }
}
