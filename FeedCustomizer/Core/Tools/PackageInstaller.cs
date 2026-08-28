using FeedCustomizer.Core.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Tools
{
    public class PackageInstaller
    {
        private static string ManifestXmlPath => AppDataPaths.ManifestPath;
        // Registration is an external AppX operation.  Serialize it so a
        // startup refresh, an Apply operation and a toggle cannot overlap and
        // issue competing Remove/Add-AppxPackage commands.
        private static readonly SemaphoreSlim RegistrationGate = new(1, 1);
        private static readonly TimeSpan UninstallPollInterval = TimeSpan.FromMilliseconds(100);
        private const int UninstallPollAttempts = 50;
        private static readonly TimeSpan ProviderProcessExitTimeout = TimeSpan.FromSeconds(3);


        /// <summary>
        /// 安装源提供程序
        /// </summary>
        /// <returns></returns>
        public static async Task<bool> InstallFeedProvider()
        {
            await RegistrationGate.WaitAsync();
            try
            {
                return await InstallFeedProviderCoreAsync();
            }
            finally
            {
                RegistrationGate.Release();
            }
        }

        /// <summary>
        /// 将已经准备好的包内暂存资源发布到真实目录并注册源提供程序。
        /// 开关和应用源配置时调用；资源准备工作由启动流程负责。
        /// </summary>
        public static async Task<bool> InstallFeedProviderFromStagedResources()
        {
            await RegistrationGate.WaitAsync();
            try
            {
                return await InstallFeedProviderFromStagedResourcesCoreAsync();
            }
            finally
            {
                RegistrationGate.Release();
            }
        }

        private static async Task<bool> InstallFeedProviderCoreAsync()
        {
            try
            {
                await ResourcesCopier.EnsureResourcesReadyAsync();

                if (await IsFeedProviderInstalled())
                {
                    if (ResourcesCopier.IsRegisteredProviderCurrent())
                    {
                        return true;
                    }

                    // InstallFeedProvider already owns RegistrationGate, so call
                    // the gate-free core method instead of recursively waiting
                    // on the same semaphore.
                    await UninstallFeedProviderCoreAsync();
                }

                return await PublishAndRegisterProviderAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
                await ShowInstallErrorAsync(ex.ToString(), null, string.Empty);
                return false;
            }
        }

        private static async Task<bool> InstallFeedProviderFromStagedResourcesCoreAsync()
        {
            try
            {
                if (await IsFeedProviderInstalled())
                {
                    await UninstallFeedProviderCoreAsync();
                }
                else
                {
                    await StopProviderProcessesAsync();
                }

                return await PublishAndRegisterProviderAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
                await ShowInstallErrorAsync(ex.ToString(), null, string.Empty);
                return false;
            }
        }

        private static async Task<bool> PublishAndRegisterProviderAsync()
        {
            // 在注册前，将应用包内的文件同步到真实的
            // %LocalAppData%\FeedCustomProvider 路径，避免应用写入被重定向导致
            // Add-AppxPackage 无法访问到真实文件的问题。只复制已修改的文件
            //（使用 robocopy），并在第一次失败时尝试备份模式重试以应对被占用的文件。
            await StopProviderProcessesAsync();
            string realManifestPath = await CopyPackageFilesToRealLocalAppDataAsync();

            var result = await RegisterProviderAsync(realManifestPath);
            if (result.ExitCode == 0)
            {
                return true;
            }

            Debug.WriteLine($"Feed provider registration failed ({result.ExitCode}): {result.Error}");

            if (!IsDeveloperModeError(result))
            {
                await ShowInstallErrorAsync(result.Error, result.ExitCode, result.Output);
                return false;
            }

            // 开发者模式未开启。开关关闭时先询问用户是否启用自动开启，
            // 开关打开或用户同意后，通过单次提权临时开启开发者模式完成注册。
            if (!SettingsLoader.GetAutoEnableDeveloperMode())
            {
                if (!await PromptEnableAutoDeveloperModeAsync())
                {
                    return false;
                }

                SettingsLoader.SetAutoEnableDeveloperMode(true);
            }

            var elevatedResult = await DeveloperModeService.RegisterWithTemporaryDeveloperModeAsync(realManifestPath);
            if (elevatedResult.ExitCode == 0)
            {
                return true;
            }

            if (elevatedResult.ExitCode == DeveloperModeService.ElevationCancelledExitCode)
            {
                await ShowDeveloperModeElevationCancelledAsync();
            }
            else
            {
                await ShowInstallErrorAsync(elevatedResult.Error, elevatedResult.ExitCode, elevatedResult.Output);
            }

            return false;
        }

        private static string BuildRegistrationCommand(string escapedManifestPath)
        {
            return
                "$ErrorActionPreference = 'Stop'; " +
                $"Add-AppxPackage -Register -ForceApplicationShutdown -ErrorAction Stop '{escapedManifestPath}'";
        }

        private static async Task<PowerShellResult> RegisterProviderAsync(string manifestPath)
        {
            string escapedManifestPath = manifestPath.Replace("'", "''");
            return await RunPowerShellViaProcess(BuildRegistrationCommand(escapedManifestPath));
        }

        private static bool IsDeveloperModeError(PowerShellResult result) =>
            (result.Error?.Contains("0x80073CFF", StringComparison.OrdinalIgnoreCase) == true) ||
            (result.Output?.Contains("0x80073CFF", StringComparison.OrdinalIgnoreCase) == true);

        private static async Task<bool> PromptEnableAutoDeveloperModeAsync()
        {
            var resourceLoader = new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader();
            return await DialogService.ShowConfirmAsync(
                resourceLoader.GetString("EnableProviderFail"),
                resourceLoader.GetString("DeveloperModeDisabled"),
                resourceLoader.GetString("EnableAutoDeveloperMode"),
                resourceLoader.GetString("DialogCancel"));
        }

        private static Task ShowDeveloperModeElevationCancelledAsync()
        {
            var resourceLoader = new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader();
            return DialogService.ShowMessageAsync(
                resourceLoader.GetString("EnableProviderFail"),
                resourceLoader.GetString("DeveloperModeElevationCancelled"),
                resourceLoader.GetString("DialogOK"));
        }


        /// <summary>
        /// 卸载源提供程序
        /// </summary>
        /// <returns></returns>
        public static async Task UninstallFeedProvider()
        {
            await RegistrationGate.WaitAsync();
            try
            {
                await UninstallFeedProviderCoreAsync();
            }
            finally
            {
                RegistrationGate.Release();
            }
        }

        private static async Task UninstallFeedProviderCoreAsync()
        {
            // Remove-AppxPackage does not reliably terminate a classic COM
            // local server.  If the old FeedProvider.exe remains registered,
            // the next Add-AppxPackage can succeed while Widgets still talks
            // to the stale process, which presents as a switch with no feeds
            // after the first toggle.
            await StopProviderProcessesAsync();

            if (!await IsFeedProviderInstalled())
            {
                return;
            }

            var result = await RunPowerShellViaProcess(
                "Get-AppxPackage -Name '*D454B137.Jianmao.FeedCustomizerContainer*' | Remove-AppxPackage");
            if (result.ExitCode != 0)
            {
                Debug.WriteLine($"Feed provider removal failed ({result.ExitCode}): {result.Error}");
            }

            // Remove-AppxPackage can return before package registration is fully
            // gone. Do not let a subsequent Add-AppxPackage race that cleanup;
            // it can leave stale registration state and repeated deployment
            // cleanup events.
            for (int attempt = 0; attempt < UninstallPollAttempts; attempt++)
            {
                if (!await IsFeedProviderInstalled())
                {
                    // Widgets can reactivate the local server in the small
                    // window between the first process stop and package
                    // removal. Once registration is gone, terminate that last
                    // stale instance so the next registration cannot bind to
                    // the previous binary.
                    await StopProviderProcessesAsync();
                    return;
                }

                await Task.Delay(UninstallPollInterval);
            }

            Debug.WriteLine("Feed provider removal did not disappear from Get-AppxPackage within the wait window.");
            await StopProviderProcessesAsync();
        }

        private static async Task StopProviderProcessesAsync()
        {
            string sourceProviderRoot = Path.GetFullPath(Path.Combine(
                AppDataPaths.FeedProviderFolder,
                "FeedProvider"));

            foreach (Process process in Process.GetProcessesByName("FeedProvider"))
            {
                string? executablePath = null;
                try
                {
                    executablePath = process.MainModule?.FileName;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"无法读取 FeedProvider 进程 {process.Id} 的路径：{ex.Message}");
                }

                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    process.Dispose();
                    continue;
                }

                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(executablePath);
                }
                catch
                {
                    process.Dispose();
                    continue;
                }

                bool isOurProvider =
                    fullPath.StartsWith(sourceProviderRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                if (!isOurProvider)
                {
                    process.Dispose();
                    continue;
                }

                try
                {
                    if (!process.HasExited)
                    {
                        // FeedProvider is a single-process COM local server and
                        // does not spawn children. Kill(entireProcessTree: true)
                        // asks Process to inspect every process while building a
                        // descendant tree; protected system processes can make
                        // that scan throw Win32Exception (access denied), even
                        // though this FeedProvider process is terminable.
                        process.Kill();
                        // WaitForExitAsync enables exit events after Kill. If
                        // the process exits in that small window, it throws
                        // InvalidOperationException while reopening the handle.
                        // The synchronous overload deliberately tolerates an
                        // already-exited process and still gives us a timeout.
                        await Task.Run(() =>
                            process.WaitForExit((int)ProviderProcessExitTimeout.TotalMilliseconds));
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"停止旧 FeedProvider 进程 {process.Id} 失败：{ex.Message}");
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        /// <summary>
        /// 检查源提供程序是否已经安装
        /// </summary>
        /// <returns></returns>
        public static async Task<bool> IsFeedProviderInstalled()
        {
            var result = await RunPowerShellViaProcess(
                "Get-AppxPackage -Name '*D454B137.Jianmao.FeedCustomizerContainer*' | Select-Object -ExpandProperty PackageFullName");
            Debug.WriteLine(result.Output);
            return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output);
        }

        // 将包内已准备好的用户文件从应用私有目录同步到真正的
        // %LocalAppData%\FeedCustomProvider 并返回目标清单路径。使用
        // robocopy 只复制已更改的文件，遇到失败时尝试备份模式重试。
        private static async Task<string> CopyPackageFilesToRealLocalAppDataAsync()
        {
            // 源目录为应用的私有目录（包内的 LocalCache/Local/FeedCustomProvider），
            // 而不是全局 LocalAppData 路径。
            string source = AppDataPaths.PackageLocalFeedProviderFolder;
            string dest = AppDataPaths.FeedProviderFolder;

            // 构造 PowerShell 命令，使用 /COPY:DAT 只复制数据/属性/时间戳，避免复制审计信息导致权限错误
            string BuildRobocopyCommand(string extraOptions) =>
                "$src = \"" + source.Replace("\"", "\\\"") + "\"; " +
                "$dst = \"" + dest.Replace("\"", "\\\"") + "\"; " +
                "if (-not (Test-Path -Path $dst)) { New-Item -ItemType Directory -Path $dst | Out-Null }; " +
                "robocopy \"$src\" \"$dst\" /E /COPY:DAT /R:3 /W:1 /MT:8 " + extraOptions + "; exit $LASTEXITCODE";

            string cmd = BuildRobocopyCommand(string.Empty);
            if (!Directory.Exists(source))
            {
                throw new DirectoryNotFoundException($"源目录不存在：{source}");
            }

            // 如果源与目标路径相同，则跳过复制
            string normSource = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normDest = Path.GetFullPath(dest).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(normSource, normDest, StringComparison.OrdinalIgnoreCase))
            {
                Debug.WriteLine($"源与目标路径相同，跳过复制：{normSource}");
                string manifestPathSame = Path.Combine(dest, "AppxManifest.xml");
                if (!File.Exists(manifestPathSame))
                {
                    throw new FileNotFoundException($"目标路径缺少清单文件：{manifestPathSame}");
                }

                return manifestPathSame;
            }

            var result = await RunPowerShellViaProcess(cmd);

            // Robocopy 返回值小于 8 表示成功或轻微问题，>=8 表示失败
            if (result.ExitCode >= 8)
            {
                Debug.WriteLine($"robocopy first attempt failed ({result.ExitCode}). Output: {result.Output}. Error: {result.Error}");
                // 备份模式尝试覆盖被占用文件（需要权限），再次采纳返回码
                string cmd2 = BuildRobocopyCommand("/B");
                var result2 = await RunPowerShellViaProcess(cmd2);
                if (result2.ExitCode >= 8)
                {
                    Debug.WriteLine($"robocopy backup attempt failed ({result2.ExitCode}). Output: {result2.Output}. Error: {result2.Error}");
                    throw new IOException($"无法将包文件复制到 {dest}，robocopy 出错：第一次尝试: " +
                        $"ExitCode={result.ExitCode}; Output={result.Output}; Error={result.Error} || 第二次尝试: " +
                        $"ExitCode={result2.ExitCode}; Output={result2.Output}; Error={result2.Error}");
                }
            }

            string manifestPath = Path.Combine(dest, "AppxManifest.xml");
            if (!File.Exists(manifestPath))
            {
                throw new FileNotFoundException($"目标路径缺少清单文件：{manifestPath}");
            }

            return manifestPath;
        }

        private static Task ShowInstallErrorAsync(string? error, int? exitCode, string output)
        {
            var resourceLoader = new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader();
            string content = resourceLoader.GetString("SomethingErrorsOccurred");

            string details = string.Join(
                Environment.NewLine,
                $"ExitCode: {(exitCode?.ToString() ?? "n/a")}",
                $"SourceManifest: {ManifestXmlPath}",
                $"Error: {error ?? string.Empty}",
                $"Output: {output}");

            Debug.WriteLine($"Feed provider error ({exitCode}): {details}");

            if (App.MainWindow is MainWindow)
            {
                string title = resourceLoader.GetString("EnableProviderFail");
                _ = DialogService.ShowStartupFailureAsync(title, $"{content}{Environment.NewLine}{Environment.NewLine}{details}");
            }

            return Task.CompletedTask;
        }

        private static async Task<PowerShellResult> RunPowerShellViaProcess(string command)
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(command);

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return new PowerShellResult(-1, string.Empty, "无法启动 PowerShell。");
            }

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new PowerShellResult(process.ExitCode, await outputTask, await errorTask);
        }

    }
}
