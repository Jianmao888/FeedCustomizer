using FeedCustomizer.Core.Infrastructure.Deployment;
using FeedCustomizer.Core.Infrastructure.PowerShell;

namespace FeedCustomizer.Core.Deployment;

/// <summary>进程级部署服务组合根；唯一实例持有生命周期锁，不在页面间共享可变部署状态。</summary>
internal static class ProviderDeployment
{
    internal static ProviderDeploymentCoordinator Current { get; } = new(
        new ProviderDeploymentStorage(PowerShellInfrastructure.DeploymentFiles), new WindowsProviderRegistration());
}
