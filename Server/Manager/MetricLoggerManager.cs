using Server.Utilities;
using SharedLibrary.Game;
using SharedLibrary.Game.Actions;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Server.Manager
{
    /// <summary>A cube pose a player moved the cube to, as the server handled it during a tick (accepted or not).</summary>
    /// <param name="CubeID">The cube that moved.</param>
    /// <param name="PlayerID">The player who moved it.</param>
    /// <param name="ArrivalTime">When the move reached the game (server time, seconds), after any emulated inbound delay.</param>
    /// <param name="Position">Where the player moved the cube to.</param>
    /// <param name="Accepted">
    /// True if the server applied the move. False if it lost a conflict (for example another player holds the cube): the cube did not
    /// move, but the player's own client already showed it moving, until the server's next state corrected it.
    /// </param>
    public readonly record struct CubePoseUpdate(string CubeID, int PlayerID, double ArrivalTime, Vector3 Position, bool Accepted = true);

    /// <summary>One authoritative position of a cube, with what is needed to work out when each client saw it.</summary>
    /// <param name="TickTime">The server time of the tick that broadcast this position.</param>
    /// <param name="ArrivalTime">When the move reached the server (the tick time for a cube nobody moved, such as a new one).</param>
    /// <param name="OriginatorPlayerID">The player who moved the cube here, or null if the server placed it.</param>
    /// <param name="Position">The position.</param>
    /// <param name="OnlyForPlayerID">
    /// If set, only this player saw this position (a rejected move of theirs, shown locally, or the server's correction of it);
    /// everyone else never did. Null for a position that was applied to the game and so reaches every player.
    /// </param>
    internal readonly record struct PoseSample(double TickTime, double ArrivalTime, int? OriginatorPlayerID, Vector3 Position, int? OnlyForPlayerID = null);

    /// <summary>
    /// The SLAM drift that was added to one player's alignment when the server sent it a global pose. The player's headset then places the
    /// shared content as if its own place in the global frame were the drifted pose, so a point at <c>g</c> in the global frame ends up where
    /// <c>TruePosition + inverse(Rotation) * (g - TruePosition - Translation)</c> is in the real room.
    /// </summary>
    /// <param name="SendTime">The server time the drifted pose was sent to the player (seconds).</param>
    /// <param name="TruePosition">The player's true position in the global frame (the pose before the drift was added).</param>
    /// <param name="Rotation">The rotation the drift added to the pose's heading (the drifted rotation times the inverse of the true one).</param>
    /// <param name="Translation">The shift the drift added to the pose's position.</param>
    internal readonly record struct DriftSample(double SendTime, Vector3 TruePosition, Quaternion Rotation, Vector3 Translation);

    /// <summary>The running totals of one kind of distance metric, for one player over one study condition.</summary>
    internal sealed class DistanceStat
    {
        // Distance and inconsistency exist together: both are missing on a tick where there was nothing to compare.
        public int ValidTicks;
        public double SumDistanceM;
        public double SumInconsistency;

        public void Add(double distanceM, double inconsistency)
        {
            if (double.IsNaN(distanceM) || double.IsNaN(inconsistency))
                return;

            ValidTicks++;
            SumDistanceM += distanceM;
            SumInconsistency += inconsistency;
        }

        public void Add(DistanceStat other)
        {
            ValidTicks += other.ValidTicks;
            SumDistanceM += other.SumDistanceM;
            SumInconsistency += other.SumInconsistency;
        }
    }

    /// <summary>The running totals of one player's metrics within one study condition, for averaging.</summary>
    internal sealed class PlayerStats
    {
        public int Ticks;
        public double SumEstimatedMs;
        public double SumEmulatedMs;
        public double SumLatencyMs;

        public readonly DistanceStat PairwiseState = new();      // needs another player to compare with
        public readonly DistanceStat RetroactiveState = new();   // against the server's authoritative state
        public readonly DistanceStat PairwiseSpatial = new();    // the pairwise comparison, after each player's SLAM drift
        public readonly DistanceStat RetroactiveSpatial = new(); // the retroactive comparison, after the player's SLAM drift
    }

    /// <summary>The mean distances (meters) of one tick for every player, NaN where there was nothing to compare.</summary>
    internal readonly record struct MeanDistances(
        Dictionary<int, double> PairwiseState,
        Dictionary<int, double> RetroactiveState,
        Dictionary<int, double> PairwiseSpatial,
        Dictionary<int, double> RetroactiveSpatial);

    /// <summary>The running totals of one study condition: when it was logged, and each player's metrics.</summary>
    internal sealed class ConditionStats
    {
        public int Ticks;
        public double FirstServerTime;
        public double LastServerTime;
        public readonly Dictionary<int, PlayerStats> Players = new();
    }

    /// <summary>
    /// The metrics state of one game session: the log file, the summary file, each study condition's running totals, and the recent
    /// position history of each cube. Made by <see cref="MetricLoggerManager.StartSession"/> and only used from that session's tick loop.
    /// </summary>
    public sealed class MetricSession : IDisposable
    {
        internal readonly Dictionary<string, List<PoseSample>> PoseHistories = new();

        // The SLAM drift each player was sent, oldest first. Written by the spatial sync thread, read by the tick loop, so guarded by DriftLock.
        internal readonly Dictionary<int, List<DriftSample>> DriftHistories = new();
        internal readonly object DriftLock = new();
        internal StreamWriter? Writer;

        // The condition the log is currently in (null before the first tick), and each condition's totals in the order the study runs them.
        internal StudyState? LoggedCondition;
        internal readonly SortedDictionary<StudyState, ConditionStats> Conditions = new();
        internal bool SummaryWriteFailed;
        internal bool TitleSet;
        private volatile IReadOnlyDictionary<int, Metrics>? _latest;

        /// <summary>Each player's metrics from the latest logged tick (null before the first one); safe to read from any thread.</summary>
        internal IReadOnlyDictionary<int, Metrics>? Latest
        {
            get => _latest;
            set => _latest = value;
        }

        /// <summary>The metrics of one player from the latest logged tick, or null if there are none for them yet.</summary>
        public Metrics? GetLatest(int playerID) => _latest != null && _latest.TryGetValue(playerID, out Metrics? m) ? m : null;

        /// <summary>The window this session prints its console output to (opened with the first line written to it).</summary>
        internal MetricConsoleWindow ConsoleWindow { get; } = new("Metrics");

        /// <summary>The study of the game this session measures; its condition says how much latency the game adds.</summary>
        public StudyStateManager Study { get; }

        internal MetricSession(StudyStateManager study)
        {
            Study = study;
        }

        /// <summary>The CSV this session writes every tick to; null until the first tick is logged.</summary>
        public string? FilePath { get; internal set; }

        /// <summary>The CSV with the average of every metric for each study condition; null until the first tick is logged.</summary>
        public string? SummaryFilePath { get; internal set; }

        public void Dispose()
        {
            Writer?.Dispose();
            Writer = null;
            ConsoleWindow.Dispose();
        }
    }

    /// <summary>
    /// Measures the study's metrics every game tick and writes them to a CSV file and a console window of the game's own, one file and window per game session.
    /// When the study moves to its next condition, a marker line (after an empty line) is written to the log, and a second file,
    /// <c>metrics_..._summary.csv</c>, holds each condition's averages of every metric. Those averages are per condition, never across
    /// conditions.
    /// Registered as a singleton so the connection handlers can feed it latency while the game's tick loop reads it.
    /// <para>
    /// <b>Latency per client</b> (<c>LatencyMs</c>) is the client's own estimate of its one-way latency to the server
    /// (<see cref="RecordEstimatedLatency"/>, from its ping measurements) plus the latency the study adds on top
    /// (<see cref="RecordEmulatedLatency"/>; zero outside the high latency conditions).
    /// </para>
    /// <para>
    /// <b>Inconsistency per client</b> comes in four forms, which all look at where each client saw each cube.
    /// Looking back <see cref="WindowSeconds"/> from now in steps of <see cref="SampleIntervalSeconds"/>, it works out where each client
    /// saw each cube at each step, by when each position change reached that client:
    /// <list type="bullet">
    /// <item>The client that moved the cube saw the new position when it moved it, which is when the move reached the server minus that
    /// client's latency.</item>
    /// <item>Every other client saw it when the server tick that broadcast it happened, plus that client's latency.</item>
    /// </list>
    /// Moves the server rejected count too, for the client that made them (see <see cref="CubePoseUpdate.Accepted"/>).
    /// <list type="bullet">
    /// <item><b>Pairwise State Inconsistency</b>: for each client, the distances between its view of a cube and every other
    /// client's view of the same cube are averaged over all cubes and steps (<c>PairwiseStateMeanDistanceM</c>, in meters) and turned into
    /// <c>PairwiseStateInconsistency = ln(1 + mean / <see cref="DistanceUnitMeters"/>)</c>: 0 when the views agree, growing slowly as they
    /// pull apart.</item>
    /// <item><b>Retroactive State Inconsistency</b>: for each client, the distance between its view of a cube and the server's
    /// authoritative state S (the position the server accepted by that step, counted from the moment the player made the move, which is what finally counts) is averaged over all cubes and
    /// steps (<c>RetroactiveStateMeanDistanceM</c>) and turned into the same log (<c>RetroactiveStateInconsistency</c>). Unlike the pairwise
    /// form it also exists for a single client, and it counts a client's own view that lost a conflict.</item>
    /// <item><b>Pairwise Spatial Inconsistency</b> and <b>Retroactive Spatial Inconsistency</b>: the same two comparisons, but each client's
    /// view is first moved by the SLAM drift that client had at that step. The server adds drift to the global pose it sends each client
    /// (<see cref="RecordDrift"/> keeps what it added, for <see cref="HistoryRetentionSeconds"/>), and a drifted client's headset puts
    /// the shared content in the wrong place in the real room. For each step, the drift in effect for a client is the latest one that had reached it by then (sent
    /// plus its latency; none at all before the first), and it moves the client's viewed position to where that position really is in the room. S is not moved: it is
    /// where the content truly is. With no drift these equal the state forms.</item>
    /// </list>
    /// </para>
    /// </summary>
    public class MetricLoggerManager
    {
        /// <summary>How far back from the current tick the spatial inconsistency looks (seconds).</summary>
        public const double WindowSeconds = 0.5;

        /// <summary>The spacing of the points in that window at which the cube positions are compared (seconds).</summary>
        public const double SampleIntervalSeconds = 0.02;

        /// <summary>
        /// The length that counts as one in the inconsistency's log (meters): 1 cm, so a 1 cm gap scores ln 2 and a 1 m gap scores
        /// about 4.6. Change it to change the scale.
        /// </summary>
        public const double DistanceUnitMeters = 0.01;

        /// <summary>
        /// How often the summary file is rewritten with the averages so far (ticks), so the current condition's averages are on disk even
        /// if the server stops without ending the session. It is also rewritten whenever the condition changes and when the session ends.
        /// </summary>
        public const int SummaryEveryNTicks = 30;

        /// <summary>How long position history is kept (seconds). It must exceed the window plus the largest latency.</summary>
        public const double HistoryRetentionSeconds = 5.0;

        /// <summary>A cube that moved less than this (meters) since its last applied position is taken as not moved by the server itself.</summary>
        private const double PositionChangeEpsilonMeters = 1e-6;

        /// <summary>How fast the emulated latency average follows new delays (0 to 1).</summary>
        public const double EmulatedLatencySmoothing = 0.2;

        /// <summary>Print a line to the console every this many ticks (1 is every tick, 0 turns the console output off).</summary>
        public int ConsoleEveryNTicks { get; set; } = 1;

        /// <summary>Where the metric files go. Defaults to a MetricLogs folder in the server's working directory.</summary>
        public string LogDirectory { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), "MetricLogs");

        // Per player: the client's own one-way latency estimate (seconds), as it last reported it.
        private readonly ConcurrentDictionary<int, double> _estimatedLatencySeconds = new();

        // Per player: a running average of the inbound delays the study drew for their messages (milliseconds).
        private readonly ConcurrentDictionary<int, double> _emulatedLatencyMs = new();

        #region Latency inputs

        /// <summary>
        /// A client reported its estimated one-way latency to the server (half its average ping round trip).
        /// </summary>
        public void RecordEstimatedLatency(int playerID, double oneWaySeconds)
        {
            if (double.IsNaN(oneWaySeconds) || double.IsInfinity(oneWaySeconds) || oneWaySeconds < 0)
                return;

            _estimatedLatencySeconds[playerID] = oneWaySeconds;
        }

        /// <summary>
        /// The study held back one of a player's messages by this long. Only delays above zero are recorded; the average is only used in
        /// the high latency conditions, see <see cref="GetEmulatedLatencyMs"/>. Player IDs are unique across games, so this is shared.
        /// </summary>
        public void RecordEmulatedLatency(int playerID, double delayMs)
        {
            if (delayMs <= 0)
                return;

            _emulatedLatencyMs.AddOrUpdate(
                playerID,
                delayMs,
                (_, previous) => previous + EmulatedLatencySmoothing * (delayMs - previous));
        }

        /// <summary>The player's last reported latency estimate (milliseconds), or 0 if they have not reported one yet.</summary>
        public double GetEstimatedLatencyMs(int playerID) =>
            _estimatedLatencySeconds.TryGetValue(playerID, out double seconds) ? seconds * 1000.0 : 0.0;

        /// <summary>
        /// The latency the study adds for the player (milliseconds): 0 outside the high latency conditions of their game's
        /// <paramref name="study"/>, otherwise the running average of the delays drawn for them (the condition's mean until a message
        /// has been delayed).
        /// </summary>
        public double GetEmulatedLatencyMs(int playerID, StudyStateManager study)
        {
            if (!study.IsHighLatency)
                return 0.0;

            return _emulatedLatencyMs.TryGetValue(playerID, out double ms) ? ms : StudyStateManager.LatencyMean;
        }

        #endregion Latency inputs

        #region Sessions

        /// <summary>
        /// Starts the metrics for one game session. Pass the result to <see cref="LogTick"/> on every tick.
        /// </summary>
        /// <param name="study">The study of the game being measured.</param>
        public MetricSession StartSession(StudyStateManager study) => new MetricSession(study);

        /// <summary>Ends a session: writes the final summary and closes the log file.</summary>
        public void EndSession(MetricSession session)
        {
            WriteSummary(session);
            session.Dispose();
        }

        #endregion Sessions

        #region Drift input

        /// <summary>
        /// The server added SLAM drift to a player's global pose and sent it at <paramref name="sendTime"/> (server time, seconds). The drift
        /// is how the drifted pose differs from the true one. Call it for every pose sent, also when there is no drift (the drift is then none, which
        /// is what ends the drift when a study condition without drift starts). Safe to call from any thread.
        /// </summary>
        public void RecordDrift(MetricSession session, int playerID, double sendTime, Pose truePose, Pose driftedPose)
        {
            var sample = new DriftSample(
                sendTime,
                truePose.Position,
                Multiply(driftedPose.Rotation, Quaternion.Inverse(truePose.Rotation)),
                driftedPose.Position - truePose.Position);

            lock (session.DriftLock)
            {
                if (!session.DriftHistories.TryGetValue(playerID, out List<DriftSample>? history))
                {
                    history = new List<DriftSample>();
                    session.DriftHistories[playerID] = history;
                }
                history.Add(sample);

                // Keep what is recent, plus the newest older one (it is still the drift in effect until the next arrives).
                int newestOld = history.FindLastIndex(s => s.SendTime < sendTime - HistoryRetentionSeconds);
                if (newestOld > 0)
                    history.RemoveRange(0, newestOld);
            }
        }

        /// <summary>The Hamilton product a * b: b first, then a.</summary>
        private static Quaternion Multiply(Quaternion a, Quaternion b) => new Quaternion(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        #endregion Drift input

        #region Per tick

        /// <summary>
        /// Measures this tick's metrics and writes them to the session's CSV (one row per player) and the console.
        /// Call once per game tick, after the tick's actions have been applied and before the state is broadcast.
        /// </summary>
        /// <param name="session">The game session's metrics state.</param>
        /// <param name="gameID">The ID of the game.</param>
        /// <param name="tick">The server tick number.</param>
        /// <param name="serverTime">The server time of this tick (seconds); this is what is broadcast with the state.</param>
        /// <param name="state">The authoritative game state.</param>
        /// <param name="poseUpdates">The cube moves handled during this tick (accepted or rejected), in the order they were applied.</param>
        public void LogTick(MetricSession session, int gameID, long tick, double serverTime, GameState state, IReadOnlyList<CubePoseUpdate> poseUpdates)
        {
            if (!session.TitleSet)
            {
                session.ConsoleWindow.SetTitle($"Metrics - game {gameID}");
                session.TitleSet = true;
            }

            UpdatePoseHistories(session, serverTime, state, poseUpdates);

            List<int> players = state.PlayerIDs.ToList();
            if (players.Count == 0)
                return;

            var estimatedMs = new Dictionary<int, double>();
            var emulatedMs = new Dictionary<int, double>();
            var latencySeconds = new Dictionary<int, double>();
            foreach (int playerID in players)
            {
                double estimated = GetEstimatedLatencyMs(playerID);
                double emulated = GetEmulatedLatencyMs(playerID, session.Study);
                estimatedMs[playerID] = estimated;
                emulatedMs[playerID] = emulated;
                latencySeconds[playerID] = (estimated + emulated) / 1000.0;
            }

            MeanDistances distances = ComputeMeanDistances(session, serverTime, state, players, latencySeconds);

            EnsureWriter(session);
            StudyState studyState = session.Study.CurrentState;
            string utcNow = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            // When the study moves to its next condition, mark it in the log so the rows of the two conditions are easy to tell apart.
            bool conditionChanged = session.LoggedCondition != studyState;
            if (conditionChanged)
            {
                WriteConditionMarker(session, session.LoggedCondition, studyState, utcNow, gameID, tick, serverTime);
                session.LoggedCondition = studyState;
            }

            // This condition's running totals, which the summary averages.
            if (!session.Conditions.TryGetValue(studyState, out ConditionStats? conditionStats))
            {
                conditionStats = new ConditionStats { FirstServerTime = serverTime };
                session.Conditions[studyState] = conditionStats;
            }
            conditionStats.Ticks++;
            conditionStats.LastServerTime = serverTime;

            var console = new StringBuilder();
            bool printToConsole = ConsoleEveryNTicks > 0 && tick % ConsoleEveryNTicks == 0;
            if (printToConsole)
                console.Append($"[Metrics] game {gameID} tick {tick} {studyState} round {state.Round}");

            var latestPlayers = new Dictionary<int, Metrics>();
            foreach (int playerID in players)
            {
                double latencyMs = latencySeconds[playerID] * 1000.0;
                double pairwiseState = distances.PairwiseState[playerID];
                double pairwiseStateInconsistency = ToInconsistency(pairwiseState);
                double retroactiveState = distances.RetroactiveState[playerID];
                double retroactiveStateInconsistency = ToInconsistency(retroactiveState);
                double pairwiseSpatial = distances.PairwiseSpatial[playerID];
                double pairwiseSpatialInconsistency = ToInconsistency(pairwiseSpatial);
                double retroactiveSpatial = distances.RetroactiveSpatial[playerID];
                double retroactiveSpatialInconsistency = ToInconsistency(retroactiveSpatial);

                latestPlayers[playerID] = new Metrics
                {
                    Tick = tick,
                    LatencyMs = (float)latencyMs,
                    EstimatedLatencyMs = (float)estimatedMs[playerID],
                    EmulatedLatencyMs = (float)emulatedMs[playerID],
                    PairwiseStateMeanDistanceCm = ToCentimeters(pairwiseState),
                    PairwiseStateInconsistency = ToFloat(pairwiseStateInconsistency),
                    RetroactiveStateMeanDistanceCm = ToCentimeters(retroactiveState),
                    RetroactiveStateInconsistency = ToFloat(retroactiveStateInconsistency),
                    PairwiseSpatialMeanDistanceCm = ToCentimeters(pairwiseSpatial),
                    PairwiseSpatialInconsistency = ToFloat(pairwiseSpatialInconsistency),
                    RetroactiveSpatialMeanDistanceCm = ToCentimeters(retroactiveSpatial),
                    RetroactiveSpatialInconsistency = ToFloat(retroactiveSpatialInconsistency),
                };

                session.Writer!.WriteLine(string.Join(",",
                    utcNow,
                    gameID.ToString(CultureInfo.InvariantCulture),
                    tick.ToString(CultureInfo.InvariantCulture),
                    Format(serverTime, "F3"),
                    studyState,
                    state.Round.ToString(CultureInfo.InvariantCulture),
                    playerID.ToString(CultureInfo.InvariantCulture),
                    Format(estimatedMs[playerID], "F2"),
                    Format(emulatedMs[playerID], "F2"),
                    Format(latencyMs, "F2"),
                    Format(pairwiseState, "F5"),
                    Format(pairwiseStateInconsistency, "F4"),
                    Format(retroactiveState, "F5"),
                    Format(retroactiveStateInconsistency, "F4"),
                    Format(pairwiseSpatial, "F5"),
                    Format(pairwiseSpatialInconsistency, "F4"),
                    Format(retroactiveSpatial, "F5"),
                    Format(retroactiveSpatialInconsistency, "F4")));

                if (!conditionStats.Players.TryGetValue(playerID, out PlayerStats? playerStats))
                {
                    playerStats = new PlayerStats();
                    conditionStats.Players[playerID] = playerStats;
                }
                playerStats.Ticks++;
                playerStats.SumEstimatedMs += estimatedMs[playerID];
                playerStats.SumEmulatedMs += emulatedMs[playerID];
                playerStats.SumLatencyMs += latencyMs;
                playerStats.PairwiseState.Add(pairwiseState, pairwiseStateInconsistency);
                playerStats.RetroactiveState.Add(retroactiveState, retroactiveStateInconsistency);
                playerStats.PairwiseSpatial.Add(pairwiseSpatial, pairwiseSpatialInconsistency);
                playerStats.RetroactiveSpatial.Add(retroactiveSpatial, retroactiveSpatialInconsistency);

                if (printToConsole)
                {
                    console.Append($" | P{playerID} latency {latencyMs:F0} ms (est {estimatedMs[playerID]:F0} + emu {emulatedMs[playerID]:F0})");
                    AppendConsoleMetric(console, "state pairwise", pairwiseStateInconsistency, pairwiseState);
                    AppendConsoleMetric(console, "state retroactive", retroactiveStateInconsistency, retroactiveState);
                    AppendConsoleMetric(console, "spatial pairwise", pairwiseSpatialInconsistency, pairwiseSpatial);
                    AppendConsoleMetric(console, "spatial retroactive", retroactiveSpatialInconsistency, retroactiveSpatial);
                }
            }

            session.Latest = latestPlayers;

            if (printToConsole)
                session.ConsoleWindow.WriteLine(console.ToString());

            if (conditionChanged || tick % SummaryEveryNTicks == 0)
                WriteSummary(session);
        }

        private static void AppendConsoleMetric(StringBuilder console, string name, double inconsistency, double distanceM) =>
            console.Append(double.IsNaN(inconsistency)
                ? $", {name} n/a"
                : string.Create(CultureInfo.InvariantCulture, $", {name} {inconsistency:F3} (mean {distanceM * 100.0:F1} cm)"));

        private static float ToCentimeters(double meters) => double.IsNaN(meters) ? -1f : (float)(meters * 100.0);

        private static float ToFloat(double value) => double.IsNaN(value) ? -1f : (float)value;

        /// <summary>
        /// Adds this tick's cube positions to the history, including the ones that never made it into the game:
        /// <list type="bullet">
        /// <item>every cube the history does not know yet (a new one), at its current position;</item>
        /// <item>each move the server accepted, which every player ends up seeing;</item>
        /// <item>each move the server rejected (it lost a conflict), which only the player who made it saw: their own client showed
        /// the cube moving, so the history has it for them alone;</item>
        /// <item>any change of a cube's position the server made itself (such as resetting a cube), seen by every player;</item>
        /// <item>for each player with a rejected move, the correction: the cube's real position, which reaches them with this tick's
        /// broadcast and puts their view back in line with the game.</item>
        /// </list>
        /// Forgets cubes that are gone and positions too old to matter.
        /// </summary>
        private static void UpdatePoseHistories(MetricSession session, double serverTime, GameState state, IReadOnlyList<CubePoseUpdate> poseUpdates)
        {
            List<CubeState> cubes = state.Cubes.Values.ToList();
            var cubeIDs = new HashSet<string>(cubes.Select(cube => cube.ID));

            foreach (string goneID in session.PoseHistories.Keys.Where(id => !cubeIDs.Contains(id)).ToList())
                session.PoseHistories.Remove(goneID);

            foreach (CubeState cube in cubes)
            {
                if (!session.PoseHistories.ContainsKey(cube.ID))
                {
                    session.PoseHistories[cube.ID] = new List<PoseSample>
                    {
                        new PoseSample(serverTime, serverTime, null, cube.Pose.Position),
                    };
                }
            }

            // The players whose move of a cube was rejected this tick; they get the cube's real position back.
            var corrected = new HashSet<(string CubeID, int PlayerID)>();

            foreach (CubePoseUpdate update in poseUpdates)
            {
                if (!session.PoseHistories.TryGetValue(update.CubeID, out List<PoseSample>? history))
                    continue;

                if (update.Accepted)
                {
                    history.Add(new PoseSample(serverTime, update.ArrivalTime, update.PlayerID, update.Position));
                }
                else
                {
                    // Seen only by the player who made the move, at the moment they made it (as for an accepted move).
                    history.Add(new PoseSample(serverTime, update.ArrivalTime, update.PlayerID, update.Position, OnlyForPlayerID: update.PlayerID));
                    corrected.Add((update.CubeID, update.PlayerID));
                }
            }

            // A position the game changed by itself (not through a player's move) reaches every player with this tick's broadcast.
            foreach (CubeState cube in cubes)
            {
                List<PoseSample> history = session.PoseHistories[cube.ID];
                int lastApplied = history.FindLastIndex(sample => sample.OnlyForPlayerID == null);
                if (lastApplied >= 0 && Distance(history[lastApplied].Position, cube.Pose.Position) > PositionChangeEpsilonMeters)
                    history.Add(new PoseSample(serverTime, serverTime, null, cube.Pose.Position));
            }

            // The correction of each rejected move: the cube's real position, which the player gets with this tick's broadcast.
            foreach ((string cubeID, int playerID) in corrected)
            {
                if (state.Cubes.TryGetValue(cubeID, out CubeState? cube))
                    session.PoseHistories[cubeID].Add(new PoseSample(serverTime, serverTime, null, cube.Pose.Position, OnlyForPlayerID: playerID));
            }

            // Keep what is recent, plus the newest older position for each viewer (everyone, and each player with positions only they
            // saw): a cube that has not moved for a while is still where it was.
            double cutoff = serverTime - HistoryRetentionSeconds;
            foreach (List<PoseSample> history in session.PoseHistories.Values)
            {
                var viewersSeen = new HashSet<int?>();
                for (int i = history.Count - 1; i >= 0; i--)
                {
                    if (history[i].TickTime >= cutoff)
                        continue;

                    // The newest old position of each viewer stays; older ones of the same viewer go.
                    if (!viewersSeen.Add(history[i].OnlyForPlayerID))
                        history.RemoveAt(i);
                }
            }
        }

        /// <summary>The sums and counts of distances for each player, for averaging.</summary>
        private sealed class Accumulator
        {
            private readonly double[] _sums;
            private readonly int[] _counts;

            public Accumulator(int players)
            {
                _sums = new double[players];
                _counts = new int[players];
            }

            public void Add(int player, double distance)
            {
                _sums[player] += distance;
                _counts[player]++;
            }

            public Dictionary<int, double> Means(List<int> players)
            {
                var means = new Dictionary<int, double>();
                for (int p = 0; p < players.Count; p++)
                    means[players[p]] = _counts[p] > 0 ? _sums[p] / _counts[p] : double.NaN;
                return means;
            }
        }

        /// <summary>
        /// For each player, over the look-back window and all cubes, four average distances (meters), NaN where there was nothing to compare:
        /// <list type="bullet">
        /// <item><b>Pairwise state</b>: between where they saw the cubes and where each other player saw the same cubes.</item>
        /// <item><b>Retroactive state</b>: between where they saw the cubes and the server's authoritative state at the same moment.</item>
        /// <item><b>Pairwise spatial</b> and <b>retroactive spatial</b>: the same, after each player's view has been moved by the SLAM drift
        /// they had at that moment (see <see cref="ApplyDrift"/>). The authoritative state is not moved.</item>
        /// </list>
        /// </summary>
        private static MeanDistances ComputeMeanDistances(
            MetricSession session, double now, GameState state, List<int> players, Dictionary<int, double> latencySeconds)
        {
            int sampleCount = (int)Math.Round(WindowSeconds / SampleIntervalSeconds) + 1;
            var pairwiseState = new Accumulator(players.Count);
            var retroactiveState = new Accumulator(players.Count);
            var pairwiseSpatial = new Accumulator(players.Count);
            var retroactiveSpatial = new Accumulator(players.Count);
            var views = new Vector3?[players.Count];
            var spatialViews = new Vector3?[players.Count];
            DriftSample[][] drifts = SnapshotDrifts(session, players);

            foreach (CubeState cube in state.Cubes.Values.ToList())
            {
                if (!session.PoseHistories.TryGetValue(cube.ID, out List<PoseSample>? history) || history.Count == 0)
                    continue;

                for (int step = 0; step < sampleCount; step++)
                {
                    double time = now - WindowSeconds + step * SampleIntervalSeconds;

                    for (int p = 0; p < players.Count; p++)
                    {
                        views[p] = ViewedPosition(history, players[p], latencySeconds[players[p]], time);
                        spatialViews[p] = views[p] is Vector3 viewed ? ApplyDrift(drifts[p], latencySeconds[players[p]], time, viewed) : null;
                    }

                    // Retroactive: each player's view against the server's authoritative state S at this moment.
                    Vector3? authoritative = AuthoritativePosition(history, time, latencySeconds);
                    if (authoritative != null)
                    {
                        for (int p = 0; p < players.Count; p++)
                        {
                            if (views[p] == null)
                                continue;

                            retroactiveState.Add(p, Distance(views[p]!.Value, authoritative.Value));
                            retroactiveSpatial.Add(p, Distance(spatialViews[p]!.Value, authoritative.Value));
                        }
                    }

                    // Pairwise: each player's view against every other player's view.
                    for (int a = 0; a < players.Count; a++)
                    {
                        if (views[a] == null)
                            continue;

                        for (int b = a + 1; b < players.Count; b++)
                        {
                            if (views[b] == null)
                                continue;

                            double stateDistance = Distance(views[a]!.Value, views[b]!.Value);
                            pairwiseState.Add(a, stateDistance);
                            pairwiseState.Add(b, stateDistance);

                            double spatialDistance = Distance(spatialViews[a]!.Value, spatialViews[b]!.Value);
                            pairwiseSpatial.Add(a, spatialDistance);
                            pairwiseSpatial.Add(b, spatialDistance);
                        }
                    }
                }
            }

            return new MeanDistances(
                pairwiseState.Means(players),
                retroactiveState.Means(players),
                pairwiseSpatial.Means(players),
                retroactiveSpatial.Means(players));
        }

        /// <summary>A copy of each player's drift history (indexed like <paramref name="players"/>), so the tick can read it without holding the lock.</summary>
        private static DriftSample[][] SnapshotDrifts(MetricSession session, List<int> players)
        {
            var snapshot = new DriftSample[players.Count][];
            lock (session.DriftLock)
            {
                for (int p = 0; p < players.Count; p++)
                {
                    snapshot[p] = session.DriftHistories.TryGetValue(players[p], out List<DriftSample>? history)
                        ? history.ToArray()
                        : Array.Empty<DriftSample>();
                }
            }

            return snapshot;
        }

        /// <summary>
        /// Where a position the player saw is really, in the room, once the player's SLAM drift is taken into account. The drift in effect at
        /// <paramref name="time"/> is the latest one that had reached the player by then (when it was sent plus their latency), or none if
        /// none had. A drifted player's headset lines the shared content up with a wrong idea of where it is in the global frame, which
        /// moves a point <c>g</c> to <c>G + inverse(Q) * (g - G - T)</c>, where G is the player's true position when the pose was sent, Q the
        /// rotation and T the shift the drift added to that pose.
        /// </summary>
        private static Vector3 ApplyDrift(DriftSample[] samples, double latencySeconds, double time, Vector3 position)
        {
            bool found = false;
            DriftSample inEffect = default;
            double latestSent = double.NegativeInfinity;

            foreach (DriftSample sample in samples)
            {
                if (sample.SendTime + latencySeconds <= time && sample.SendTime >= latestSent)
                {
                    latestSent = sample.SendTime;
                    inEffect = sample;
                    found = true;
                }
            }

            if (!found)
                return position;

            Vector3 offset = position - inEffect.TruePosition - inEffect.Translation;
            return inEffect.TruePosition + Quaternion.Inverse(inEffect.Rotation) * offset;
        }

        /// <summary>
        /// Where the player saw the cube at <paramref name="time"/>: the position that reached them most recently by then, or null if
        /// none had yet (the cube was new). A position reached the player who moved it at its arrival at the server minus their latency,
        /// and everyone else at the broadcasting tick's time plus their latency. This holds for a move the server rejected too, but
        /// then only for the player who made it. Of positions that reached a player at the same time, the later one in the history wins.
        /// </summary>
        private static Vector3? ViewedPosition(List<PoseSample> history, int playerID, double latencySeconds, double time)
        {
            Vector3? position = null;
            double latestSeen = double.NegativeInfinity;

            foreach (PoseSample sample in history)
            {
                // A position only some other player saw (their rejected move, or its correction) never reached this player.
                if (sample.OnlyForPlayerID != null && sample.OnlyForPlayerID != playerID)
                    continue;

                double seenAt = sample.OriginatorPlayerID == playerID
                    ? sample.ArrivalTime - latencySeconds
                    : sample.TickTime + latencySeconds;

                if (seenAt <= time && seenAt >= latestSeen)
                {
                    latestSeen = seenAt;
                    position = sample.Position;
                }
            }

            return position;
        }

        /// <summary>
        /// The server's authoritative position of the cube at <paramref name="time"/>, in the time the moves actually happened: the latest
        /// position the game accepted whose move had been made by then, or null if the cube did not exist yet. A move the server accepted
        /// counts from the moment its player made it, which is when it reached the server minus that player's latency (the moment the
        /// player's own view shows it, and so the same moment the pairwise metric uses for them), not from the tick that applied it.
        /// A position the server set itself counts from its tick. A rejected move never counts. Of positions made at the same moment,
        /// the one applied later wins.
        /// </summary>
        private static Vector3? AuthoritativePosition(List<PoseSample> history, double time, Dictionary<int, double> latencySeconds)
        {
            Vector3? position = null;
            double latest = double.NegativeInfinity;

            foreach (PoseSample sample in history)
            {
                if (sample.OnlyForPlayerID != null)
                    continue;

                double madeAt = sample.OriginatorPlayerID is int originator && latencySeconds.TryGetValue(originator, out double latency)
                    ? sample.ArrivalTime - latency
                    : sample.TickTime;

                if (madeAt <= time && madeAt >= latest)
                {
                    latest = madeAt;
                    position = sample.Position;
                }
            }

            return position;
        }

        /// <summary>ln(1 + distance / <see cref="DistanceUnitMeters"/>); NaN stays NaN.</summary>
        private static double ToInconsistency(double distanceMeters) =>
            double.IsNaN(distanceMeters) ? double.NaN : Math.Log(1.0 + distanceMeters / DistanceUnitMeters);

        private static double Distance(Vector3 a, Vector3 b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            double dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        #endregion Per tick

        #region Files

        private void EnsureWriter(MetricSession session)
        {
            if (session.Writer != null)
                return;

            Directory.CreateDirectory(LogDirectory);
            string baseName = Path.Combine(LogDirectory, $"metrics_{DateTime.Now:yyyyMMdd_HHmmss_fff}");
            session.FilePath = baseName + ".csv";
            session.SummaryFilePath = baseName + "_summary.csv";

            // Lines end in \n (not the platform's newline), so the blank line before a condition marker is exactly one \n.
            session.Writer = new StreamWriter(session.FilePath, append: false, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            session.Writer.WriteLine("UtcTime,GameID,Tick,ServerTime,StudyState,Round,PlayerID,EstimatedLatencyMs,EmulatedLatencyMs,LatencyMs,PairwiseStateMeanDistanceM,PairwiseStateInconsistency,RetroactiveStateMeanDistanceM,RetroactiveStateInconsistency,PairwiseSpatialMeanDistanceM,PairwiseSpatialInconsistency,RetroactiveSpatialMeanDistanceM,RetroactiveSpatialInconsistency");

            session.ConsoleWindow.WriteLine($"[Metrics] Logging to {session.FilePath}");
            session.ConsoleWindow.WriteLine($"[Metrics] Averages per study condition go to {session.SummaryFilePath}");
        }

        /// <summary>
        /// Writes a line to the log saying which study condition the rows after it belong to, and prints it to the console. Every
        /// condition change after the first is preceded by an empty line (a "\n"), so it stands out. The line starts with "#" so
        /// tools that read the file as CSV can skip it (for example pandas' <c>comment='#'</c>); empty lines are skipped by default.
        /// </summary>
        private static void WriteConditionMarker(MetricSession session, StudyState? previous, StudyState current, string utcNow, int gameID, long tick, double serverTime)
        {
            string details = $"UTC {utcNow} | game {gameID} | tick {tick} | server time {Format(serverTime, "F3")}";
            string marker = previous == null
                ? $"# STUDY CONDITION: {current} (start) | {details}"
                : $"# STUDY CONDITION CHANGE: {previous} -> {current} | {details}";

            if (previous != null)
                session.Writer!.Write("\n");
            session.Writer!.WriteLine(marker);

            session.ConsoleWindow.WriteLine($"\n[Metrics] {marker.TrimStart('#', ' ')}");
        }

        /// <summary>
        /// Rewrites the summary file: for each study condition, each player's average of every metric over all the ticks logged in that
        /// condition (not across conditions), plus an All row that averages over the players. Conditions are listed in the order the study
        /// runs them. A condition still running has the averages of its ticks so far.
        /// </summary>
        private static void WriteSummary(MetricSession session)
        {
            if (session.SummaryFilePath == null || session.Conditions.Count == 0)
                return;

            var csv = new StringBuilder();
            csv.Append("StudyCondition,PlayerID,Ticks,DurationSeconds,MeanEstimatedLatencyMs,MeanEmulatedLatencyMs,MeanLatencyMs,MeanPairwiseStateDistanceM,MeanPairwiseStateInconsistency,TicksWithPairwiseState,MeanRetroactiveStateDistanceM,MeanRetroactiveStateInconsistency,TicksWithRetroactiveState,MeanPairwiseSpatialDistanceM,MeanPairwiseSpatialInconsistency,TicksWithPairwiseSpatial,MeanRetroactiveSpatialDistanceM,MeanRetroactiveSpatialInconsistency,TicksWithRetroactiveSpatial\n");

            foreach (KeyValuePair<StudyState, ConditionStats> condition in session.Conditions)
            {
                ConditionStats stats = condition.Value;
                double duration = stats.LastServerTime - stats.FirstServerTime;
                var all = new PlayerStats();

                foreach (KeyValuePair<int, PlayerStats> player in stats.Players.OrderBy(p => p.Key))
                {
                    AppendSummaryRow(csv, condition.Key, player.Key.ToString(CultureInfo.InvariantCulture), stats.Ticks, duration, player.Value);

                    all.Ticks += player.Value.Ticks;
                    all.SumEstimatedMs += player.Value.SumEstimatedMs;
                    all.SumEmulatedMs += player.Value.SumEmulatedMs;
                    all.SumLatencyMs += player.Value.SumLatencyMs;
                    all.PairwiseState.Add(player.Value.PairwiseState);
                    all.RetroactiveState.Add(player.Value.RetroactiveState);
                    all.PairwiseSpatial.Add(player.Value.PairwiseSpatial);
                    all.RetroactiveSpatial.Add(player.Value.RetroactiveSpatial);
                }

                AppendSummaryRow(csv, condition.Key, "All", stats.Ticks, duration, all);
            }

            try
            {
                File.WriteAllText(session.SummaryFilePath, csv.ToString(), new UTF8Encoding(false));
                session.SummaryWriteFailed = false;
            }
            catch (IOException ex)
            {
                // Most likely the file is open in a program that locks it (such as Excel); the next rewrite will try again.
                if (!session.SummaryWriteFailed)
                    session.ConsoleWindow.WriteError($"[Metrics] Could not write {session.SummaryFilePath}: {ex.Message}");
                session.SummaryWriteFailed = true;
            }
        }

        /// <summary>One row of the summary: the averages of a player (or of all players) over a condition's ticks.</summary>
        private static void AppendSummaryRow(StringBuilder csv, StudyState condition, string player, int conditionTicks, double durationSeconds, PlayerStats stats)
        {
            csv.Append(string.Join(",",
                condition,
                player,
                conditionTicks.ToString(CultureInfo.InvariantCulture),
                Format(durationSeconds, "F1"),
                Mean(stats.SumEstimatedMs, stats.Ticks, "F2"),
                Mean(stats.SumEmulatedMs, stats.Ticks, "F2"),
                Mean(stats.SumLatencyMs, stats.Ticks, "F2"),
                Mean(stats.PairwiseState.SumDistanceM, stats.PairwiseState.ValidTicks, "F5"),
                Mean(stats.PairwiseState.SumInconsistency, stats.PairwiseState.ValidTicks, "F4"),
                stats.PairwiseState.ValidTicks.ToString(CultureInfo.InvariantCulture),
                Mean(stats.RetroactiveState.SumDistanceM, stats.RetroactiveState.ValidTicks, "F5"),
                Mean(stats.RetroactiveState.SumInconsistency, stats.RetroactiveState.ValidTicks, "F4"),
                stats.RetroactiveState.ValidTicks.ToString(CultureInfo.InvariantCulture),
                Mean(stats.PairwiseSpatial.SumDistanceM, stats.PairwiseSpatial.ValidTicks, "F5"),
                Mean(stats.PairwiseSpatial.SumInconsistency, stats.PairwiseSpatial.ValidTicks, "F4"),
                stats.PairwiseSpatial.ValidTicks.ToString(CultureInfo.InvariantCulture),
                Mean(stats.RetroactiveSpatial.SumDistanceM, stats.RetroactiveSpatial.ValidTicks, "F5"),
                Mean(stats.RetroactiveSpatial.SumInconsistency, stats.RetroactiveSpatial.ValidTicks, "F4"),
                stats.RetroactiveSpatial.ValidTicks.ToString(CultureInfo.InvariantCulture)));
            csv.Append('\n');
        }

        /// <summary>An average for the CSV; an empty cell if there was nothing to average.</summary>
        private static string Mean(double sum, int count, string format) =>
            count > 0 ? Format(sum / count, format) : string.Empty;

        /// <summary>A number for the CSV; an empty cell for NaN.</summary>
        private static string Format(double value, string format) =>
            double.IsNaN(value) ? string.Empty : value.ToString(format, CultureInfo.InvariantCulture);

        #endregion Files
    }
}
