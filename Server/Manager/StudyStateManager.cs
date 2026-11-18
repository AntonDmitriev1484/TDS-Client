using SharedLibrary.Game;
using SharedLibrary.Game.Actions;
using System.Diagnostics;

namespace Server.Manager
{
    /// <summary>The study conditions, in the order they are run.</summary>
    public enum StudyState
    {
        No_Latency_No_Drift,
        Hi_Latency_No_Drift,
        No_Latency_Hi_Drift,
        Hi_Latency_Hi_Drift,
    }

    /// <summary>
    /// The user study's state machine: which condition is running, and the settings the conditions use. It starts in the first
    /// <see cref="StudyState"/> and moves to the next each time a player wins a game (<see cref="OnGameWon"/>), stopping at the last.
    /// Each game (<c>ServerGameStateManager.Study</c>) owns its own, so every game runs through the conditions independently. The
    /// connection handlers reach a player's through the game the player is in. Safe to use from any thread.
    /// </summary>
    public class StudyStateManager
    {
        /// <summary>
        /// Old step-drift settings. <c>ServerGameStateManager.SetDriftError</c> still sends these to the clients, which snap their
        /// SharedScene by them. They are 0 so that mechanism does nothing and does not stack on the ramped drift of
        /// <see cref="AddDriftPoseError"/>, which uses <see cref="MaxDriftYawDegrees"/> and <see cref="MaxDriftTranslationMeters"/>.
        /// </summary>
        public const float DriftRot = 0f;
        public const float DriftTrans = 0f;

        /// <summary>Size of the headset's yaw error once the drift has fully ramped up (degrees).</summary>
        public const float MaxDriftYawDegrees = 20f;

        /// <summary>Size of the headset's position error once the drift has fully ramped up (meters, horizontal).</summary>
        public const float MaxDriftTranslationMeters = 0.2f;

        /// <summary>
        /// How long, from the start of a high drift condition, the drift takes to grow from nothing to its full size (seconds).
        /// It grows linearly, then holds. If a game ends before this, players never see the full drift.
        /// </summary>
        public const double DriftRampSeconds = 60;

        /// <summary>Mean of the added inbound latency in the high latency conditions (milliseconds).</summary>
        public const double LatencyMean = 250;

        /// <summary>Standard deviation of the added inbound latency in the high latency conditions (milliseconds).</summary>
        public const double LatencyStd = 50;

        private readonly object _lock = new();
        private readonly Random _random = new();
        private StudyState _currentState = StudyState.No_Latency_No_Drift;
        private bool _isComplete;

        // When the current condition began (Stopwatch timestamp). The drift ramps up from here.
        private long _conditionStartTimestamp = Stopwatch.GetTimestamp();

        // Each player's drift direction for the current condition, picked the first time that player is sent a pose: which way the
        // yaw turns (+1 or -1) and the heading of the position error (radians about the vertical axis). Cleared on every condition change.
        private readonly Dictionary<int, (float YawSign, float Heading)> _driftDirections = new();

        /// <summary>The condition currently running.</summary>
        public StudyState CurrentState { get { lock (_lock) return _currentState; } }

        /// <summary>True once a player has won the game in the last condition.</summary>
        public bool IsComplete { get { lock (_lock) return _isComplete; } }

        /// <summary>True in the conditions that add latency to inbound game messages.</summary>
        public bool IsHighLatency
        {
            get
            {
                StudyState state = CurrentState;
                return state == StudyState.Hi_Latency_No_Drift || state == StudyState.Hi_Latency_Hi_Drift;
            }
        }

        /// <summary>True in the conditions that add drift.</summary>
        public bool IsHighDrift
        {
            get
            {
                StudyState state = CurrentState;
                return state == StudyState.No_Latency_Hi_Drift || state == StudyState.Hi_Latency_Hi_Drift;
            }
        }

        /// <summary>
        /// A player won the game: advance to the next condition. Returns false (and stays put) if the last condition was already running,
        /// in which case the study is complete.
        /// </summary>
        public bool OnGameWon(int winnerPlayerID)
        {
            lock (_lock)
            {
                if (_isComplete)
                    return false;

                StudyState previous = _currentState;
                if (previous == StudyState.Hi_Latency_Hi_Drift)
                {
                    _isComplete = true;
                    Console.WriteLine($"[Study] Player {winnerPlayerID} won {previous}; that was the last condition, so the study is complete.");
                    return false;
                }

                _currentState = previous + 1;

                // Each condition starts with no drift, which then ramps up again in a newly drawn direction.
                _conditionStartTimestamp = Stopwatch.GetTimestamp();
                _driftDirections.Clear();

                Console.WriteLine($"[Study] Player {winnerPlayerID} won {previous}; advancing to {_currentState}.");
                return true;
            }
        }

