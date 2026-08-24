using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

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

        private static async Task<bool> InstallFeedProviderCoreAsync()
        {
            try
            {
                // Always perform this preflight. Cached page state can bypass the
                // normal first-run copy path, but registration still requires the
                // local manifest and the current architecture provider.
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

                string escapedSourceFolder = AppDataPaths.FeedProviderFolder.Replace("'", "''");
                string escapedRegistrationFolder = AppDataPaths.RegistrationFolder.Replace("'", "''");
                string escapedRegistrationManifestPath = AppDataPaths.RegistrationManifestPath.Replace("'", "''");
                string escapedSourceProviderFolder = Path.Combine(
                    AppDataPaths.FeedProviderFolder,
                    "FeedProvider").Replace("'", "''");
                string escapedRegistrationProviderFolder = Path.Combine(
                    AppDataPaths.RegistrationFolder,
                    "FeedProvider").Replace("'", "''");
                await StopProviderProcessesAsync();
                var result = await RunPowerShellViaProcess(
                    $"$source = '{escapedSourceFolder}'; " +
                    $"$target = '{escapedRegistrationFolder}'; " +
                    $"$providerSource = '{escapedSourceProviderFolder}'; " +
                    $"$providerTarget = '{escapedRegistrationProviderFolder}'; " +
                    "New-Item -ItemType Directory -Force -Path $target -ErrorAction Stop | Out-Null; " +
                    // Mirror only FeedProvider. The root also contains the
                    // manifest, definitions and downloaded images, which must
                    // never be deleted during a provider refresh.
                    "& robocopy.exe $providerSource $providerTarget /MIR /COPY:DT /DCOPY:T /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null; " +
                    "$providerCopyExitCode = $LASTEXITCODE; " +
                    "if ($providerCopyExitCode -gt 7) { throw \"源提供程序同步失败，Robocopy ExitCode: $providerCopyExitCode\" }; " +
                    "& robocopy.exe $source $target /E /COPY:DT /DCOPY:T /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null; " +
                    "$copyExitCode = $LASTEXITCODE; " +
                    "if ($copyExitCode -gt 7) { throw \"资源同步失败，Robocopy ExitCode: $copyExitCode\" }; " +
                    $"Add-AppxPackage -Register -ForceApplicationShutdown -ErrorAction Stop '{escapedRegistrationManifestPath}'");

                if (result.ExitCode != 0)
                {
                    Debug.WriteLine($"Feed provider registration failed ({result.ExitCode}): {result.Error}");
                    await ShowInstallErrorAsync(result.Error, result.ExitCode, result.Output);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
                await ShowInstallErrorAsync(ex.ToString(), null, string.Empty);
                return false;
            }
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
            string registrationProviderRoot = Path.GetFullPath(Path.Combine(
                AppDataPaths.RegistrationFolder,
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
                    fullPath.StartsWith(sourceProviderRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    fullPath.StartsWith(registrationProviderRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                if (!isOurProvider)
                {
                    process.Dispose();
                    continue;
                }

                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        Task waitTask = process.WaitForExitAsync();
                        await Task.WhenAny(waitTask, Task.Delay(ProviderProcessExitTimeout));
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

        private static Task ShowInstallErrorAsync(string? error, int? exitCode, string output)
        {
            var resourceLoader = new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader();
            string content = resourceLoader.GetString("SomethingErrorsOccurred");
            if (error?.Contains("0x80073CFF", StringComparison.OrdinalIgnoreCase) == true)
            {
                content = resourceLoader.GetString("DeveloperModeDisabled");
            }

            string details = string.Join(
                Environment.NewLine,
                $"ExitCode: {(exitCode?.ToString() ?? "n/a")}",
                $"SourceManifest: {ManifestXmlPath}",
                $"RegistrationManifest: {AppDataPaths.RegistrationManifestPath}",
                $"Error: {error ?? string.Empty}",
                $"Output: {output}");

            Debug.WriteLine($"Feed provider error ({exitCode}): {details}");

            if (App.MainWindow is MainWindow window)
            {
                _ = window.ShowStartupFailureDialogAsync(
                    resourceLoader.GetString("EnableProviderFail"),
                    $"{content}{Environment.NewLine}{Environment.NewLine}{details}");
            }

            return Task.CompletedTask;
        }

        private sealed record PowerShellResult(int ExitCode, string Output, string Error);

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
