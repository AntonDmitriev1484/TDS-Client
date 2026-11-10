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
    /// Registered as a singleton, so the game and the connection handlers see the same state. Safe to use from any thread.
    /// </summary>
    public class StudyStateManager
    {
        /// <summary>Drift rotation in the high drift conditions (degrees).</summary>
        public const float DriftRot = 20f;

        /// <summary>Drift translation in the high drift conditions (meters).</summary>
        public const float DriftTrans = 0.2f;

        /// <summary>Mean of the added inbound latency in the high latency conditions (milliseconds).</summary>
        public const double LatencyMean = 250;

        /// <summary>Standard deviation of the added inbound latency in the high latency conditions (milliseconds).</summary>
        public const double LatencyStd = 50;

        private readonly object _lock = new();
        private readonly Random _random = new();
        private StudyState _currentState = StudyState.No_Latency_No_Drift;
        private bool _isComplete;

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
    }
}
