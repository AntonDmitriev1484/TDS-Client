using SharedLibrary.Game.Actions;
using SharedLibrary.SpatialSync;
using System;

namespace SharedLibrary.Communication.DataTransferObjects.SpatialSync
{
    /// <summary>
    /// File format of <see cref="KeyFrameDTO.ImageData"/>.
    /// </summary>
    public enum KeyFrameImageFormat
    {
        Jpeg = 0,
        Png = 1,
    }

    /// <summary>
    /// A passthrough camera image captured together with the pose of the camera that took it.
    /// </summary>
    public class KeyFrameDTO
    {
        public long ID { get; set; }

        /// <summary>World-space pose of the camera at the moment the image was captured.</summary>
        public Pose CameraPose { get; set; }

        /// <summary>
        /// Properties of the camera that took the snapshot.
        /// </summary>
        public CameraIntrinsics CameraIntrinsics { get; set; }

        /// <summary>Camera timestamp of the image.</summary>
        public DateTime Timestamp { get; set; }

        public int Width { get; set; }
        public int Height { get; set; }

        /// <summary>Format of <see cref="ImageData"/>.</summary>
        public KeyFrameImageFormat ImageFormat { get; set; }

        /// <summary>
        /// The camera image as an encoded 8-bit grayscale file (see <see cref="ImageFormat"/>). It decodes to
        /// Width x Height pixels with row 0 at the top.
        /// </summary>
        public byte[] ImageData { get; set; }
    }
}
