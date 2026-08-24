using Microsoft.Windows.Widgets.Feeds.Providers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using WinRT;

namespace FeedProvider
{
    internal static class ComGuids
    {
        internal static readonly Guid IUnknown = new("00000000-0000-0000-C000-000000000046");
        internal static readonly Guid IClassFactory = new("00000001-0000-0000-C000-000000000046");
        internal static readonly Guid IFeedProvider = new("7293A12B-0329-458D-AC25-5332BE478FDE");
    }

    internal sealed class FeedProviderClassFactory
    {
        // Keep the provider alive for the complete lifetime of the registered
        // class factory. Widgets can activate it long after registration.
        private static readonly FeedProvider Provider = new();

        public int CreateInstance(nint pUnkOuter, ref Guid riid, out nint ppvObject)
        {
            ppvObject = nint.Zero;
            if (pUnkOuter != nint.Zero)
            {
                return HResults.ClassENoAggregation;
            }

            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "FeedProvider-vtable.log"),
                    $"provider-vtable={ABI.Microsoft.Windows.Widgets.Feeds.Providers.IFeedProviderMethods.AbiToProjectionVftablePtr}{Environment.NewLine}");
                // IClassFactory::CreateInstance must return the exact interface
                // requested by the caller. MarshalInspectable always asks for
                // IInspectable, which works under some CoreCLR paths but makes
                // Native AOT fail activation with E_NOINTERFACE. Marshal the
                // projected interface itself so CsWinRT uses the generated
                // IFeedProvider CCW vtable.
                ppvObject = MarshalInterface<IFeedProvider>.FromManaged(Provider);
                return HResults.SOk;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FeedProvider COM activation failed: {ex}");
                ppvObject = nint.Zero;
                return ex.HResult;
            }
        }

        public int LockServer(int fLock) => HResults.SOk;
    }

    internal static class ComClassObject
    {
        private const uint CLSCTX_LOCAL_SERVER = 0x4;
        private const uint REGCLS_MULTIPLEUSE = 0x1;

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoRegisterClassObject(
            in Guid rclsid,
            nint pUnk,
            uint dwClsContext,
            uint flags,
            out uint registrationCookie);

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoRevokeClassObject(uint registrationCookie);

        internal static void Register(Guid clsid, nint classFactory, out uint cookie)
        {
            int hr = CoRegisterClassObject(
                in clsid,
                classFactory,
                CLSCTX_LOCAL_SERVER,
                REGCLS_MULTIPLEUSE,
                out cookie);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }

        internal static int Revoke(uint cookie) => CoRevokeClassObject(cookie);
    }

    // A hand-written COM vtable avoids the Native AOT generated IClassFactory
    // ABI thunk that was failing during Widgets activation.
    internal sealed unsafe class FactoryComWrappers : ComWrappers
    {
        private static readonly FactoryVtable Vtable = CreateVtable();
        internal static FeedProviderClassFactory Factory { get; } = new();

        protected override ComInterfaceEntry* ComputeVtables(
            object obj,
            CreateComInterfaceFlags flags,
            out int count)
        {
            count = 1;
            ComInterfaceEntry* entries = (ComInterfaceEntry*)RuntimeHelpers.AllocateTypeAssociatedMemory(
                typeof(FactoryComWrappers),
                sizeof(ComInterfaceEntry));
            entries[0] = new ComInterfaceEntry
            {
                IID = ComGuids.IClassFactory,
                Vtable = (nint)Unsafe.AsPointer(ref Unsafe.AsRef(in Vtable))
            };
            return entries;
        }

        protected override object CreateObject(IntPtr externalComObject, CreateObjectFlags flags) =>
            throw new NotSupportedException();

        protected override void ReleaseObjects(IEnumerable objects)
        {
        }

        private static FactoryVtable CreateVtable()
        {
            ComWrappers.GetIUnknownImpl(
                out nint queryInterface,
                out nint addRef,
                out nint release);
            return new FactoryVtable
            {
                QueryInterface = queryInterface,
                AddRef = addRef,
                Release = release,
                CreateInstance = (nint)(delegate* unmanaged[MemberFunction]<
                    ComInterfaceDispatch*, nint, Guid*, nint*, int>)&CreateInstanceAbi,
                LockServer = (nint)(delegate* unmanaged[MemberFunction]<
                    ComInterfaceDispatch*, int, int>)&LockServerAbi
            };
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static int CreateInstanceAbi(
            ComInterfaceDispatch* dispatch,
            nint outer,
            Guid* riid,
            nint* result)
        {
            if (riid is null || result is null)
            {
                return HResults.EPointer;
            }

            try
            {
                Guid requestedIid = *riid;
                return Factory.CreateInstance(outer, ref requestedIid, out *result);
            }
            catch (Exception ex)
            {
                *result = nint.Zero;
                return ex.HResult;
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvMemberFunction) })]
        private static int LockServerAbi(ComInterfaceDispatch* dispatch, int lockServer)
        {
            try
            {
                return Factory.LockServer(lockServer);
            }
            catch (Exception ex)
            {
                return ex.HResult;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FactoryVtable
        {
            public nint QueryInterface;
            public nint AddRef;
            public nint Release;
            public nint CreateInstance;
            public nint LockServer;
        }
    }

    internal static class HResults
    {
        internal const int SOk = 0;
        internal const int EPointer = unchecked((int)0x80004003);
        internal const int ENoInterface = unchecked((int)0x80004002);
        internal const int ClassENoAggregation = unchecked((int)0x80040110);
    }
}
