using Microsoft.Windows.Widgets.Feeds.Providers;
using Microsoft.Windows.Widgets.Providers;
using System.Threading;

namespace FeedProvider
{

    public static class Program
    {
        [MTAThread]
        static void Main(string[] args)
        {
            Console.WriteLine("FeedProvider Starting...");
            if (args.Length > 0 && args[0] == "-RegisterProcessAsComServer")
            {
                WinRT.ComWrappersSupport.InitializeComWrappers();

                uint registrationHandle;
                var factory = new FeedProviderFactory<FeedProvider>();
                Com.ClassObject.Register(typeof(FeedProvider).GUID, factory, out registrationHandle);

                Console.WriteLine("Feed Provider registered.");

                var existingFeedProviders = FeedManager.GetDefault().GetEnabledFeedProviders();
                if (existingFeedProviders != null)
                {
                    Console.WriteLine($"There are {existingFeedProviders.Length} FeedProviders currently outstanding:");
                    foreach (var feedProvider in existingFeedProviders)
                    {
                        Console.WriteLine($"  ProviderId: {feedProvider.FeedProviderDefinitionId}, DefinitionIds: ");
                        var m = WidgetManager.GetDefault().GetWidgetIds();
                        if (feedProvider.EnabledFeedDefinitionIds != null)
                        {
                            foreach (var enabledFeedId in feedProvider.EnabledFeedDefinitionIds)
                            {
                                Console.WriteLine($" {enabledFeedId} ");
                            }
                        }
                    }
                }
                // Keep the local COM server alive after registering the class
                // factory. Exiting here immediately revokes the factory.
                using var exitEvent = new ManualResetEvent(false);
                AppDomain.CurrentDomain.ProcessExit += (_, _) => exitEvent.Set();
                Console.CancelKeyPress += (_, args) =>
                {
                    args.Cancel = true;
                    exitEvent.Set();
                };
                exitEvent.WaitOne();
                Com.ClassObject.Revoke(registrationHandle);
            }
            else
            {
                Console.WriteLine("Not being launched to service Feed Provider... exiting.");
            }
        }
    }
}
