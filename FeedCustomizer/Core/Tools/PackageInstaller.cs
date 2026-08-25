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

        private static async Task<bool> InstallFeedProviderCoreAsync()
        {
            try
            {
                // Always perform this preflight. Cached page state can bypass the
                // normal first-run copy path, but registration still requires the
                // local manifest and the current architecture provider.
                await MigrateLegacyCacheAsync();
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

                string escapedRegistrationManifestPath = AppDataPaths.RegistrationManifestPath.Replace("'", "''");
                await StopProviderProcessesAsync();
                var result = await RunPowerShellViaProcess(BuildRegistrationCommand(
                    escapedRegistrationManifestPath));

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

        internal static async Task MigrateLegacyCacheAsync()
        {
            string source = AppDataPaths.FeedProviderFolder.Replace("'", "''");
            string legacy = AppDataPaths.LegacyFeedProviderFolder.Replace("'", "''");
            string migrationFlag = Path.Combine(
                AppDataPaths.FeedProviderFolder,
                ".legacy_cache_migrated").Replace("'", "''");

            string command =
                "$ErrorActionPreference = 'Stop'; " +
                $"$source = '{source}'; " +
                $"$legacy = '{legacy}'; " +
                $"$migrationFlag = '{migrationFlag}'; " +
                "function Copy-PlainFile([string]$sourcePath, [string]$destinationPath) { " +
                "  $parent = Split-Path -Parent $destinationPath; " +
                "  if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null } " +
                "  $bytes = [System.IO.File]::ReadAllBytes($sourcePath); " +
                "  [System.IO.File]::WriteAllBytes($destinationPath, $bytes); " +
                "} " +
                "if (-not (Test-Path -LiteralPath $migrationFlag)) { " +
                "  $sourceManifest = Join-Path $source 'AppxManifest.xml'; " +
                "  $legacyManifest = Join-Path $legacy 'AppxManifest.xml'; " +
                "  $sourceDefinitions = 0; $legacyDefinitions = 0; " +
                "  $sourceReadable = -not (Test-Path -LiteralPath $sourceManifest); " +
                "  $legacyReadable = -not (Test-Path -LiteralPath $legacyManifest); " +
                "  if (Test-Path -LiteralPath $sourceManifest) { " +
                "    try { $sourceXml = [xml](Get-Content -LiteralPath $sourceManifest -Raw); " +
                "      $sourceDefinitions = @($sourceXml.SelectNodes(\"//*[local-name()='Definition']\")).Count; $sourceReadable = $true " +
                "    } catch { } " +
                "  } " +
                "  if (Test-Path -LiteralPath $legacyManifest) { " +
                "    try { $legacyXml = [xml](Get-Content -LiteralPath $legacyManifest -Raw); " +
                "      $legacyDefinitions = @($legacyXml.SelectNodes(\"//*[local-name()='Definition']\")).Count; $legacyReadable = $true " +
                "    } catch { } " +
                "  } " +
                "  if ($sourceDefinitions -eq 0 -and $legacyDefinitions -gt 0) { " +
                "    New-Item -ItemType Directory -Force -Path $source -ErrorAction Stop | Out-Null; " +
                "    Copy-PlainFile -sourcePath $legacyManifest -destinationPath $sourceManifest; " +
                "  } " +
                "  $legacyImages = Join-Path $legacy 'Images'; " +
                "  $sourceImages = Join-Path $source 'Images'; " +
                "  if (Test-Path -LiteralPath $legacyImages) { " +
                "    Get-ChildItem -LiteralPath $legacyImages -File -Recurse | ForEach-Object { " +
                "      $relative = $_.FullName.Substring($legacyImages.Length).TrimStart('\\'); " +
                "      $destination = Join-Path $sourceImages $relative; " +
                "      if (-not (Test-Path -LiteralPath $destination)) { " +
                "        $parent = Split-Path -Parent $destination; " +
                "        New-Item -ItemType Directory -Force -Path $parent | Out-Null; " +
                "        Copy-PlainFile -sourcePath $_.FullName -destinationPath $destination; " +
                "      } " +
                "    } " +
                "  } " +
                "  if ($sourceReadable -and $legacyReadable) { " +
                "    New-Item -ItemType File -Force -Path $migrationFlag -ErrorAction Stop | Out-Null " +
                "  } " +
                "}";

            PowerShellResult result = await RunPowerShellViaProcess(command);
            if (result.ExitCode != 0)
            {
                // Migration is best-effort. The canonical LocalCache manifest
                // remains untouched on failure, and registration will report a
                // normal deployment error if its required files are missing.
                Debug.WriteLine($"Legacy feed cache migration failed ({result.ExitCode}): {result.Error}");
            }
        }

        private static string BuildRegistrationCommand(string escapedRegistrationManifestPath)
        {
            string source = AppDataPaths.FeedProviderFolder.Replace("'", "''");
            string target = AppDataPaths.RegistrationFolder.Replace("'", "''");

            return
                "$ErrorActionPreference = 'Stop'; " +
                $"$source = '{source}'; " +
                $"$target = '{target}'; " +
                "$providerTarget = Join-Path $target 'FeedProvider'; " +
                "function Copy-PlainFile([string]$sourcePath, [string]$destinationPath) { " +
                "  $parent = Split-Path -Parent $destinationPath; " +
                "  if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null } " +
                "  if (Test-Path -LiteralPath $destinationPath) { " +
                "    [System.IO.File]::SetAttributes($destinationPath, [System.IO.FileAttributes]::Normal); " +
                "  } " +
                "  $bytes = [System.IO.File]::ReadAllBytes($sourcePath); " +
                "  [System.IO.File]::WriteAllBytes($destinationPath, $bytes); " +
                "} " +
                "New-Item -ItemType Directory -Force -Path $target -ErrorAction Stop | Out-Null; " +
                "if (Test-Path -LiteralPath $providerTarget) { " +
                "  $removed = $false; $lastError = $null; " +
                "  for ($attempt = 0; $attempt -lt 4; $attempt++) { " +
                "    try { Remove-Item -LiteralPath $providerTarget -Recurse -Force -ErrorAction Stop; $removed = $true; break } " +
                "    catch { $lastError = $_; Start-Sleep -Milliseconds 150 } " +
                "  } " +
                "  if (-not $removed) { throw $lastError } " +
                "} " +
                "$sourceLength = $source.Length; " +
                "Get-ChildItem -LiteralPath $source -File -Recurse -Force | ForEach-Object { " +
                "  $relative = $_.FullName.Substring($sourceLength).TrimStart('\\'); " +
                "  $destination = Join-Path $target $relative; " +
                "  $copied = $false; $lastError = $null; " +
                "  for ($attempt = 0; $attempt -lt 4; $attempt++) { " +
                "    try { " +
                "      Copy-PlainFile -sourcePath $_.FullName -destinationPath $destination; " +
                "      $copied = $true; break " +
                "    } catch { $lastError = $_; Start-Sleep -Milliseconds 150 } " +
                "  } " +
                "  if (-not $copied) { throw $lastError } " +
                "}; " +
                $"Add-AppxPackage -Register -ForceApplicationShutdown -ErrorAction Stop '{escapedRegistrationManifestPath}'";
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
            string legacyProviderRoot = Path.GetFullPath(Path.Combine(
                AppDataPaths.LegacyFeedProviderFolder,
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
                    fullPath.StartsWith(registrationProviderRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    fullPath.StartsWith(legacyProviderRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
