using System;
using System.Collections.Generic;
using System.Text;

namespace FeedCustomizer.Core.Infrastructure.PowerShell;

/// <summary>同权限脚本片段的顺序组合项；片段不得自行退出进程或依赖其他片段的局部变量。</summary>
internal sealed record PowerShellScriptStep(string Name, string Content, int FailureExitCode);

/// <summary>
/// 将受信任的基础设施脚本片段组合成一次执行请求，并为每一步保留稳定的失败退出码。
/// 路径等外部值由片段提供者转义；组合器不解析或修改 PowerShell 语法。
/// </summary>
internal static class PowerShellScriptComposer
{
    internal static PowerShellScript Compose(
        string fileName,
        string prelude,
        IReadOnlyList<PowerShellScriptStep> steps,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(prelude);
        ArgumentNullException.ThrowIfNull(steps);

        if (steps.Count == 0)
        {
            throw new ArgumentException("组合脚本至少需要一个步骤。", nameof(steps));
        }

        var exitCodes = new HashSet<int>();
        var script = new StringBuilder(prelude);

        foreach (PowerShellScriptStep step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.Name) || string.IsNullOrWhiteSpace(step.Content) ||
                step.FailureExitCode <= 0 || !exitCodes.Add(step.FailureExitCode))
            {
                throw new ArgumentException("脚本步骤缺少名称或内容，或失败退出码无效、重复。", nameof(steps));
            }

            // 子作用域隔离局部变量，异常边界保证发布失败时后续注册片段不会执行。
            script.AppendLine();
            script.AppendLine("try {");
            script.AppendLine("    & {");
            script.AppendLine(step.Content);
            script.AppendLine("    }");
            script.AppendLine("}");
            script.AppendLine("catch {");
            script.AppendLine($"    ('Step: ' + {PowerShellLiteral.Quote(step.Name)}) | Out-File -LiteralPath $errorPath -Encoding UTF8");
            script.AppendLine("    ($_ | Out-String) | Out-File -LiteralPath $errorPath -Encoding UTF8 -Append");
            script.AppendLine($"    exit {step.FailureExitCode}");
            script.AppendLine("}");
        }

        script.AppendLine("exit 0");
        return new PowerShellScript(fileName, script.ToString(), Timeout: timeout);
    }
}
