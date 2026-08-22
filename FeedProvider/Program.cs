using Microsoft.Windows.Widgets.Feeds.Providers;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;

namespace FeedProvider
{

    public static class Program
    {
        // Keep every object involved in the registered class object rooted for
        // the complete lifetime of the out-of-process COM server.  The
        // generated AOT ABI dispatch stores the factory object in a handle,
        // but retaining these roots also prevents a wrapper from being
        // collected while the Widgets host is activating it.
        private static readonly FactoryComWrappers ComWrappers = new();
        private static nint _factoryPointer;

        [MTAThread]
        static void Main(string[] args)
        {
            Console.WriteLine("FeedProvider Starting...");
            if (args.Length > 0 && args[0] == "-RegisterProcessAsComServer")
            {
                WinRT.ComWrappersSupport.InitializeComWrappers();

                uint registrationHandle;
                _factoryPointer = ComWrappers.GetOrCreateComInterfaceForObject(
                    FactoryComWrappers.Factory,
                    CreateComInterfaceFlags.None);
                ComClassObject.Register(FeedProvider.ClassId, _factoryPointer, out registrationHandle);

                Console.WriteLine("Feed Provider registered.");

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
                ComClassObject.Revoke(registrationHandle);
                Marshal.Release(_factoryPointer);
                _factoryPointer = 0;
            }
            else
            {
                Console.WriteLine("Not being launched to service Feed Provider... exiting.");
            }
        }
    }
}
