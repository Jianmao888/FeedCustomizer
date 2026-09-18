using Microsoft.Win32;
using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Windows.ApplicationModel;

namespace FeedCustomizer.Core.Infrastructure.Logging;

/// <summary>
/// 在每次启动时记录一次版本环境。仅收集诊断所需的公开版本信息，不收集用户名、设备名或硬件标识。
/// </summary>
internal static class SystemInfoLogWriter
{
    private const string WindowsVersionKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private static readonly IAppLog Log = AppLog.For(nameof(SystemInfoLogWriter));

    /// <summary>尽力写入环境头信息；任何单项读取失败都不会影响应用启动。</summary>
    internal static void WriteStartupEnvironment()
    {
        PackageInfo package = ReadPackageInfo();
        WindowsInfo windows = ReadWindowsInfo();

        Log.Information(
            "启动环境：应用版本={AppVersion}，包名称={PackageName}，Windows产品={WindowsProductName}，" +
            "Windows显示版本={WindowsDisplayVersion}，Windows内部版本={WindowsBuild}.{WindowsUbr}，" +
            "进程架构={ProcessArchitecture}，系统架构={OsArchitecture}，.NET={DotNetVersion}，" +
            "WinUI程序集版本={WinUiVersion}，AppLifecycle程序集版本={AppLifecycleVersion}，" +
            "界面区域={Culture}，会话={StartupSessionId}",
            package.Version,
            package.Name,
            windows.ProductName,
            windows.DisplayVersion,
            windows.Build,
            windows.Ubr,
            RuntimeInformation.ProcessArchitecture,
            RuntimeInformation.OSArchitecture,
            RuntimeInformation.FrameworkDescription,
            typeof(Microsoft.UI.Xaml.Application).Assembly.GetName().Version?.ToString() ?? "未知",
            typeof(Microsoft.Windows.AppLifecycle.AppInstance).Assembly.GetName().Version?.ToString() ?? "未知",
            CultureInfo.CurrentUICulture.Name,
            AppLog.SessionId);
    }

    private static PackageInfo ReadPackageInfo()
    {
        try
        {
            PackageId id = Package.Current.Id;
            PackageVersion version = id.Version;
            return new PackageInfo(
                id.Name,
                $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取 MSIX 应用包版本失败，将使用未打包标识");
            return new PackageInfo("未打包", "未打包");
        }
    }

    private static WindowsInfo ReadWindowsInfo()
    {
        return new WindowsInfo(
            ReadRegistryValue("ProductName"),
            ReadRegistryValue("DisplayVersion"),
            ReadRegistryValue("CurrentBuildNumber"),
            ReadRegistryValue("UBR"));
    }

    private static string ReadRegistryValue(string valueName)
    {
        try
        {
            return Registry.GetValue(WindowsVersionKey, valueName, null)?.ToString() ?? "未知";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取 Windows 版本字段失败，字段={ValueName}", valueName);
            return "未知";
        }
    }

    private sealed record PackageInfo(string Name, string Version);

    private sealed record WindowsInfo(string ProductName, string DisplayVersion, string Build, string Ubr);
}
