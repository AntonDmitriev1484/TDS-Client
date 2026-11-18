using System;

namespace SharedLibrary.Game
{
    /// <summary>
    /// One player's study metrics as the server measured them on a tick: the latency between the player and the server, and four
    /// inconsistencies of the player's view of the cubes. Two are "state" inconsistencies, which compare what each player's game state
    /// showed (against the other players' views, and against the server's authoritative state); the other two are "spatial"
    /// inconsistencies, which compare the same views after the SLAM drift added to each player's alignment has moved them. Sent to each player inside the
    /// <c>GameStateDTO</c> so the client can show them. A value of -1 means "not available" (no distance could be measured that tick).
    /// </summary>
    [Serializable]
    public class Metrics
    {
        /// <summary>The server tick these were measured on.</summary>
        public long Tick;

        // Latency
        /// <summary>Estimated + emulated one-way latency between this player and the server (milliseconds).</summary>
        public float LatencyMs;
        /// <summary>The part of <see cref="LatencyMs"/> the client measured itself (milliseconds).</summary>
        public float EstimatedLatencyMs;
        /// <summary>The part of <see cref="LatencyMs"/> the study added (milliseconds).</summary>
        public float EmulatedLatencyMs;

        // Pairwise State Inconsistency
        /// <summary>Mean distance between this player's view of the cubes and each other player's view (centimeters), or -1.</summary>
        public float PairwiseStateMeanDistanceCm = -1f;
        /// <summary>Pairwise State Inconsistency: ln(1 + mean distance / 1 cm), or -1.</summary>
        public float PairwiseStateInconsistency = -1f;

        // Retroactive State Inconsistency
        /// <summary>Mean distance between this player's view of the cubes and the server's authoritative state (centimeters), or -1.</summary>
        public float RetroactiveStateMeanDistanceCm = -1f;
        /// <summary>Retroactive State Inconsistency: ln(1 + mean distance / 1 cm), or -1.</summary>
        public float RetroactiveStateInconsistency = -1f;

        // Pairwise Spatial Inconsistency (the views are moved by each player's SLAM drift first)
        /// <summary>Mean distance between where this player saw the cubes and where each other player saw them, after the SLAM drift (centimeters), or -1.</summary>
        public float PairwiseSpatialMeanDistanceCm = -1f;
        /// <summary>Pairwise Spatial Inconsistency: ln(1 + mean distance / 1 cm), or -1.</summary>
        public float PairwiseSpatialInconsistency = -1f;

        // Retroactive Spatial Inconsistency (the player's view is moved by their SLAM drift first)
        /// <summary>Mean distance between where this player saw the cubes, after the SLAM drift, and the server's authoritative state (centimeters), or -1.</summary>
        public float RetroactiveSpatialMeanDistanceCm = -1f;
        /// <summary>Retroactive Spatial Inconsistency: ln(1 + mean distance / 1 cm), or -1.</summary>
        public float RetroactiveSpatialInconsistency = -1f;

        public Metrics() { }

        public Metrics GetDeepCopy() => (Metrics)MemberwiseClone();
    }
}
