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
        // 注册操作是外部的 AppX 操作。对其进行序列化，
        // 防止启动时的刷新、用户的应用（Apply）操作和切换操作重叠，
        // 导致同时发出竞争性的 Remove/Add-AppxPackage 命令。
        //
        // 额外注释：使用 SemaphoreSlim 作为全局门（RegistrationGate）可以
        // 确保一次只有一个注册/卸载流程在运行，避免并发导致的不确定性
        //（比如部分注册残留、文件被占用或注册冲突）。
        private static readonly SemaphoreSlim RegistrationGate = new(1, 1);
        private static readonly TimeSpan UninstallPollInterval = TimeSpan.FromMilliseconds(100);
        private const int UninstallPollAttempts = 50;
        private static readonly TimeSpan ProviderProcessExitTimeout = TimeSpan.FromSeconds(3);


        /// <summary>
        /// 安装源提供程序
        /// </summary>
        /// <returns></returns>
        public static async Task<ProviderRegistrationResult> InstallFeedProvider()
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
        public static async Task<ProviderRegistrationResult> InstallFeedProviderFromStagedResources()
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

        private static async Task<ProviderRegistrationResult> InstallFeedProviderCoreAsync()
        {
            try
            {
                await ResourcesCopier.EnsureResourcesReadyAsync();

                if (await IsFeedProviderInstalled())
                {
                    if (ResourcesCopier.IsRegisteredProviderCurrent())
                    {
                        return ProviderRegistrationResult.Success(ManifestXmlPath);
                    }

                    // InstallFeedProvider 已经持有 RegistrationGate（信号量），
                    // 因此直接调用不再等待信号量的核心卸载方法，
                    // 避免在同一信号量上递归等待导致死锁或不必要的延迟。
                    //
                    // 额外注释：这保证了在持有锁的上下文内，我们不会再次执行
                    // 会尝试 WaitAsync 的方法，从而保持锁的正确释放顺序。
                    await UninstallFeedProviderCoreAsync();
                }

                return await PublishAndRegisterProviderAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
                return ProviderRegistrationResult.Failed(
                    ManifestXmlPath,
                    null,
                    ex.ToString(),
                    string.Empty);
            }
        }

        private static async Task<ProviderRegistrationResult> InstallFeedProviderFromStagedResourcesCoreAsync()
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
                return ProviderRegistrationResult.Failed(
                    ManifestXmlPath,
                    null,
                    ex.ToString(),
                    string.Empty);
            }
        }

        private static async Task<ProviderRegistrationResult> PublishAndRegisterProviderAsync()
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
                return ProviderRegistrationResult.Success(realManifestPath);
            }

            Debug.WriteLine($"Feed provider registration failed ({result.ExitCode}): {result.Error}");

            if (!IsDeveloperModeError(result))
            {
                return ProviderRegistrationResult.Failed(
                    realManifestPath,
                    result.ExitCode,
                    result.Error,
                    result.Output);
            }

            // 开发者模式未开启时，由调用方决定是否向用户请求授权；基础设施层不显示 UI。
            if (!SettingsLoader.GetAutoEnableDeveloperMode())
            {
                return new ProviderRegistrationResult(
                    ProviderRegistrationStatus.DeveloperModeConfirmationRequired,
                    result.ExitCode,
                    result.Error,
                    result.Output,
                    realManifestPath);
            }

            var elevatedResult = await DeveloperModeService.RegisterWithTemporaryDeveloperModeAsync(realManifestPath);
            if (elevatedResult.ExitCode == 0)
            {
                return ProviderRegistrationResult.Success(realManifestPath);
            }

            if (elevatedResult.ExitCode == DeveloperModeService.ElevationCancelledExitCode)
            {
                return new ProviderRegistrationResult(
                    ProviderRegistrationStatus.ElevationCancelled,
                    elevatedResult.ExitCode,
                    elevatedResult.Error,
                    elevatedResult.Output,
                    realManifestPath);
            }

            return ProviderRegistrationResult.Failed(
                realManifestPath,
                elevatedResult.ExitCode,
                elevatedResult.Error,
                elevatedResult.Output);
        }

        // 构建用于在 PowerShell 中注册 Appx 包的命令字符串。
        // 参数 escapedManifestPath 应当已经对单引号进行重复转义（' -> ''），
        // 以便安全地嵌入到单引号包裹的 PowerShell 字面量中。
        //
        // 额外注释：命令中使用 -Register 来注册清单，
        // -ForceApplicationShutdown 尝试关闭使用该包的应用以便顺利注册，
        // 并设置 $ErrorActionPreference 以便出现错误时抛出异常并由调用方处理。
        private static string BuildRegistrationCommand(string escapedManifestPath)
        {
            return
                "$ErrorActionPreference = 'Stop'; " +
                $"Add-AppxPackage -Register -ForceApplicationShutdown -ErrorAction Stop '{escapedManifestPath}'";
        }

        // 使用独立进程运行 PowerShell 命令来执行注册，避免在当前进程中直接调用 PowerShell API
        // 以减少环境副作用。manifestPath 中的单引号会被替换为两个单引号以进行 PowerShell 字符串转义。
        private static async Task<PowerShellResult> RegisterProviderAsync(string manifestPath)
        {
            string escapedManifestPath = manifestPath.Replace("'", "''");
            return await RunPowerShellViaProcess(BuildRegistrationCommand(escapedManifestPath));
        }

        // 检查 PowerShell 返回结果中是否包含表示未启用开发者模式的错误代码（0x80073CFF）。
        // 如果在标准错误或标准输出中发现该代码，则视为开发者模式相关的错误。
        private static bool IsDeveloperModeError(PowerShellResult result) =>
            (result.Error?.Contains("0x80073CFF", StringComparison.OrdinalIgnoreCase) == true) ||
            (result.Output?.Contains("0x80073CFF", StringComparison.OrdinalIgnoreCase) == true);

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
            // Remove-AppxPackage 并不总是可靠地终止传统的 COM 本地服务器（classic COM local server）。
            // 如果旧的 FeedProvider.exe 仍然被注册，下一次 Add-AppxPackage 可能会成功，
            // 但 Widgets 仍然会与旧的进程通信，导致首次切换后出现没有 feeds 的情况。
            //
            // 额外注释：因此在卸载流程中，需要额外采取措施确保旧进程被终止，
            // 并且在注册新的包之前不会与旧实例发生绑定或通信。
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

            // Remove-AppxPackage 可能在 package 注册完全移除之前就返回。
            // 不要让随后的 Add-AppxPackage 与该清理操作并发发生竞速；
            // 否则可能留下过时的注册状态并触发重复的部署清理事件。
            //
            // 额外注释：用轮询或等待机制确保注册完全移除后再继续下一步注册，
            // 可以降低残留注册带来的不可预测问题。
            for (int attempt = 0; attempt < UninstallPollAttempts; attempt++)
            {
                if (!await IsFeedProviderInstalled())
                {
                    // 在第一次进程停止与包移除之间的短时间窗口内，
                    // Widgets 可能会重新激活本地服务器（local server）。
                    // 一旦注册被移除，务必终止最后存留的旧实例，
                    // 以防下一次注册绑定到之前的二进制文件。
                    //
                    // 额外注释：这一步通常在卸载流程后立即执行，通过查找
                    // 并终止残留进程来保证下次安装/注册的干净环境。
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
                        // FeedProvider 是单进程的 COM 本地服务器（不会派生子进程）。
                        // 对整个进程树执行 Kill(entireProcessTree: true) 会要求
                        // 遍历所有进程以构建子孙树；某些受保护的系统进程
                        // 在遍历时可能会抛出 Win32Exception（访问被拒绝），
                        // 即使目标 FeedProvider 进程本身是可终止的。
                        //
                        // 因此我们选择直接终止该进程本身，以避免在构建进程树
                        // 时遇到权限或访问冲突的问题。
                        process.Kill();
                        // WaitForExitAsync 在 Kill 之后会启用退出事件。如果在
                        // 这个很短的时间窗口内进程已经退出，再去重新打开句柄
                        // 会导致 InvalidOperationException。使用同步的 WaitForExit
                        // 重载可以容忍进程已退出的情况，并为我们提供一个超时保障。
                        //
                        // 额外注释：这里刻意选择不会遍历整棵进程树的终止方式，
                        // 并在容错处理上偏向可靠的同步等待以避免竞态异常或
                        // 因句柄重入导致的异常，从而保证卸载/重新注册流程的稳定性。
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
                "robocopy \"$src\" \"$dst\" /E /COPY:DAT /R:0 /W:0 /MT:8 " + extraOptions + "; exit $LASTEXITCODE";

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

                // 如果因为文件被占用导致复制失败，最常见的被占用文件是 dest 下的 resources.pri。
                // 与其反复尝试 robocopy 的备份模式，不如直接尝试删除该文件再重试一次复制；
                // 如果 targeted 删除无效，再尝试强制删除目标目录内容后再重试一次。

                string priPath = Path.Combine(dest, "resources.pri");

                // 尝试有针对性地删除 resources.pri
                try
                {
                    string removePriCmd = "$path = \"" + priPath.Replace("\"", "\\\"") + "\"; " +
                        "if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }; exit 0";
                    await RunPowerShellViaProcess(removePriCmd);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"尝试删除 resources.pri 时发生错误：{ex}");
                }

                // 再次尝试一次 robocopy
                var retryResult = await RunPowerShellViaProcess(cmd);
                if (retryResult.ExitCode < 8)
                {
                    result = retryResult;
                }
                else
                {
                    Debug.WriteLine($"robocopy retry after deleting resources.pri failed ({retryResult.ExitCode}). Output: {retryResult.Output}. Error: {retryResult.Error}");

                    // 作为最终手段，强制删除目标目录下的所有内容后再重试一次
                    try
                    {
                        string removeAllCmd = "$dst = \"" + dest.Replace("\"", "\\\"") + "\"; " +
                            "if (Test-Path -Path $dst) { Remove-Item -Path (Join-Path $dst '*') -Force -Recurse -ErrorAction SilentlyContinue }; exit 0";
                        await RunPowerShellViaProcess(removeAllCmd);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"尝试强制清空目标目录时发生错误：{ex}");
                    }

                    // 最后一次尝试
                    var finalResult = await RunPowerShellViaProcess(cmd);
                    if (finalResult.ExitCode >= 8)
                    {
                        Debug.WriteLine($"robocopy final attempt failed ({finalResult.ExitCode}). Output: {finalResult.Output}. Error: {finalResult.Error}");
                        throw new IOException($"无法将包文件复制到 {dest}，robocopy 多次尝试失败：第一次: " +
                            $"ExitCode={result.ExitCode}; Output={result.Output}; Error={result.Error} || 重试: " +
                            $"ExitCode={retryResult.ExitCode}; Output={retryResult.Output}; Error={retryResult.Error} || 最后一次: " +
                            $"ExitCode={finalResult.ExitCode}; Output={finalResult.Output}; Error={finalResult.Error}");
                    }

                    result = finalResult;
                }
            }

            string manifestPath = Path.Combine(dest, "AppxManifest.xml");
            if (!File.Exists(manifestPath))
            {
                throw new FileNotFoundException($"目标路径缺少清单文件：{manifestPath}");
            }

            return manifestPath;
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
