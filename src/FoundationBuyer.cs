using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Amplitude.Mercury.Interop;
using Amplitude.Mercury.Sandbox;
using BepInEx;
using Newtonsoft.Json;
using UnityEngine;

namespace AutoFoundations
{
    // Main thread: spends influence above the reserve on foundations with the game's own "build foundation" order, the
    // same order a click on a foundation tile sends. Automatically while AutoBuy is on, or once on "Buy now". Each order
    // is tracked with the game's ticket, so a purchase is only counted once the game reports it carried it out.
    internal sealed class FoundationBuyer
    {
        // A ticket the game hasn't answered by then is settled from the scans instead.
        private const float TicketTimeoutSeconds = 10f;
        private const int MaxAttemptsPerTile = 3;
        // A few per frame, and the next ones only from a scan taken after those were answered, so each choice sees the
        // influence already spent.
        private const int MaxOrdersPerUpdate = 10;
        private const int HistoryLength = 15;

        private sealed class PendingFoundation
        {
            public ulong City;
            public float Cost;
            public float[] Yields;
            public float Time;
            public int Scan;
            public PostOrderTicket Ticket;
        }

        // Tile -> order sent and not answered yet.
        private readonly Dictionary<int, PendingFoundation> pending = new Dictionary<int, PendingFoundation>();
        // This turn: orders sent per tile, tiles bought, tiles the game refused.
        private readonly Dictionary<int, int> attempts = new Dictionary<int, int>();
        private readonly HashSet<int> bought = new HashSet<int>();
        private readonly HashSet<int> refused = new HashSet<int>();
        // Spots seen this turn, to report the ones that open up during it.
        private readonly HashSet<int> seenSpots = new HashSet<int>();
        private readonly List<string> history = new List<string>();
        private readonly Dictionary<ulong, string> cityNames = new Dictionary<ulong, string>();
        private FoundationSettings settings;
        private string gameId;
        private int turn = -1;
        private int seenScan;
        // Only act on scans taken after this one: older ones may list tiles built on since, or influence already spent.
        private int freshAfterScan;
        private bool wanted;
        private bool buyNow;
        // The purchases since the buyer last went idle, for one summary line in the log.
        private int burstBought;
        private float burstSpent;
        private float burstStartInfluence = float.NaN;

        // Set by the window, before each Update, while it is open.
        public bool Watched { get; set; }

        public FoundationState State { get; private set; }

        public bool InGame => State != null;

        public int BoughtThisTurn { get; private set; }

        public float SpentThisTurn { get; private set; }

        // Time.unscaledTime of the last purchase, for the on-screen notice.
        public float LastBoughtAt { get; private set; } = -1f;

        public int RefusedThisTurn => refused.Count;

        public bool IsBuying => pending.Count > 0 || buyNow;

        // Newest first.
        public IReadOnlyList<string> History => history;

        public void BuyNow()
        {
            buyNow = true;
            freshAfterScan = FoundationCapture.Latest?.Scan ?? 0;
            FoundationCapture.RequestRefresh();
        }

        public bool IsCityOff(ulong city) => settings != null && settings.OffCities.Contains(Key(city));

        public void SetCityOff(ulong city, bool off)
        {
            if (settings == null)
            {
                return;
            }
            settings.OffCities.Remove(Key(city));
            if (off)
            {
                settings.OffCities.Add(Key(city));
            }
            Save();
        }

        public string CityName(ulong guid)
        {
            if (cityNames.TryGetValue(guid, out string name))
            {
                return name;
            }
            name = null;
            try
            {
                if (State?.FindCity(guid)?.NameSource is EntityNameInfo info)
                {
                    name = System.Text.RegularExpressions.Regex.Replace(info.ToString() ?? string.Empty, "<[^>]*>", string.Empty).Trim();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"No display name for city {guid}: {e.Message}");
            }
            if (string.IsNullOrEmpty(name))
            {
                return "City " + guid;
            }
            cityNames[guid] = name;
            return name;
        }

