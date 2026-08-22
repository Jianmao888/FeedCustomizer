using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;

namespace FeedCustomizer.Core.Tools
{
    public class PackageInstaller
    {
        private static string ManifestXmlPath => AppDataPaths.ManifestPath;


        /// <summary>
        /// 安装源提供程序
        /// </summary>
        /// <returns></returns>
        public static async Task<bool> InstallFeedProvider()
        {
            try
            {
                // Always perform this preflight. Cached page state can bypass the
                // normal first-run copy path, but registration still requires the
                // local manifest and the current architecture provider.
                await Task.Run(async () => await ResourcesCopier.EnsureResourcesReadyAsync());

                // Re-serialize existing definitions before registration so manifests
                // created by older versions receive the current feed schema and
                // icon paths.
                var feeds = await ManifestXmlService.Read();
                await ManifestXmlService.Write(feeds);

                if (await IsFeedProviderInstalled())
                {
                    if (await Task.Run(ResourcesCopier.IsRegisteredProviderCurrent))
                    {
                        return true;
                    }

                    await UninstallFeedProvider();
                }

                string escapedSourceFolder = AppDataPaths.FeedProviderFolder.Replace("'", "''");
                string escapedRegistrationFolder = AppDataPaths.RegistrationFolder.Replace("'", "''");
                string escapedRegistrationManifestPath = AppDataPaths.RegistrationManifestPath.Replace("'", "''");
                var result = await RunPowerShellViaProcess(
                    $"$source = '{escapedSourceFolder}'; " +
                    $"$target = '{escapedRegistrationFolder}'; " +
                    "New-Item -ItemType Directory -Force -Path $target -ErrorAction Stop | Out-Null; " +
                    "& robocopy.exe $source $target /MIR /COPY:DT /DCOPY:T /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null; " +
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
            if (await IsFeedProviderInstalled())
            {
                var result = await RunPowerShellViaProcess(
                    "Get-AppxPackage -Name '*D454B137.Jianmao.FeedCustomizerContainer*' | Remove-AppxPackage");
                if (result.ExitCode != 0)
                {
                    Debug.WriteLine($"Feed provider removal failed ({result.ExitCode}): {result.Error}");
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

        private static async Task ShowInstallErrorAsync(string? error, int? exitCode, string output)
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
                await window.ShowStartupFailureDialogAsync(
                    resourceLoader.GetString("EnableProviderFail"),
                    $"{content}{Environment.NewLine}{Environment.NewLine}{details}");
            }
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
