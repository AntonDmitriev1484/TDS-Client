using SharedLibrary.Game;
using SharedLibrary.Game.Actions;
using System;
using System.Collections.Generic;

namespace SharedLibrary.Communication.DataTransferObjects.SpatialSync
{
    /// <summary>
    /// One scene anchor of a headset's room scan (Meta Space Setup / Scene API): a wall, the floor, the ceiling, a door, a window, a table, a couch...
    /// as that headset sees it, in that headset's own tracking space. Nothing in it is shared between headsets: each headset scanned the room itself
    /// and has its own anchors with its own UUIDs. The server tells which anchors are the same thing by <see cref="Label"/> and <see cref="Size"/>,
    /// and lines the headsets up by how those anchors sit.
    /// </summary>
    public class SceneAnchorDTO
    {
        /// <summary>This headset's own id for the anchor (not the same on other headsets). Only used to tell the anchors of one headset apart over time.</summary>
        public string AnchorUuid { get; set; }

        /// <summary>The semantic label of the anchor, as text: WALL_FACE, FLOOR, CEILING, DOOR_FRAME, WINDOW_FRAME, TABLE, COUCH, ...</summary>
        public string Label { get; set; }

        /// <summary>
        /// The pose of the middle of the anchor's bounds in this headset's tracking space (the same space as <see cref="KeyFrameAnchorDTO.CameraLocalPose"/>).
        /// For a flat anchor (wall, door, window, floor, ...) its forward axis (z) is the surface normal.
        /// </summary>
        public Pose Pose { get; set; }

        /// <summary>The extents of the anchor's bounds along its own axes, in metres (x width, y height, z depth). The depth of a flat anchor is 0.</summary>
        public Vector3 Size { get; set; }

        /// <summary>Whether the anchor is a current, usable one. The pose of one that is not is ignored by the server.</summary>
        public bool IsTracked { get; set; } = true;

        /// <summary>Parameterless constructor for JSON deserialization between server and client.</summary>
        public SceneAnchorDTO() { }

        public SceneAnchorDTO(string anchorUuid, string label, Pose pose, Vector3 size, bool isTracked = true)
        {
            AnchorUuid = anchorUuid;
            Label = label;
            Pose = pose;
            Size = size;
            IsTracked = isTracked;
        }
    }

    /// <summary>
    /// What a headset sends to be synchronized with the other headsets: its pose and the scene anchors of its room scan, all in its own tracking
    /// space, at one moment. The server matches the scene anchors of the headsets with each other (by label and size), works out where each headset's
    /// tracking space is in the global frame, and answers with a <see cref="SyncResponseDTO"/> whose global pose is <see cref="CameraLocalPose"/> in
    /// the global frame.
    /// </summary>
    public class KeyFrameAnchorDTO
    {
        public long ID { get; set; }

        /// <summary>When the anchor poses were read on the headset.</summary>
        public DateTime Timestamp { get; set; }

        /// <summary>The pose of the headset (the camera the client wants a global pose for) in its tracking space at that moment.</summary>
        public Pose CameraLocalPose { get; set; }

        /// <summary>The scene anchors of the headset's room, with their poses in its tracking space. Sent empty while the headset has no room loaded yet.</summary>
        public List<SceneAnchorDTO> Anchors { get; set; } = new List<SceneAnchorDTO>();

        /// <summary>Parameterless constructor for JSON deserialization between server and client.</summary>
        public KeyFrameAnchorDTO() { }
    }
}
