using System; 
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace WinRT.FeedProviderGenericHelpers
{

    internal static class GlobalVtableLookup
    {

        [System.Runtime.CompilerServices.ModuleInitializer]
        internal static void InitializeGlobalVtableLookup()
        {
            ComWrappersSupport.RegisterTypeComInterfaceEntriesLookup(new Func<Type, ComWrappers.ComInterfaceEntry[]>(LookupVtableEntries));
            ComWrappersSupport.RegisterTypeRuntimeClassNameLookup(new Func<Type, string>(LookupRuntimeClassName));
        }

        private static ComWrappers.ComInterfaceEntry[] LookupVtableEntries(Type type)
        {
            string typeName = type.ToString();
            if (typeName == "FeedProvider.FeedProvider"
            )
            {
                
        return new global::System.Runtime.InteropServices.ComWrappers.ComInterfaceEntry[]
        {
            new global::System.Runtime.InteropServices.ComWrappers.ComInterfaceEntry
            {
                IID = global::ABI.Microsoft.Windows.Widgets.Feeds.Providers.IFeedProviderMethods.IID,
                Vtable = global::ABI.Microsoft.Windows.Widgets.Feeds.Providers.IFeedProviderMethods.AbiToProjectionVftablePtr
            },
};

            }
            return default;
        }
private static string LookupRuntimeClassName(Type type)
{
    string typeName = type.ToString();
if (typeName == "FeedProvider.FeedProvider"
)
{
    return "Microsoft.Windows.Widgets.Feeds.Providers.IFeedProvider";
}
            return default;
        }
    }
}
