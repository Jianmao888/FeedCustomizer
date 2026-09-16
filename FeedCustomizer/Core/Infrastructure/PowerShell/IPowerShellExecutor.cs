using FeedCustomizer.Core.Models;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Infrastructure.PowerShell
{
    /// <summary>
    /// PowerShell 进程执行边界。业务代码只依赖更上层的语义适配器，
    /// 由适配器把具体脚本交给此接口执行。
    /// </summary>
    internal interface IPowerShellExecutor
    {
        Task<PowerShellResult> ExecuteAsync(
            PowerShellScript script,
            CancellationToken cancellationToken = default);
    }
}
