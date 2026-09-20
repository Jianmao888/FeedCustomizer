using FeedCustomizer.Core.Infrastructure.Logging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Documents;

/// <summary>
/// 应用文档协调服务。它统一处理版本同步、缺失自愈和更新后遗留清理，
/// 并用进程级串行锁保证启动维护与用户点击打开不会同时替换目录。
/// </summary>
internal sealed class ApplicationDocumentService
{
    private static readonly IAppLog Log = AppLog.For<ApplicationDocumentService>();
    private readonly IApplicationDocumentStorage _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal ApplicationDocumentService(IApplicationDocumentStorage storage)
    {
        _storage = storage;
    }

    /// <summary>
    /// 在启动主流程完成后维护文档。普通同版本启动只读取小型状态文件，
    /// 只有检测到应用更新时才允许执行旧注册目录的 PowerShell 清理。
    /// </summary>
    internal async Task MaintainAfterStartupAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureCatalogAsync(forceForMissingEntry: false, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 准备指定文档。即使状态显示当前版本，入口被用户手动删除时也会从不可变安装目录全量自愈。
    /// 自愈不是应用更新，因此不会触发 PowerShell 遗留清理。
    /// </summary>
    internal async Task<ApplicationDocumentResult> PrepareAsync(
        ApplicationDocumentKind kind,
        string languageTag,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            string? relativeEntry = ApplicationDocumentCatalog.GetRelativeEntry(kind, languageTag);
            if (relativeEntry is null)
            {
                return ApplicationDocumentResult.Failed(
                    ApplicationDocumentStatus.Unsupported,
                    $"文档类型尚未配置：{kind}");
            }

            await EnsureCatalogAsync(forceForMissingEntry: false, cancellationToken);
            string? path = _storage.ResolveDocumentPath(kind, languageTag);
            if (path is not null)
            {
                return ApplicationDocumentResult.Ready(path);
            }

            // 状态文件可能仍是当前版本，但用户已手动删除入口或关联资源。
            // 此处只从包内源重建私有目录，不访问真实 LocalAppData，也不启动 PowerShell。
            Log.Warning("文档入口缺失，正在从安装包自愈，类型={DocumentKind}", kind);
            await EnsureCatalogAsync(forceForMissingEntry: true, cancellationToken);
            path = _storage.ResolveDocumentPath(kind, languageTag);
            return path is not null
                ? ApplicationDocumentResult.Ready(path)
                : ApplicationDocumentResult.Failed(
                    ApplicationDocumentStatus.MissingAfterSynchronization,
                    $"同步完成后仍找不到文档入口：{relativeEntry}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Error(ex, "准备应用文档失败，类型={DocumentKind}", kind);
            return ApplicationDocumentResult.Failed(
                ApplicationDocumentStatus.SynchronizationFailed,
                ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureCatalogAsync(
        bool forceForMissingEntry,
        CancellationToken cancellationToken)
    {
        ApplicationDocumentState? previousState;
        try
        {
            previousState = _storage.ReadState();
        }
        catch (InvalidDataException ex)
        {
            // 状态本身不是用户数据；损坏时按未知版本从不可变安装源重建，随后原子覆写状态文件。
            Log.Warning(ex, "应用文档版本状态损坏，正在重建文档目录");
            previousState = null;
        }
        string currentVersion = _storage.CurrentPackageVersion;

        // 首个采用独立文档目录的版本没有旧状态文件。旧私有 HelpDoc 是无需 PowerShell 即可读取的升级证据，
        // 可区分历史安装与全新安装，避免全新安装无意义地启动外部进程。
        bool legacyPrivateHelpExists = previousState is null && _storage.LegacyPrivateHelpExists();
        bool isApplicationUpdate = previousState is not null
            ? !string.Equals(previousState.PackageVersion, currentVersion, StringComparison.Ordinal)
            : legacyPrivateHelpExists;
        bool catalogIsCurrent = previousState is not null &&
            previousState.CatalogSchema == ApplicationDocumentCatalog.SchemaVersion &&
            string.Equals(previousState.PackageVersion, currentVersion, StringComparison.Ordinal);

        if (!catalogIsCurrent || forceForMissingEntry)
        {
            await _storage.ReplaceCatalogAsync(cancellationToken);
            Log.Information(
                "应用文档目录已同步，包版本={PackageVersion}，原因={SynchronizationReason}",
                currentVersion,
                forceForMissingEntry ? "入口缺失自愈" : "首次安装或应用更新");
        }

        string cleanupAttemptedVersion = previousState?.LegacyCleanupAttemptedVersion ?? string.Empty;
        // 全新安装没有历史副本，直接标记迁移完成；成功清理过的安装后续更新也无需再次启动 PowerShell。
        bool cleanupCompleted = previousState?.LegacyCleanupCompleted ?? !legacyPrivateHelpExists;
        if (isApplicationUpdate &&
            !cleanupCompleted &&
            !string.Equals(cleanupAttemptedVersion, currentVersion, StringComparison.Ordinal))
        {
            // 必须先成功发布新目录再移除旧副本。调用脚本前先记为本版本已尝试，
            // 防止进程中断或权限问题让以后每次普通启动都创建 PowerShell 进程；
            // 若本次有诊断，下一次应用更新仍可再尝试一次。
            cleanupAttemptedVersion = currentVersion;
            _storage.WriteState(new ApplicationDocumentState(
                currentVersion,
                ApplicationDocumentCatalog.SchemaVersion,
                cleanupAttemptedVersion,
                LegacyCleanupCompleted: false));
            LegacyDocumentCleanupResult cleanup = await _storage.RemoveLegacyHelpAsync(cancellationToken);
            cleanupCompleted = cleanup.Diagnostics.Count == 0;
            Log.Information(
                "应用更新后的旧文档清理结束，私有副本已移除={PrivateRemoved}，注册副本已移除={RegisteredRemoved}，诊断数量={DiagnosticCount}",
                cleanup.PrivateCopyRemoved,
                cleanup.RegisteredCopyRemoved,
                cleanup.Diagnostics.Count);
            foreach (string diagnostic in cleanup.Diagnostics)
            {
                Log.Warning("旧文档清理诊断：{CleanupDiagnostic}", diagnostic);
            }
        }

        var currentState = new ApplicationDocumentState(
            currentVersion,
            ApplicationDocumentCatalog.SchemaVersion,
            cleanupAttemptedVersion,
            cleanupCompleted);
        if (previousState != currentState)
        {
            // 同版本正常启动不重复写磁盘；状态只在首次同步、更新或清理结果变化时落盘。
            _storage.WriteState(currentState);
        }
    }
}
