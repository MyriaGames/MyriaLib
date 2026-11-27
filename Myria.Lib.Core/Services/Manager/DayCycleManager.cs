using Myria.Lib.Core.Systems;
using Myria.Lib.Core.Systems.Enums;

namespace Myria.Lib.Core.Services.Manager
{
    public static class DayCycleManager
    {
        // ── Configuration ────────────────────────────────────────────────────────

        /// <summary>
        /// How many ticks constitute one time segment.
        /// Four segments make one full game-day, so a day costs 4 × this value.
        /// Default 50 → 200 ticks/day. Tune per app before calling Initialize.
        /// </summary>
        public static int TicksPerSegment { get; set; } = 50;

        // ── State ────────────────────────────────────────────────────────────────

        public static string CurrentTimeSegment => GameService.Game.TimeOfDay;
        public static int GameDay => GameService.Game.GameDay;

        /// <summary>Ticks accumulated toward the next segment transition (0 … TicksPerSegment-1).</summary>
        public static int CurrentTicks => GameService.Game.Ticks;

        // ── Events ───────────────────────────────────────────────────────────────

        /// <summary>Fires each time the time-of-day segment changes. Carries the new segment.</summary>
        public static event Action<string>? SegmentChanged;

        /// <summary>Fires each time a new game-day begins. Carries the new day number.</summary>
        public static event Action<int>? DayAdvanced;

        // ── Inactivity timer ─────────────────────────────────────────────────────

        private static CancellationTokenSource? _inactivityCts;
        private static readonly object _lock = new();

        // ── Public API ───────────────────────────────────────────────────────────

        /// <summary>
        /// Restores in-game time state from the loaded <see cref="GameService.Game"/> and
        /// rolls daily gather limits for all rooms that have gathering spots.
        /// Called by <see cref="GameService.InitializeGame"/> — do not call manually.
        /// </summary>
        public static void Initialize()
        {
            // Clamp saved ticks in case TicksPerSegment was changed since the last save.
            var game = GameService.Game;
            if (game.Ticks < 0 || game.Ticks >= TicksPerSegment)
                game.Ticks = 0;

            RollGatherLimits();
        }

        /// <summary>
        /// Rolls today's gather charges for every room with gathering spots. Also needed after a
        /// data reload (GameService.InitializeGame with skipGameState): that builds new Room objects,
        /// which start with no charges - online, no room offered gathering at all.
        /// </summary>
        public static void RollGatherLimits()
        {
            foreach (var room in RoomService.AllRooms.Where(r => r.GatheringSpots.Count > 0))
                room.RollGatherLimit();
        }

        /// <summary>
        /// Adds <paramref name="ticks"/> to the in-game clock.
        /// Segments and days advance automatically as thresholds are crossed.
        /// Thread-safe — called from both the main thread and the inactivity timer.
        /// </summary>
        public static void AddTicks(int ticks)
        {
            if (ticks <= 0) return;

            lock (_lock)
            {
                var game = GameService.Game;
                game.Ticks += ticks;

                while (game.Ticks >= TicksPerSegment)
                {
                    game.Ticks -= TicksPerSegment;
                    AdvanceSegmentInternal(game);
                }

                game.LastSavedAt = DateTime.Now;
                GameStatusService.Save(game);
            }
        }

        /// <summary>
        /// Starts a background timer that slowly adds ticks while the player is idle.
        /// Useful to give the impression that time passes even without input.
        /// Call <see cref="StopInactivityTimer"/> before starting a new one or on app shutdown.
        /// </summary>
        /// <param name="ticksPerInterval">Ticks added each interval (default 1).</param>
        /// <param name="intervalMs">Milliseconds between each tick injection (default 10 000 = 10 s).</param>
        public static void StartInactivityTimer(int ticksPerInterval = 1, int intervalMs = 10_000)
        {
            StopInactivityTimer();

            _inactivityCts = new CancellationTokenSource();
            var token = _inactivityCts.Token;

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(intervalMs, token);
                        AddTicks(ticksPerInterval);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, token);
        }

        /// <summary>Stops the inactivity timer if one is running.</summary>
        public static void StopInactivityTimer()
        {
            _inactivityCts?.Cancel();
            _inactivityCts = null;
        }

        // ── Private ──────────────────────────────────────────────────────────────

        private static readonly string[] _segments = TimeSegment.AllBuiltIn.ToArray();

        private static void AdvanceSegmentInternal(GameStatus game)
        {
            int current = Array.IndexOf(_segments, game.TimeOfDay);
            int next = (current + 1) % _segments.Length; // -1 (unrecognized) wraps to 0, same as a new day
            bool newDay = next == 0; // Night → Morning wrap

            game.TimeOfDay = _segments[next];
            SegmentChanged?.Invoke(game.TimeOfDay);

            if (newDay)
            {
                game.GameDay++;
                OnNewDay();
                DayAdvanced?.Invoke(game.GameDay);
            }
        }

        private static void OnNewDay() => RollGatherLimits();
    }
}
