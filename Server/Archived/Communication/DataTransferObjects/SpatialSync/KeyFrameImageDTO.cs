using SharedLibrary.Game.Actions;
using SharedLibrary.SpatialSync;
using System;

namespace SharedLibrary.Communication.DataTransferObjects.SpatialSync
{
    /// <summary>
    /// File format of <see cref="KeyFrameImageDTO.ImageData"/>.
    /// </summary>
    public enum KeyFrameImageFormat
    {
        Jpeg = 0,
        Png = 1,
    }

    /// <summary>
    /// One passthrough camera's image captured together with the pose and intrinsics of the camera that took it.
    /// </summary>
    public class CameraImageDTO
    {
        /// <summary>World-space (tracking space) pose of the camera at the moment the image was captured.</summary>
        public Pose CameraLocalPose { get; set; }

        /// <summary>Properties of the camera, including <see cref="CameraIntrinsics.LensOffset"/>: its pose relative to the headset.</summary>
        public CameraIntrinsics CameraIntrinsics { get; set; }

        /// <summary>Camera timestamp of the image.</summary>
        public DateTime Timestamp { get; set; }

        public int Width { get; set; }
        public int Height { get; set; }

        /// <summary>Format of <see cref="ImageData"/>.</summary>
        public KeyFrameImageFormat ImageFormat { get; set; }

        /// <summary>The camera image as an encoded 8-bit grayscale file. It decodes to Width x Height pixels with row 0 at the top.</summary>
        public byte[] ImageData { get; set; }
    }

    /// <summary>
    /// A key frame of the point cloud mapping (archived): the images of the headset's two passthrough cameras, taken together, each with the pose and intrinsics of the camera that
    /// took it. The two cameras are treated the same way. The right camera is the reference camera: the map is built from its images and the
    /// global pose the server answers with is the pose of that camera. The left camera is the other half of the stereo pair, which the server uses
    /// to measure depth.
    /// </summary>
    public class KeyFrameImageDTO
    {
        public long ID { get; set; }

        /// <summary>The left camera. Null if it was not available, in which case the key frame is a single-camera one.</summary>
        public CameraImageDTO LeftCamera { get; set; }

        /// <summary>The right camera, the reference camera. Required.</summary>
        public CameraImageDTO RightCamera { get; set; }
    }
}
