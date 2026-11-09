using System.Numerics;

namespace Server.Utilities
{
    /// <summary>One scene anchor as the viewer draws it, in the frame of the sub-map it is in.</summary>
    /// <param name="Kind">"Wall" (vertical surface), "Floor", "Ceiling" or "Volume".</param>
    /// <param name="Matched">True if the anchor was matched with an anchor of another player (or is the reference player's own), false if it only this player has so far.</param>
    public sealed record SceneViewAnchor(int PlayerID, string Label, string Kind, Vector3 Position, Quaternion Rotation, Vector3 Size, bool Matched);

    /// <summary>A headset as the viewer draws it: where it was in its last report and which way it was looking.</summary>
    public sealed record SceneViewPlayer(int PlayerID, Vector3 Position, Vector3 Forward);

    /// <summary>
    /// What one viewer window shows. A player whose scan has not been merged yet is a sub-map of their own, in their own tracking space; the players
    /// that are placed in the global map are drawn together in one sub-map in the global frame, each with their own anchors, so how well their scans
    /// agree can be seen.
    /// </summary>
    public sealed record SceneSubMap(string Key, string Title, bool IsGlobal, int[] PlayerIDs, SceneViewAnchor[] Anchors, SceneViewPlayer[] Players, string Note);

    public sealed record SceneMapSnapshot(long Version, SceneSubMap[] SubMaps);
}
