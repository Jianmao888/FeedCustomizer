using Microsoft.Windows.Widgets.Feeds.Providers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.Marshalling;
using WinRT;

namespace FeedProvider
{
    internal static class ComGuids
    {
        internal static readonly Guid IUnknown = new("00000000-0000-0000-C000-000000000046");
        internal static readonly Guid IFeedProvider = new("7293a12b-0329-458d-ac25-5332be478fde");
    }

    internal sealed class FeedProviderClassFactory
    {
        private static readonly FeedProvider Provider = new();

        public int CreateInstance(nint pUnkOuter, ref Guid riid, out nint ppvObject)
        {
            ppvObject = 0;
            if (pUnkOuter != 0)
            {
                return HResults.ClassENoAggregation;
            }

            try
            {
                ppvObject = MarshalInspectable<IFeedProvider>.FromManaged(Provider);
                return HResults.SOk;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FeedProvider COM activation failed: {ex}");
                return ex.HResult;
            }
        }

        public int LockServer(int fLock) => HResults.SOk;
    }

    internal static class ComClassObject
    {
        [DllImport("ole32.dll")]
        private static extern int CoRegisterClassObject(
            in Guid rclsid,
            nint pUnk,
            uint dwClsContext,
            uint flags,
            out uint registrationCookie);

        [DllImport("ole32.dll")]
        private static extern int CoRevokeClassObject(uint registrationCookie);

        internal static void Register(Guid clsid, nint classFactory, out uint cookie)
        {
            int hr = CoRegisterClassObject(clsid, classFactory, 0x4, 0x1, out cookie);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
        }

        internal static int Revoke(uint cookie) => CoRevokeClassObject(cookie);
    }

    // A hand-written COM vtable avoids the Native AOT generated
    // IClassFactory ABI thunk that fails before dispatching to managed code.
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
                IID = new Guid("00000001-0000-0000-C000-000000000046"),
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
            try
            {
                Guid requestedIid = *riid;
                return Factory.CreateInstance(outer, ref requestedIid, out *result);
            }
            catch (Exception ex)
            {
                *result = 0;
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
        internal const int ENoInterface = unchecked((int)0x80004002);
        internal const int ClassENoAggregation = unchecked((int)0x80040110);
    }
}