        public void Update()
        {
            bool inGame = SandboxManager.Sandbox != null;
            bool auto = Plugin.AutoBuy.Value;
            bool nowWanted = inGame && (Watched || auto || buyNow || pending.Count > 0);
            if (nowWanted && !wanted)
            {
                freshAfterScan = FoundationCapture.Latest?.Scan ?? 0;
                FoundationCapture.RequestRefresh();
            }
            wanted = nowWanted;
            FoundationCapture.Wanted = nowWanted;

            FoundationState state = FoundationCapture.Latest;
            if (!inGame || state == null || state.GameId != SandboxManager.GameID)
            {
                if (!inGame && gameId != null)
                {
                    CloseGame();
                }
                return;
            }
            if (state.GameId != gameId)
            {
                OpenGame(state.GameId);
            }
            State = state;
            CheckTickets(state);
            if (state.Turn != turn)
            {
                // A burst the turn's end cut short gets no summary: the new turn's influence would make it wrong.
                ResetBurst();
                // "Buy now" is for the turn it was clicked in.
                buyNow = buyNow && turn < 0;
                turn = state.Turn;
                pending.Clear();
                attempts.Clear();
                bought.Clear();
                refused.Clear();
                seenSpots.Clear();
                cityNames.Clear();
                BoughtThisTurn = 0;
                SpentThisTurn = 0f;
            }
            bool newScan = state.Scan != seenScan;
            if (newScan)
            {
                seenScan = state.Scan;
                ReportNewSpots(state);
            }
            bool fresh = state.Scan > freshAfterScan;
            int posted = 0;
            if ((auto || buyNow) && state.CanAct && state.IsHuman && fresh)
            {
                posted = PostOrders(state);
                if (buyNow && posted == 0 && pending.Count == 0)
                {
                    buyNow = false;
                }
            }
            if (fresh && posted == 0 && pending.Count == 0)
            {
                LogSummary(state);
            }
        }

        public void Save()
        {
            if (settings == null || string.IsNullOrEmpty(gameId))
            {
                return;
            }
            try
            {
                string path = PathFor(gameId);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(settings, Formatting.Indented));
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(temp, path);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Could not save the settings of game {gameId}: {e.Message}");
            }
        }

        private void OpenGame(string id)
        {
            gameId = id;
            settings = Load(id);
            pending.Clear();
            attempts.Clear();
            bought.Clear();
            refused.Clear();
            seenSpots.Clear();
            history.Clear();
            cityNames.Clear();
            turn = -1;
            seenScan = 0;
            buyNow = false;
            BoughtThisTurn = 0;
            SpentThisTurn = 0f;
            ResetBurst();
        }

        private void CloseGame()
        {
            gameId = null;
            settings = null;
            State = null;
            pending.Clear();
            buyNow = false;
            ResetBurst();
        }

        // The game's answer to each order: Valid = carried out, Invalid = its own checks refused it, Rejected = not
        // accepted at this moment of the turn (tried again, up to MaxAttemptsPerTile).
        private void CheckTickets(FoundationState state)
        {
            if (pending.Count == 0)
            {
                return;
            }
            float now = Time.unscaledTime;
            bool answered = false;
            foreach (KeyValuePair<int, PendingFoundation> pair in pending.ToList())
            {
                PendingFoundation order = pair.Value;
                PostOrderTicket ticket = order.Ticket;
                bool waiting = ticket != null && ticket.Status == Amplitude.AsyncStatus.Started;
                if (waiting && now - order.Time < TicketTimeoutSeconds)
                {
                    continue;
                }
                pending.Remove(pair.Key);
                answered = true;
                PostOrderResponse result = ticket != null && ticket.Status == Amplitude.AsyncStatus.Completed ? ticket.Result : PostOrderResponse.Undefined;
                if (result == PostOrderResponse.Valid)
                {
                    Bought(state, pair.Key, order, string.Empty);
                    continue;
                }
                if (result == PostOrderResponse.Invalid)
                {
                    refused.Add(pair.Key);
                    Plugin.Log.LogWarning($"Turn {state.Turn}: the game refused a foundation in {CityName(order.City)} (tile {pair.Key}); leaving that spot until next turn.");
                    continue;
                }
                // No answer in time: a later scan that no longer offers the tile means it was built.
                if ((ticket == null || waiting) && state.Scan > order.Scan && !state.Spots.Any(s => s.Tile == pair.Key))
                {
                    Bought(state, pair.Key, order, " (no answer from the game, but the spot is gone)");
                    continue;
                }
                if (Attempts(pair.Key) >= MaxAttemptsPerTile)
                {
                    refused.Add(pair.Key);
                    Plugin.Log.LogWarning($"Turn {state.Turn}: the game did not take a foundation order for {CityName(order.City)} (tile {pair.Key}, {result}) {Attempts(pair.Key)} times; leaving that spot until next turn.");
                }
                else if (Plugin.LogPurchases.Value)
                {
                    Plugin.Log.LogInfo($"Turn {state.Turn}: foundation order for {CityName(order.City)} (tile {pair.Key}) not taken ({result}); trying again.");
                }
            }
            if (answered)
            {
                // Choose again only from a scan taken after these answers, which shows the influence they spent.
                freshAfterScan = FoundationCapture.Latest?.Scan ?? 0;
                FoundationCapture.RequestRefresh();
            }
        }

