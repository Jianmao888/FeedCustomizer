using FeedCustomizer.Core.Models;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Infrastructure.PowerShell
{
    /// <summary>
    /// 通过独立 powershell.exe 进程执行脚本，统一负责临时文件、提权、输出捕获、
    /// 超时、取消和清理。所有调用均使用脚本文件，不再向 -Command 拼接业务命令。
    /// </summary>
    internal sealed class PowerShellProcessExecutor : IPowerShellExecutor
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

        public async Task<PowerShellResult> ExecuteAsync(
            PowerShellScript script,
            CancellationToken cancellationToken = default)
        {
            ValidateScriptFileName(script.FileName);

            string tempDirectory = Path.Combine(
                Path.GetTempPath(),
                $"FeedCustomizer_PowerShell_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirectory);

            string scriptPath = Path.Combine(tempDirectory, script.FileName);
            string outputPath = Path.Combine(tempDirectory, "stdout.txt");
            string errorPath = Path.Combine(tempDirectory, "stderr.txt");
            string scriptWithContext = string.Join(
                Environment.NewLine,
                $"$outputPath = {PowerShellLiteral.Quote(outputPath)}",
                $"$errorPath = {PowerShellLiteral.Quote(errorPath)}",
                script.Content);

            try
            {
                // Windows PowerShell 5.1 需要 BOM 才能稳定识别包含非 ASCII 路径的 UTF-8 脚本。
                await File.WriteAllTextAsync(
                    scriptPath,
                    scriptWithContext,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                    cancellationToken);
                return script.RequiresElevation
                    ? await ExecuteElevatedAsync(scriptPath, outputPath, errorPath, script.Timeout, cancellationToken)
                    : await ExecuteStandardAsync(scriptPath, outputPath, errorPath, script.Timeout, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new PowerShellResult(
                    PowerShellExitCodes.Cancelled,
                    string.Empty,
                    "PowerShell 操作已取消。");
            }
            finally
            {
                await TryDeleteDirectoryAsync(tempDirectory);
            }
        }

        private static async Task<PowerShellResult> ExecuteStandardAsync(
            string scriptPath,
            string outputPath,
            string errorPath,
            TimeSpan? timeout,
            CancellationToken cancellationToken)
        {
            ProcessStartInfo startInfo = CreateStartInfo(scriptPath, requiresElevation: false);
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return new PowerShellResult(
                    PowerShellExitCodes.StartFailed,
                    string.Empty,
                    "无法启动 PowerShell。");
            }

            Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            PowerShellResult? interruptedResult = await WaitForExitAsync(
                process,
                timeout,
                cancellationToken);
            if (interruptedResult is not null)
            {
                // 进程已因取消或超时被终止，此时不等待可能永远无法结束的输出读取任务。
                return interruptedResult;
            }

            string standardOutput = await standardOutputTask;
            string standardError = await standardErrorTask;
            return new PowerShellResult(
                process.ExitCode,
                JoinOutput(standardOutput, await ReadAllTextIfExistsAsync(outputPath)),
                JoinOutput(standardError, await ReadAllTextIfExistsAsync(errorPath)));
        }

        private static async Task<PowerShellResult> ExecuteElevatedAsync(
            string scriptPath,
            string outputPath,
            string errorPath,
            TimeSpan? timeout,
            CancellationToken cancellationToken)
        {
            try
            {
                ProcessStartInfo startInfo = CreateStartInfo(scriptPath, requiresElevation: true);
                using Process? process = Process.Start(startInfo);
                if (process is null)
                {
                    return new PowerShellResult(
                        PowerShellExitCodes.StartFailed,
                        string.Empty,
                        "无法启动管理员 PowerShell。");
                }

                PowerShellResult? interruptedResult = await WaitForExitAsync(
                    process,
                    timeout,
                    cancellationToken);
                if (interruptedResult is not null)
                {
                    return interruptedResult;
                }

                return new PowerShellResult(
                    process.ExitCode,
                    await ReadAllTextIfExistsAsync(outputPath),
                    await ReadAllTextIfExistsAsync(errorPath));
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == PowerShellExitCodes.ElevationCancelled)
            {
                return new PowerShellResult(
                    PowerShellExitCodes.ElevationCancelled,
                    string.Empty,
                    "用户取消了管理员权限请求。");
            }
        }

        private static ProcessStartInfo CreateStartInfo(string scriptPath, bool requiresElevation)
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = "powershell.exe",
                UseShellExecute = requiresElevation,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = !requiresElevation,
                RedirectStandardError = !requiresElevation,
                Verb = requiresElevation ? "runas" : string.Empty
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            return startInfo;
        }

        private static async Task<PowerShellResult?> WaitForExitAsync(
            Process process,
            TimeSpan? timeout,
            CancellationToken cancellationToken)
        {
            using var timeoutSource = new CancellationTokenSource(timeout ?? DefaultTimeout);
            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);

            try
            {
                await process.WaitForExitAsync(linkedSource.Token);
                return null;
            }
            catch (OperationCanceledException)
            {
                // 取消既可能来自调用方也可能来自超时；尽力终止子进程后用稳定退出码区分两者。
                TryKillProcess(process);
                bool cancelledByCaller = cancellationToken.IsCancellationRequested;
                return new PowerShellResult(
                    cancelledByCaller ? PowerShellExitCodes.Cancelled : PowerShellExitCodes.TimedOut,
                    string.Empty,
                    cancelledByCaller ? "PowerShell 操作已取消。" : "PowerShell 操作执行超时。");
            }
        }

        private static void TryKillProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"终止 PowerShell 进程失败：{ex.Message}");
            }
        }

        private static async Task<string> ReadAllTextIfExistsAsync(string path)
        {
            try
            {
                return File.Exists(path)
                    ? await File.ReadAllTextAsync(path)
                    : string.Empty;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"读取 PowerShell 输出文件失败：{ex.Message}");
                return string.Empty;
            }
        }

        private static string JoinOutput(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first)) return second;
            if (string.IsNullOrWhiteSpace(second)) return first;
            return first.TrimEnd() + Environment.NewLine + second.TrimEnd();
        }

        private static void ValidateScriptFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) ||
                !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) ||
                !string.Equals(Path.GetExtension(fileName), ".ps1", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("PowerShell 脚本名称必须是不含路径的 .ps1 文件名。", nameof(fileName));
            }
        }

        private static async Task TryDeleteDirectoryAsync(string path)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                    }

                    return;
                }
                catch (Exception ex) when (attempt < 2)
                {
                    // 子进程刚退出时其文件句柄可能尚未释放，有限重试不会掩盖最终清理失败。
                    Debug.WriteLine($"清理 PowerShell 临时目录失败，将重试：{ex.Message}");
                    await Task.Delay(100);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"清理 PowerShell 临时目录失败：{ex.Message}");
                }
            }
        }
    }
}
