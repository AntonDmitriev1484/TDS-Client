using System;

namespace SharedLibrary.Utilities
{
    public static class TimeUtilities
    {
        public static float GetTime()
        {
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
        }
    }
}
