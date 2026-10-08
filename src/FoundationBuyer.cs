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
    // is tracked with the game's ticket, so a purchase is only counted once the game reports it carried it out. Every
    // affordable spot is ordered at once (a foundation's price doesn't depend on the others bought), and the next ones
    // only from a scan taken after those were answered, so each choice sees the influence already spent.
    internal sealed class FoundationBuyer
    {
        private sealed class PendingFoundation
        {
            public ulong City;
            public float Cost;
            public float[] Yields;
            // The game's state when the order was sent: an order it turns down in that state is tried again once it
            // moved on to another.
            public string SentIn;
            public PostOrderTicket Ticket;
        }

        // Tile -> order sent and not answered yet.
        private readonly Dictionary<int, PendingFoundation> pending = new Dictionary<int, PendingFoundation>();
        // This turn: tiles bought, tiles the game refused.
        private readonly HashSet<int> bought = new HashSet<int>();
        private readonly HashSet<int> refused = new HashSet<int>();
        // Spots seen this turn, to report the ones that open up during it.
        private readonly HashSet<int> seenSpots = new HashSet<int>();
        // The purchases of historyTurn, the last turn anything was bought in.
        private readonly List<string> history = new List<string>();
        private readonly Dictionary<ulong, string> cityNames = new Dictionary<ulong, string>();
        private FoundationSettings settings;
        private string gameId;
        private int turn = -1;
        private int historyTurn = -1;
        private int seenScan;
        // Only act on scans taken after this one: older ones may list tiles built on since, or influence already spent.
        private int freshAfterScan;
        // The game's state in which it turned foundation orders down (not accepted at that moment of the turn), or
        // null: no orders until it has moved on to another state.
        private string rejectedIn;
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

        // The purchases of HistoryTurn, newest first.
        public IReadOnlyList<string> History => history;

        public int HistoryTurn => historyTurn;

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
                bought.Clear();
                refused.Clear();
                seenSpots.Clear();
                cityNames.Clear();
                rejectedIn = null;
                BoughtThisTurn = 0;
                SpentThisTurn = 0f;
            }
            bool newScan = state.Scan != seenScan;
            if (newScan)
            {
                seenScan = state.Scan;
                ReportNewSpots(state);
            }
            if (rejectedIn != null && rejectedIn != CurrentState())
            {
                rejectedIn = null;
            }
            bool fresh = state.Scan > freshAfterScan;
            int posted = 0;
            if ((auto || buyNow) && state.CanAct && state.IsHuman && fresh && rejectedIn == null)
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
            bought.Clear();
            refused.Clear();
            seenSpots.Clear();
            history.Clear();
            cityNames.Clear();
            turn = -1;
            historyTurn = -1;
            seenScan = 0;
            rejectedIn = null;
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
            rejectedIn = null;
            buyNow = false;
            ResetBurst();
        }

        // The game answers every order: Valid = carried out, Invalid = its own checks refused it (the spot is left until
        // next turn), Rejected = not accepted at this moment of the turn (ordered again once the game moved on).
        private void CheckTickets(FoundationState state)
        {
            if (pending.Count == 0)
            {
                return;
            }
            bool answered = false;
            foreach (KeyValuePair<int, PendingFoundation> pair in pending.ToList())
            {
                PendingFoundation order = pair.Value;
                PostOrderTicket ticket = order.Ticket;
                if (!ticket.IsDone)
                {
                    continue;
                }
                pending.Remove(pair.Key);
                answered = true;
                PostOrderResponse result = ticket.Status == Amplitude.AsyncStatus.Completed ? ticket.Result : PostOrderResponse.Undefined;
                if (result == PostOrderResponse.Valid)
                {
                    Bought(state, pair.Key, order);
                    continue;
                }
                if (result == PostOrderResponse.Rejected)
                {
                    rejectedIn = order.SentIn;
                    if (Plugin.LogPurchases.Value)
                    {
                        Plugin.Log.LogInfo($"Turn {state.Turn}: foundation order for {CityName(order.City)} (tile {pair.Key}) not accepted at this moment ({order.SentIn}); ordering again once the game moves on.");
                    }
                    continue;
                }
                refused.Add(pair.Key);
                Plugin.Log.LogWarning($"Turn {state.Turn}: the game refused a foundation in {CityName(order.City)} (tile {pair.Key}, {(result == PostOrderResponse.Invalid ? "its checks failed" : ticket.Status.ToString())}); leaving that spot until next turn.");
            }
            if (answered)
            {
                // Choose again only from a scan taken after these answers, which shows the influence they spent.
                freshAfterScan = FoundationCapture.Latest?.Scan ?? 0;
                FoundationCapture.RequestRefresh();
            }
        }

        private void Bought(FoundationState state, int tile, PendingFoundation order)
        {
            bought.Add(tile);
            BoughtThisTurn++;
            SpentThisTurn += order.Cost;
            burstBought++;
            burstSpent += order.Cost;
            LastBoughtAt = Time.unscaledTime;
            string text = $"{CityName(order.City)}: {(order.Cost > 0f ? $"{order.Cost:#,0} influence" : "free")}, tile yields {Yield.Describe(order.Yields)}";
            if (state.Turn != historyTurn)
            {
                history.Clear();
                historyTurn = state.Turn;
            }
            history.Insert(0, text);
            if (Plugin.LogPurchases.Value)
            {
                Plugin.Log.LogInfo($"Turn {state.Turn}: foundation bought in {text}.");
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

        private int PostOrders(FoundationState state)
        {
            float committed = pending.Values.Sum(p => p.Cost);
            List<FoundationSpot> chosen = FoundationRules.Choose(state, Plugin.KeepInfluence.Value, committed,
                spot => !pending.ContainsKey(spot.Tile) && !bought.Contains(spot.Tile) && !refused.Contains(spot.Tile) && !IsCityOff(spot.City),
                spot => FoundationRules.Value(spot.Yields, state.FindCity(spot.City)?.Weights));
            int posted = 0;
            foreach (FoundationSpot spot in chosen)
            {
                string sentIn = CurrentState();
                // No ticket: the game is closing.
                PostOrderTicket ticket = SandboxManager.PostAndTrackOrder(new OrderBuildFoundationAt(spot.City, spot.Tile));
                if (ticket == null)
                {
                    break;
                }
                if (float.IsNaN(burstStartInfluence))
                {
                    burstStartInfluence = state.Influence;
                }
                pending[spot.Tile] = new PendingFoundation
                {
                    City = spot.City,
                    Cost = Math.Max(0f, spot.Cost),
                    Yields = spot.Yields,
                    SentIn = sentIn,
                    Ticket = ticket,
                };
                posted++;
            }
            if (posted > 0)
            {
                FoundationCapture.RequestRefresh();
            }
            return posted;
        }

        private static string CurrentState() => SandboxManager.Sandbox?.CurrentStateName ?? string.Empty;

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
