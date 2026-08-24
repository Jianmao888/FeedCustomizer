using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace FeedProvider
{
    public static class Program
    {
        // Keep the custom ComWrappers and class-factory CCW rooted until the
        // local-server registration is revoked.
        private static readonly FactoryComWrappers ComWrappers = new();
        private static nint _factoryPointer;

        [MTAThread]
        private static void Main(string[] args)
        {
            Console.WriteLine("FeedProvider Starting...");
            if (args.Length == 0 || args[0] != "-RegisterProcessAsComServer")
            {
                Console.WriteLine("Not being launched to service Feed Provider... exiting.");
                return;
            }

            // Initialize CsWinRT before MarshalInspectable<IFeedProvider> is
            // called from IClassFactory::CreateInstance.
            WinRT.ComWrappersSupport.InitializeComWrappers();

            uint registrationHandle = 0;
            _factoryPointer = ComWrappers.GetOrCreateComInterfaceForObject(
                FactoryComWrappers.Factory,
                CreateComInterfaceFlags.None);

            try
            {
                ComClassObject.Register(
                    typeof(FeedProvider).GUID,
                    _factoryPointer,
                    out registrationHandle);

                Console.WriteLine("Feed Provider registered.");

                // Do not query FeedManager or WidgetManager here. Those calls
                // race Widgets activation and are unnecessary for registration.
                using var exitEvent = new ManualResetEvent(false);
                AppDomain.CurrentDomain.ProcessExit += (_, _) => exitEvent.Set();
                Console.CancelKeyPress += (_, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    exitEvent.Set();
                };
                exitEvent.WaitOne();
            }
            finally
            {
                if (registrationHandle != 0)
                {
                    ComClassObject.Revoke(registrationHandle);
                }

                if (_factoryPointer != nint.Zero)
                {
                    Marshal.Release(_factoryPointer);
                    _factoryPointer = nint.Zero;
                }
            }
        }
    }
}
