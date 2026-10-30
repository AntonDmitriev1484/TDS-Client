using SharedLibrary.Communication.DataTransferObjects.SpatialSync;

namespace Server.Manager.SpatialSync
{
    public class GlobalMappingManager
    {
        private readonly DebugImageDisplayService? _debugImageDisplayService;

        #region Properties

        public bool DebugMode { get; set; }

        #endregion Properties

        #region Initialization

        public GlobalMappingManager(bool debugMode)
        {
            DebugMode = debugMode;
            _debugImageDisplayService = debugMode ? new DebugImageDisplayService() : null;
        }

        #endregion Initialization


        #region Methods

        public void HandleNewKeyFrame(int playerID, KeyFrameDTO keyFrameDTO)
        {
            if (DebugMode)
            {
                _debugImageDisplayService!.ShowFrame(playerID, keyFrameDTO);
            }

        }

        #endregion Methods

    }
}
