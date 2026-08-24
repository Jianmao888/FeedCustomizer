using Microsoft.Windows.Widgets.Feeds.Providers;
using System.Runtime.InteropServices;

namespace FeedProvider
{
    // The provider is returned across the WinRT ABI from the native-AOT COM
    // class factory.  Explicitly opt this implementation into CsWinRT's
    // generated CCW/vtable path; without it only IClassFactory is generated
    // and MarshalInspectable<IFeedProvider>.FromManaged(...) fails at runtime.
    [WinRT.GeneratedWinRTExposedType]
    [ComVisible(true)]
    [ComDefaultInterface(typeof(IFeedProvider))]
    [Guid("53FCE339-5BFD-4911-BCAF-21302DE5CD75")]
    public sealed partial class FeedProvider : IFeedProvider
    {
        public void OnFeedProviderEnabled(FeedProviderEnabledArgs args)
        {
            Console.WriteLine($"{args.FeedProviderDefinitionId} feed provider was enabled.");
            var updateOptions = new CustomQueryParametersUpdateOptions(args.FeedProviderDefinitionId, "param1&param2");
            FeedManager.GetDefault().SetCustomQueryParameters(updateOptions);
        }

        public void OnFeedProviderDisabled(FeedProviderDisabledArgs args)
        {
            Console.WriteLine($"{args.FeedProviderDefinitionId} feed provider was disabled.");
        }

        public void OnFeedEnabled(FeedEnabledArgs args)
        {
            Console.WriteLine($"{args.FeedDefinitionId} feed was enabled.");
        }

        public void OnFeedDisabled(FeedDisabledArgs args)
        {
            Console.WriteLine($"{args.FeedDefinitionId} feed was disabled.");
        }

        public void OnCustomQueryParametersRequested(CustomQueryParametersRequestedArgs args)
        {
            Console.WriteLine($"CustomQueryParamaters were requested for {args.FeedProviderDefinitionId}.");
            var updateOptions = new CustomQueryParametersUpdateOptions(args.FeedProviderDefinitionId, "param1&param2");
            FeedManager.GetDefault().SetCustomQueryParameters(updateOptions);
        }
    }
}
