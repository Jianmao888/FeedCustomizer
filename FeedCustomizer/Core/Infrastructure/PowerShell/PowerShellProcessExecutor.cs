using FeedCustomizer.Core.Infrastructure.Logging;
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
        private static readonly IAppLog Log = AppLog.For<PowerShellProcessExecutor>();
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

        public async Task<PowerShellResult> ExecuteAsync(
            PowerShellScript script,
            CancellationToken cancellationToken = default)
        {
            ValidateScriptFileName(script.FileName);
            string operationId = Guid.NewGuid().ToString("N")[..8];
            Stopwatch stopwatch = Stopwatch.StartNew();

            Log.Information(
                "开始执行 PowerShell，操作={OperationId}，脚本={ScriptName}，需要提权={RequiresElevation}，超时秒={TimeoutSeconds}",
                operationId,
                script.FileName,
                script.RequiresElevation,
                (script.Timeout ?? DefaultTimeout).TotalSeconds);

            string tempDirectory = Path.Combine(
                Path.GetTempPath(),
                $"FeedCustomizer_PowerShell_{Guid.NewGuid():N}");
            // 每次调用使用独立目录，避免并发执行时脚本和输出文件互相覆盖。
            Directory.CreateDirectory(tempDirectory);

            // 业务脚本只需写入预先注入的两个路径；提权启动无法重定向标准流时仍可回传诊断。
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
                // 提权进程必须启用 Shell 才能触发 UAC，因而不能直接重定向标准输出；两条路径分别处理。
                PowerShellResult result = script.RequiresElevation
                    ? await ExecuteElevatedAsync(scriptPath, outputPath, errorPath, script.Timeout, cancellationToken)
                    : await ExecuteStandardAsync(scriptPath, outputPath, errorPath, script.Timeout, cancellationToken);

                if (result.ExitCode == 0 && string.IsNullOrWhiteSpace(result.Error))
                {
                    Log.Information(
                        "PowerShell 执行完成，操作={OperationId}，脚本={ScriptName}，退出码={ExitCode}，输出长度={OutputLength}，错误长度={ErrorLength}，耗时毫秒={ElapsedMilliseconds}",
                        operationId,
                        script.FileName,
                        result.ExitCode,
                        result.Output.Length,
                        result.Error.Length,
                        stopwatch.ElapsedMilliseconds);
                }
                else
                {
                    // 脚本均由应用内受控适配器生成。失败诊断先隐藏用户环境信息并限长，
                    // 既保留 AppX/ACL/文件占用等关键上下文，也避免把无限输出直接写入日志。
                    Log.Warning(
                        "PowerShell 执行产生错误诊断，操作={OperationId}，脚本={ScriptName}，退出码={ExitCode}，输出长度={OutputLength}，错误长度={ErrorLength}，耗时毫秒={ElapsedMilliseconds}，输出诊断={OutputDiagnostic}，错误诊断={ErrorDiagnostic}",
                        operationId,
                        script.FileName,
                        result.ExitCode,
                        result.Output.Length,
                        result.Error.Length,
                        stopwatch.ElapsedMilliseconds,
                        LogPrivacy.PrepareDiagnostic(result.Output),
                        LogPrivacy.PrepareDiagnostic(result.Error));
                }

                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 写入临时脚本阶段也可能被调用方取消，统一映射为基础设施约定的结果。
                Log.Warning(
                    "PowerShell 在准备阶段被调用方取消，操作={OperationId}，脚本={ScriptName}，耗时毫秒={ElapsedMilliseconds}",
                    operationId,
                    script.FileName,
                    stopwatch.ElapsedMilliseconds);
                return new PowerShellResult(
                    PowerShellExitCodes.Cancelled,
                    string.Empty,
                    "PowerShell 操作已取消。");
            }
            catch (Exception ex)
            {
                Log.Error(
                    ex,
                    "PowerShell 基础设施执行异常，操作={OperationId}，脚本={ScriptName}，耗时毫秒={ElapsedMilliseconds}",
                    operationId,
                    script.FileName,
                    stopwatch.ElapsedMilliseconds);
                throw;
            }
            finally
            {
                // 无论启动、执行或写入失败都回收脚本与诊断，避免临时目录逐次累积。
                await TryDeleteDirectoryAsync(tempDirectory);
                stopwatch.Stop();
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

            // 进程仍在运行时并行排空两个管道，防止任一缓冲区写满而让子进程和父进程相互等待。
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
            // 普通进程同时收集标准流和脚本显式写入的文件，以兼容不同脚本的输出习惯。
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
                // UseShellExecute=true 是 runas 的前提；因此诊断由脚本写入临时文件，而非标准流。
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
                // UAC 取消发生在进程创建之前，转换为领域层可稳定识别的结果而不是异常。
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
            // ArgumentList 负责逐项传参，避免路径或参数被拼进命令字符串后再次被 PowerShell 解析。
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
            // 将调用方取消和操作超时合并等待，随后仍可从原始令牌判断到底是哪一种终止原因。
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
                    // 只终止本次创建的 powershell.exe；其脚本操作均受单次超时约束。
                    process.Kill();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "终止 PowerShell 进程失败");
            }
        }

        private static async Task<string> ReadAllTextIfExistsAsync(string path)
        {
            try
            {
                // 提权进程可能未创建输出文件；缺失输出是可预期情况，不应覆盖原始操作结果。
                return File.Exists(path)
                    ? await File.ReadAllTextAsync(path)
                    : string.Empty;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "读取 PowerShell 输出文件失败");
                return string.Empty;
            }
        }

        private static string JoinOutput(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first))
            {
                return second;
            }

            if (string.IsNullOrWhiteSpace(second))
            {
                return first;
            }

            return first.TrimEnd() + Environment.NewLine + second.TrimEnd();
        }

        private static void ValidateScriptFileName(string fileName)
        {
            // 禁止目录片段，确保临时脚本路径始终位于本执行器刚创建的专用目录中。
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
                    Log.Warning(
                        ex,
                        "清理 PowerShell 临时目录失败，将进行有限重试，当前尝试={Attempt}",
                        attempt + 1);
                    await Task.Delay(100);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "清理 PowerShell 临时目录失败，已达到重试上限");
                }
            }
        }
    }
}
