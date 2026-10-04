using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Region;

/// <summary>
/// 窗口生命周期内唯一的区域用例服务。查询、缓存和策略修改共用一个串行边界，
/// 对话框由调用方在进入服务前显示，避免持锁等待 UI 交互。
/// </summary>
internal sealed class RegionService(IRegionPlatform platform)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool? _isNonEuropeanUnion;
    private bool? _isPolicyEnabled;
    private int _policyOperationRunning;

    /// <summary>平台修改及权限恢复正在执行；窗口关闭可据此等待安全完成，不包含确认或锁等待。</summary>
    internal bool IsPolicyOperationRunning => Volatile.Read(ref _policyOperationRunning) != 0;

    /// <summary>读取区域快照，仅缓存确定的结果；暂时失败在后续导航时仍有重新检测机会。</summary>
    internal async Task<RegionState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false))
        {
            return new RegionState(null, null, "Operation = GetRegionState; Error = LockTimeout");
        }

        try
        {
            var diagnostics = new List<string>();
            if (_isNonEuropeanUnion is null)
            {
                DeviceRegionResult device = await ReadDeviceRegionAsync(cancellationToken).ConfigureAwait(false);
                _isNonEuropeanUnion = EuropeanUnionRegionRules.IsNonEuropeanUnion(device.IsoCountryCode);
                AddDiagnostic(diagnostics, device.Diagnostic);
                if (_isNonEuropeanUnion is null && string.IsNullOrWhiteSpace(device.Diagnostic))
                {
                    AddDiagnostic(diagnostics, "Operation = ReadDeviceRegion; Error = UnrecognizedCountryCode");
                }
            }

            if (_isPolicyEnabled is null)
            {
                RegionPolicyStateResult policy = await ReadPolicyStateAsync(cancellationToken).ConfigureAwait(false);
                _isPolicyEnabled = policy.IsEnabled;
                AddDiagnostic(diagnostics, policy.Diagnostic);
                if (_isPolicyEnabled is null && string.IsNullOrWhiteSpace(policy.Diagnostic))
                {
                    AddDiagnostic(diagnostics, "Operation = ReadRegionPolicy; Error = UnknownPolicyState");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new RegionState(
                _isNonEuropeanUnion,
                _isPolicyEnabled,
                string.Join(Environment.NewLine, diagnostics));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 串行启用区域策略。页面取消只能阻止修改开始；修改开始后必须观察权限恢复，
    /// 更新确定状态后才向已取消的调用方传递取消，避免中断脚本的 ACL 恢复。
    /// </summary>
    internal async Task<RegionPolicyOperationResult> EnablePolicyAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false))
        {
            return new RegionPolicyOperationResult(
                RegionPolicyOperationStatus.Failed,
                "Operation = EnableRegionPolicy; Error = LockTimeout");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RegionPolicyOperationResult result = await EnableCriticalPolicyAsync(cancellationToken).ConfigureAwait(false);
            if (result.Status == RegionPolicyOperationStatus.Success)
            {
                _isPolicyEnabled = true;
            }
            else if (result.Status != RegionPolicyOperationStatus.Cancelled)
            {
                // 修改或权限恢复失败时，文件可能已经变化；后续查询不能复用修改前的结论。
                _isPolicyEnabled = null;
            }

            // 页面已经离开时仍保存完整观测的系统结果，但不再向旧页面提交提示。
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RegionPolicyOperationResult> EnableCriticalPolicyAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _policyOperationRunning, 1);
        try
        {
            // 先发布运行标志再做最后一次取消检查，使关闭入口在取消页面后观察标志时，
            // 要么看到正在恢复的修改，要么使尚未进入修改的调用在此停止。
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // 执行器遇调用方取消会终止进程，可能跳过提权脚本的 finally。
                // 关键区只使用执行器既有有限超时，不把页面生命周期令牌传入修改操作。
                return await platform.EnablePolicyAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new RegionPolicyOperationResult(
                    RegionPolicyOperationStatus.Failed,
                    RegionDiagnostics.FromException("EnableRegionPolicy", ex));
            }
        }
        finally
        {
            Volatile.Write(ref _policyOperationRunning, 0);
        }
    }

    private async Task<DeviceRegionResult> ReadDeviceRegionAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await platform.ReadDeviceRegionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DeviceRegionResult(
                null,
                RegionDiagnostics.FromException("ReadDeviceRegion", ex));
        }
    }

    private async Task<RegionPolicyStateResult> ReadPolicyStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await platform.ReadPolicyStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new RegionPolicyStateResult(
                null,
                RegionDiagnostics.FromException("ReadRegionPolicy", ex));
        }
    }

    private static void AddDiagnostic(List<string> diagnostics, string diagnostic)
    {
        if (!string.IsNullOrWhiteSpace(diagnostic))
        {
            diagnostics.Add(diagnostic);
        }
    }
}
