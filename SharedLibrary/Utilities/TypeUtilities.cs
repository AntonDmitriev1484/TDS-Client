using System;
using System.Linq;

namespace SharedLibrary.Utilities
{
    public static class TypeUtilities
    {

        // Attempt to resolve the type without needing the full namespace path
        public static Type GetTypeByName(string typeString)
        {
            // Search through all loaded assemblies in the current AppDomain
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => a.GetTypes())  // Get all types from all assemblies
                .FirstOrDefault(t => t.Name == typeString);  // Match type name only

            if (type == null)
            {
                throw new Exception($"The incoming message's type was not found: {typeString}");
            }

            return type;
        }
    }
}