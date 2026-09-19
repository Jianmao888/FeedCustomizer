using FeedCustomizer.Core.Feedback;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Infrastructure.Feedback;

/// <summary>
/// 通过 Windows Simple MAPI 打开桌面邮件编辑器并请求附加日志。
/// 本机调用可能一直等待到用户关闭邮件窗口，因此放到工作线程，避免阻塞 WinUI 消息循环。
/// </summary>
internal sealed partial class SimpleMapiFeedbackMailTransport : IFeedbackMailTransport
{
    private const uint SuccessSuccess = 0;
    private const uint MapiUserAbort = 1;
    private const uint MapiNotSupported = 26;
    private const uint MapiLogonUi = 0x00000001;
    private const uint MapiNewSession = 0x00000002;
    private const uint MapiDialog = 0x00000008;
    private const uint MapiTo = 1;
    private const uint AttachmentPositionNotSpecified = uint.MaxValue;

    public string Name => "SimpleMAPI";

    public Task<FeedbackMailTransportResult> TryLaunchAsync(
        FeedbackMailMessage message,
        IntPtr ownerWindowHandle,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(message.AttachmentPath))
        {
            return Task.FromResult(new FeedbackMailTransportResult(
                FeedbackMailTransportStatus.Failed,
                Name,
                false,
                "日志附件在调用 MAPI 前不存在。"));
        }

        // MAPI 没有可靠的异步取消协议。取消只在交给客户端前生效，接管后由用户控制邮件窗口。
        return Task.Run(() => LaunchCore(message, ownerWindowHandle), CancellationToken.None);
    }

    private static FeedbackMailTransportResult LaunchCore(
        FeedbackMailMessage message,
        IntPtr ownerWindowHandle)
    {
        IntPtr subject = IntPtr.Zero;
        IntPtr body = IntPtr.Zero;
        IntPtr recipientName = IntPtr.Zero;
        IntPtr recipientAddress = IntPtr.Zero;
        IntPtr recipientPointer = IntPtr.Zero;
        IntPtr attachmentPath = IntPtr.Zero;
        IntPtr attachmentName = IntPtr.Zero;
        IntPtr attachmentPointer = IntPtr.Zero;

        try
        {
            subject = Marshal.StringToCoTaskMemUni(message.Subject);
            body = Marshal.StringToCoTaskMemUni(message.Body);
            recipientName = Marshal.StringToCoTaskMemUni(message.Recipient);
            recipientAddress = Marshal.StringToCoTaskMemUni($"SMTP:{message.Recipient}");
            attachmentPath = Marshal.StringToCoTaskMemUni(message.AttachmentPath);
            attachmentName = Marshal.StringToCoTaskMemUni(message.AttachmentName);

            var recipient = new MapiRecipDescW
            {
                RecipientClass = MapiTo,
                Name = recipientName,
                Address = recipientAddress
            };
            recipientPointer = AllocateStructure(recipient);

            var attachment = new MapiFileDescW
            {
                Position = AttachmentPositionNotSpecified,
                PathName = attachmentPath,
                FileName = attachmentName
            };
            attachmentPointer = AllocateStructure(attachment);

            var nativeMessage = new MapiMessageW
            {
                Subject = subject,
                NoteText = body,
                RecipientCount = 1,
                Recipients = recipientPointer,
                FileCount = 1,
                Files = attachmentPointer
            };

            uint result = MapiSendMailW(
                0,
                unchecked((nuint)ownerWindowHandle.ToInt64()),
                ref nativeMessage,
                MapiLogonUi | MapiNewSession | MapiDialog,
                0);
            return result switch
            {
                SuccessSuccess => new FeedbackMailTransportResult(
                    FeedbackMailTransportStatus.Launched,
                    "SimpleMAPI",
                    true,
                    "Simple MAPI 已完成客户端调用。"),
                MapiUserAbort => new FeedbackMailTransportResult(
                    FeedbackMailTransportStatus.ClientHandled,
                    "SimpleMAPI",
                    true,
                    "邮件客户端流程由用户结束，应用不跟踪是否发送。"),
                MapiNotSupported => new FeedbackMailTransportResult(
                    FeedbackMailTransportStatus.Unsupported,
                    "SimpleMAPI",
                    false,
                    $"Simple MAPI 不受支持，返回码={result}。"),
                _ => new FeedbackMailTransportResult(
                    FeedbackMailTransportStatus.Failed,
                    "SimpleMAPI",
                    false,
                    $"Simple MAPI 调用失败，返回码={result}。")
            };
        }
        finally
        {
            FreeStructure(attachmentPointer);
            FreeStructure(recipientPointer);
            Marshal.FreeCoTaskMem(attachmentName);
            Marshal.FreeCoTaskMem(attachmentPath);
            Marshal.FreeCoTaskMem(recipientAddress);
            Marshal.FreeCoTaskMem(recipientName);
            Marshal.FreeCoTaskMem(body);
            Marshal.FreeCoTaskMem(subject);
        }
    }

    private static IntPtr AllocateStructure<T>(T value) where T : struct
    {
        IntPtr pointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<T>());
        Marshal.StructureToPtr(value, pointer, fDeleteOld: false);
        return pointer;
    }

    private static void FreeStructure(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    [LibraryImport("MAPI32.dll", EntryPoint = "MAPISendMailW")]
    private static partial uint MapiSendMailW(
        nuint session,
        nuint uiParameter,
        ref MapiMessageW message,
        uint flags,
        uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MapiMessageW
    {
        internal uint Reserved;
        internal IntPtr Subject;
        internal IntPtr NoteText;
        internal IntPtr MessageType;
        internal IntPtr DateReceived;
        internal IntPtr ConversationId;
        internal uint Flags;
        internal IntPtr Originator;
        internal uint RecipientCount;
        internal IntPtr Recipients;
        internal uint FileCount;
        internal IntPtr Files;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MapiRecipDescW
    {
        internal uint Reserved;
        internal uint RecipientClass;
        internal IntPtr Name;
        internal IntPtr Address;
        internal uint EntryIdSize;
        internal IntPtr EntryId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MapiFileDescW
    {
        internal uint Reserved;
        internal uint Flags;
        internal uint Position;
        internal IntPtr PathName;
        internal IntPtr FileName;
        internal IntPtr FileType;
    }
}
