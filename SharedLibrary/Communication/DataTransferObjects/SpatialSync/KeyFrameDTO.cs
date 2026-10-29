using SharedLibrary.Game.Actions;
using System;

namespace SharedLibrary.Communication.DataTransferObjects.SpatialSync
{
    /// <summary>
    /// A passthrough camera image captured together with the pose of the camera that took it.
    /// </summary>
    public class KeyFrameDTO
    {
        public long ID { get; set; }

        /// <summary>World-space pose of the camera at the moment the image was captured.</summary>
        public Pose CameraPose { get; set; }

        /// <summary>Camera timestamp of the image.</summary>
        public DateTime Timestamp { get; set; }

        public int Width { get; set; }
        public int Height { get; set; }

        /// <summary>8-bit luma, row-major, row 0 = top of the image, Width * Height bytes.</summary>
        public byte[] Gray { get; set; }
    }
}
