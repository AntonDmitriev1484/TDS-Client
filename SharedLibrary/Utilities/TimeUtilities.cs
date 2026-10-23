using System;

namespace SharedLibrary.Utilities
{
    public static class TimeUtilities
    {
        public static double GetTime()
        {
            return DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        }
    }
}
