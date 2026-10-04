using FeedCustomizer.Core.Infrastructure.Logging;
using FeedCustomizer.Core.Infrastructure.PowerShell;
using FeedCustomizer.Core.Models;
using FeedCustomizer.Core.Region;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Infrastructure.Region;

/// <summary>封装 Windows 设备区域、系统策略文件和既有提权适配器，不维护页面共享状态。</summary>
internal sealed partial class WindowsRegionPlatform(RegionPolicyPowerShellAdapter regionPolicy) : IRegionPlatform
{
    private static readonly IAppLog Log = AppLog.For<WindowsRegionPlatform>();
    private static readonly Guid ThirdPartyFeedPolicyId = new("16d2b50e-fa7c-4bb1-ab17-01d766530b3b");
    private const string PolicyFileName = "IntegratedServicesRegionPolicySet.json";
    private const string DeviceRegionKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Control Panel\DeviceRegion";
    private const string DeviceRegionValueName = "DeviceRegion";
    private const int KeyRead = 0x20019;
    private const int KeyWow64_64Key = 0x0100;
    private const uint RegistryDword = 4;
    private const int GeoIso2 = 0x0004;

    /// <summary>将短暂阻塞的 Win32 读取留在后台，调用方不需要了解线程或本机句柄。</summary>
    public Task<DeviceRegionResult> ReadDeviceRegionAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            // 只访问固定的本机注册表键并调用本机地理信息 API，不遍历文件或等待外部进程；
            // Win32 调用无法中途取消，因此在这组短同步调用前后观察调用方取消。
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                DeviceRegionResult result = ReadDeviceRegion();
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "读取 Windows 设备区域失败，注册表路径={RegistryPath}", DeviceRegionKeyPath);
                return new DeviceRegionResult(null, RegionDiagnostics.FromException("ReadDeviceRegion", ex));
            }
        }, cancellationToken);
    }

    /// <summary>读取固定系统文件并结构化解析策略，访问失败不会误判为已解限。</summary>
    public async Task<RegionPolicyStateResult> ReadPolicyStateAsync(CancellationToken cancellationToken = default)
    {
        string policyPath = Path.Combine(Environment.SystemDirectory, PolicyFileName);
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readCancellation.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            string content = await File.ReadAllTextAsync(policyPath, readCancellation.Token).ConfigureAwait(false);
            RegionPolicyStateResult result = RegionPolicyJsonParser.Parse(content, ThirdPartyFeedPolicyId);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.IsEnabled is null)
            {
                Log.Warning("系统区域策略无法确定，目标路径={PolicyPath}，诊断={Diagnostic}", policyPath, result.Diagnostic);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (readCancellation.IsCancellationRequested)
        {
            Log.Warning("读取系统区域策略超时，目标路径={PolicyPath}", policyPath);
            return new RegionPolicyStateResult(null, "Operation = ReadRegionPolicy; Error = ReadTimeout");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取系统区域策略失败，目标路径={PolicyPath}", policyPath);
            return new RegionPolicyStateResult(
                null,
                RegionDiagnostics.FromException("ReadRegionPolicy", ex));
        }
    }

    /// <summary>
    /// 复用统一执行器的提权操作，用稳定退出码区分失败。取消只在修改开始前观察；
    /// 已开始的操作使用执行器有限超时并完整观察恢复，避免页面取消终止 ACL 恢复脚本。
    /// </summary>
    public async Task<RegionPolicyOperationResult> EnablePolicyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            PowerShellResult result = await regionPolicy.EnablePolicyAsync(
                PolicyFileName,
                ThirdPartyFeedPolicyId.ToString("B"),
                CancellationToken.None).ConfigureAwait(false);
            RegionPolicyOperationStatus status = result.ExitCode switch
            {
                0 => RegionPolicyOperationStatus.Success,
                PowerShellExitCodes.ElevationCancelled => RegionPolicyOperationStatus.Cancelled,
                2 => RegionPolicyOperationStatus.PolicyNotFound,
                _ => RegionPolicyOperationStatus.Failed
            };

            Log.Information(
                "地区策略修改结束，目标策略={PolicyId}，退出码={ExitCode}，状态={Status}",
                ThirdPartyFeedPolicyId,
                result.ExitCode,
                status);
            // 执行器的原始输出只在基础设施边界处理一次，上层结果也使用既有脱敏和长度上限，
            // 避免错误对话框或反馈重新带出机器路径及过长的外部诊断。
            string diagnostic = LogPrivacy.PrepareDiagnostic(
                $"Operation = EnableRegionPolicy; ExitCode = {result.ExitCode}" + Environment.NewLine +
                $"Output = {result.Output}" + Environment.NewLine +
                $"Error = {result.Error}");
            return new RegionPolicyOperationResult(status, diagnostic);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "修改系统区域策略失败，目标策略={PolicyId}", ThirdPartyFeedPolicyId);
            return new RegionPolicyOperationResult(
                RegionPolicyOperationStatus.Failed,
                RegionDiagnostics.FromException("EnableRegionPolicy", ex));
        }
    }

    private static DeviceRegionResult ReadDeviceRegion()
    {
        IntPtr key = IntPtr.Zero;
        try
        {
            // 预定义 HKEY 是指针宽度的有符号句柄，不能在 64 位调用中按普通 int 传递。
            nint localMachine = unchecked((int)0x80000002);
            int result = RegOpenKeyEx(localMachine, DeviceRegionKeyPath, 0, KeyRead | KeyWow64_64Key, out key);
            if (result != 0 || key == IntPtr.Zero)
            {
                return DeviceFailure("RegOpenKeyEx", result);
            }

            uint dataSize = sizeof(int);
            int geoId = 0;
            result = RegQueryValueEx(key, DeviceRegionValueName, IntPtr.Zero, out uint type, ref geoId, ref dataSize);
            if (result != 0)
            {
                return DeviceFailure("RegQueryValueEx", result);
            }

            if (type != RegistryDword || dataSize != sizeof(int))
            {
                return new DeviceRegionResult(null, "Operation = ReadDeviceRegion; Error = InvalidRegistryValueType");
            }

            return ReadIsoCountryCode(geoId);
        }
        finally
        {
            if (key != IntPtr.Zero)
            {
                int closeResult = RegCloseKey(key);
                if (closeResult != 0)
                {
                    // 关闭句柄失败单独记录，不覆盖区域读取本身的结果与诊断。
                    Log.Warning("关闭设备区域注册表句柄失败，退出码={ExitCode}", closeResult);
                }
            }
        }
    }

    private static unsafe DeviceRegionResult ReadIsoCountryCode(int geoId)
    {
        int required = GetGeoInfo(geoId, GeoIso2, null, 0, 0);
        if (required <= 0)
        {
            return DeviceFailure("GetGeoInfoSize", Marshal.GetLastPInvokeError());
        }

        if (required > 32)
        {
            return new DeviceRegionResult(null, "Operation = ReadDeviceRegion; Error = InvalidCountryCodeLength");
        }

        var buffer = new char[required];
        fixed (char* geoData = buffer)
        {
            if (GetGeoInfo(geoId, GeoIso2, geoData, buffer.Length, 0) <= 0)
            {
                return DeviceFailure("GetGeoInfo", Marshal.GetLastPInvokeError());
            }
        }

        int terminatorIndex = Array.IndexOf(buffer, '\0');
        int length = terminatorIndex >= 0 ? terminatorIndex : buffer.Length;
        string countryCode = new(buffer, 0, length);
        return string.IsNullOrWhiteSpace(countryCode)
            ? new DeviceRegionResult(null, "Operation = ReadDeviceRegion; Error = EmptyCountryCode")
            : new DeviceRegionResult(countryCode, string.Empty);
    }

    private static DeviceRegionResult DeviceFailure(string stage, int errorCode)
    {
        Log.Warning("读取设备区域失败，阶段={Stage}，错误码={ErrorCode}，注册表路径={RegistryPath}", stage, errorCode, DeviceRegionKeyPath);
        return new DeviceRegionResult(null, $"Operation = ReadDeviceRegion; Stage = {stage}; ErrorCode = {errorCode}");
    }

    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegOpenKeyEx(nint hKey, string lpSubKey, int ulOptions, int samDesired, out IntPtr phkResult);

    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryValueExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegQueryValueEx(IntPtr hKey, string lpValueName, IntPtr lpReserved, out uint lpType, ref int lpData, ref uint lpcbData);

    [LibraryImport("advapi32.dll")]
    private static partial int RegCloseKey(IntPtr hKey);

    [LibraryImport("kernel32.dll", EntryPoint = "GetGeoInfoW", SetLastError = true)]
    private static unsafe partial int GetGeoInfo(int location, int geoType, char* geoData, int dataLength, int langId);
}