        /// <summary>
        /// How long to hold back an inbound game message (milliseconds): a draw from a Gaussian with mean <see cref="LatencyMean"/> and
        /// standard deviation <see cref="LatencyStd"/> (never negative) in the high latency conditions, otherwise 0.
        /// </summary>
        public double DrawInboundLatencyMs()
        {
            if (!IsHighLatency)
                return 0;

            double sample;
            lock (_lock)
            {
                // Box-Muller: two uniform samples give one standard normal sample.
                double u1 = 1.0 - _random.NextDouble(); // (0, 1], so the log is finite
                double u2 = _random.NextDouble();
                sample = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            }

            return Math.Max(0, LatencyMean + LatencyStd * sample);
        }

        /// <summary>
        /// Adds the headset's tracking drift to the global pose that is about to be sent to a player. Outside the high drift conditions
        /// the pose is returned unchanged. In them, the error grows linearly from nothing at the start of the condition to
        /// <see cref="MaxDriftYawDegrees"/> of yaw and <see cref="MaxDriftTranslationMeters"/> of horizontal position over
        /// <see cref="DriftRampSeconds"/>, then holds. Called on every sync response (about once a second per player), the pose
        /// changes a little each time, and the client blends each correction, so the scene creeps rather than jumps.
        /// <para>
        /// The result is what the player's headset would believe about its own place in the global frame if its tracking had drifted:
        /// the position is off by the error and the heading is turned by it. The client then lines its SharedScene up to that wrong
        /// pose, so the shared content turns about the player's head and slides away from where it really is. The true alignment is
        /// recomputed from the room's anchors on every key frame and the error is only added to the reply, so it is never corrected.
        /// </para>
        /// <para>
        /// The turn is about the vertical axis only, since headset tracking spaces are gravity aligned and do not tilt.
        /// </para>
        /// </summary>
        /// <param name="truePose">The pose in the global frame from the alignment.</param>
        /// <param name="playerID">
        /// The player the pose is for. Each player gets their own drift direction, as separate headsets drift separately. Callers that
        /// leave this out all share one drift.
        /// </param>
        public Pose AddDriftPoseError(Pose truePose, int playerID = 0)
        {
            lock (_lock)
            {
                bool highDrift = _currentState == StudyState.No_Latency_Hi_Drift || _currentState == StudyState.Hi_Latency_Hi_Drift;
                if (!highDrift)
                    return truePose;

                double elapsedSeconds = Stopwatch.GetElapsedTime(_conditionStartTimestamp).TotalSeconds;
                double progress = Math.Clamp(elapsedSeconds / DriftRampSeconds, 0.0, 1.0);

                if (!_driftDirections.TryGetValue(playerID, out (float YawSign, float Heading) direction))
                {
                    direction = (
                        _random.NextDouble() < 0.5 ? -1f : 1f,
                        (float)(_random.NextDouble() * 2.0 * Math.PI));
                    _driftDirections[playerID] = direction;

                    Console.WriteLine(
                        $"[Study] {_currentState}: player {playerID}'s drift will build to {direction.YawSign * MaxDriftYawDegrees:+0.#;-0.#} deg " +
                        $"and {MaxDriftTranslationMeters:0.00} m at heading {direction.Heading * 180.0 / Math.PI:0} deg over {DriftRampSeconds:0.#} s.");
                }

                float yawDegrees = (float)(direction.YawSign * MaxDriftYawDegrees * progress);
                float translationMeters = (float)(MaxDriftTranslationMeters * progress);

                var translation = new Vector3(
                    (float)Math.Cos(direction.Heading) * translationMeters,
                    0f,
                    (float)Math.Sin(direction.Heading) * translationMeters);

                return new Pose(
                    truePose.Position + translation,
                    Multiply(YawRotation(yawDegrees), truePose.Rotation));
            }
        }

        /// <summary>A rotation of <paramref name="degrees"/> about the vertical (Y) axis.</summary>
        static Quaternion YawRotation(float degrees)
        {
            double half = degrees * Math.PI / 360.0;
            return new Quaternion(0f, (float)Math.Sin(half), 0f, (float)Math.Cos(half));
        }

        /// <summary>The Hamilton product a * b: b first, then a.</summary>
        static Quaternion Multiply(Quaternion a, Quaternion b) => new Quaternion(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
    }
}