        private void Bought(FoundationState state, int tile, PendingFoundation order, string note)
        {
            bought.Add(tile);
            BoughtThisTurn++;
            SpentThisTurn += order.Cost;
            burstBought++;
            burstSpent += order.Cost;
            LastBoughtAt = Time.unscaledTime;
            string text = $"{CityName(order.City)}: {(order.Cost > 0f ? $"{order.Cost:#,0} influence" : "free")}, tile yields {Yield.Describe(order.Yields)}";
            history.Insert(0, $"Turn {state.Turn}: {text}");
            if (history.Count > HistoryLength)
            {
                history.RemoveAt(history.Count - 1);
            }
            if (Plugin.LogPurchases.Value)
            {
                Plugin.Log.LogInfo($"Turn {state.Turn}: foundation bought in {text}{note}.");
            }
        }

        // Spots that were not there earlier this turn (a new district, a spot freed up...), for the log.
        private void ReportNewSpots(FoundationState state)
        {
            bool first = seenSpots.Count == 0;
            var opened = new List<FoundationSpot>();
            foreach (FoundationSpot spot in state.Spots)
            {
                if (seenSpots.Add(spot.Tile) && !first)
                {
                    opened.Add(spot);
                }
            }
            if (opened.Count > 0 && Plugin.LogPurchases.Value)
            {
                string where = string.Join(", ", opened.GroupBy(s => s.City).Select(g => $"{CityName(g.Key)} {g.Count()}").ToArray());
                int nextToNew = opened.Count(s => s.Neighbours != null && s.Neighbours.Any(bought.Contains));
                Plugin.Log.LogInfo($"Turn {state.Turn}: {opened.Count} new foundation {(opened.Count == 1 ? "spot" : "spots")} opened up ({where}); {nextToNew} next to a foundation bought this turn.");
            }
        }

        // One line when the buyer goes idle: what it bought, and the influence before and after (from the scans).
        private void LogSummary(FoundationState state)
        {
            if (burstBought > 0 && Plugin.LogPurchases.Value)
            {
                string start = float.IsNaN(burstStartInfluence) ? "?" : burstStartInfluence.ToString("#,0");
                Plugin.Log.LogInfo($"Turn {state.Turn}: {burstBought} {(burstBought == 1 ? "foundation" : "foundations")} bought for {burstSpent:#,0} influence; influence {start} -> {state.Influence:#,0}; {state.Spots.Count} spots left.");
            }
            ResetBurst();
        }

        private void ResetBurst()
        {
            burstBought = 0;
            burstSpent = 0f;
            burstStartInfluence = float.NaN;
        }

        private int Attempts(int tile) => attempts.TryGetValue(tile, out int n) ? n : 0;

        private int PostOrders(FoundationState state)
        {
            float committed = pending.Values.Sum(p => p.Cost);
            List<FoundationSpot> chosen = FoundationRules.Choose(state, Plugin.KeepInfluence.Value, committed,
                spot => !pending.ContainsKey(spot.Tile) && !bought.Contains(spot.Tile) && !refused.Contains(spot.Tile)
                    && Attempts(spot.Tile) < MaxAttemptsPerTile && !IsCityOff(spot.City),
                spot => FoundationRules.Value(spot.Yields, state.FindCity(spot.City)?.Weights));
            float now = Time.unscaledTime;
            int posted = 0;
            foreach (FoundationSpot spot in chosen)
            {
                if (posted >= MaxOrdersPerUpdate)
                {
                    break;
                }
                if (float.IsNaN(burstStartInfluence))
                {
                    burstStartInfluence = state.Influence;
                }
                attempts[spot.Tile] = Attempts(spot.Tile) + 1;
                pending[spot.Tile] = new PendingFoundation
                {
                    City = spot.City,
                    Cost = Math.Max(0f, spot.Cost),
                    Yields = spot.Yields,
                    Time = now,
                    Scan = state.Scan,
                    Ticket = SandboxManager.PostAndTrackOrder(new OrderBuildFoundationAt(spot.City, spot.Tile)),
                };
                posted++;
            }
            if (posted > 0)
            {
                FoundationCapture.RequestRefresh();
            }
            return posted;
        }

        private static string Key(ulong guid) => guid.ToString(System.Globalization.CultureInfo.InvariantCulture);

        private static string PathFor(string id)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                id = id.Replace(c, '_');
            }
            return Path.Combine(Path.Combine(Paths.ConfigPath, "AutoFoundations"), id + ".json");
        }

        private static FoundationSettings Load(string id)
        {
            string path = PathFor(id);
            if (!File.Exists(path))
            {
                return new FoundationSettings();
            }
            try
            {
                FoundationSettings loaded = JsonConvert.DeserializeObject<FoundationSettings>(File.ReadAllText(path)) ?? new FoundationSettings();
                loaded.OffCities = loaded.OffCities ?? new List<string>();
                return loaded;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Could not read {path} ({e.Message}); starting with every city on.");
                return new FoundationSettings();
            }
        }
    }
}
