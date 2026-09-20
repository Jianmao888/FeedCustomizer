using FeedCustomizer.Core.Infrastructure.Logging;
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;

namespace FeedCustomizer.Core.Tools
{
    /// <summary>
    /// 将链接或文件直接交给 Windows 的默认关联应用，并统一记录启动失败。
    /// 调用仍会等待应用启动视觉结束，避免外部窗口在主窗口尚未可交互时抢占焦点。
    /// </summary>
    public sealed class ExternalLaunchService
    {
        private static readonly IAppLog Log = AppLog.For<ExternalLaunchService>();
        private readonly UiThreadRunner _uiThreadRunner;

        /// <summary>
        /// 创建窗口级外部打开服务。Dispatcher 只用于满足 Windows Launcher 的线程上下文要求。
        /// </summary>
        public ExternalLaunchService(
            Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue,
            Func<Task> waitForSplashHidden)
        {
            _uiThreadRunner = new UiThreadRunner(dispatcherQueue, waitForSplashHidden);
        }

        /// <summary>使用系统默认关联直接打开绝对 URI，不显示应用内确认弹窗。</summary>
        public async Task<bool> OpenLinkAsync(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                // 不把完整输入写入日志，避免未来调用方传入带查询参数的敏感链接。
                Log.Warning("无法打开外部链接：链接格式不是有效的绝对 URI");
                return false;
            }

            return await LaunchAsync(
                async () => await Launcher.LaunchUriAsync(uri),
                "external link");
        }

        /// <summary>
        /// 将经过业务层约束的绝对文件路径解析为 Windows 文件对象，并使用系统默认关联打开。
        /// 路径解析失败与系统启动失败分别记录，调用方只需根据返回值决定是否提示用户。
        /// </summary>
        public async Task<bool> OpenFileAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath))
            {
                // 文件路径来自受控文档服务，但外部打开边界仍需拒绝无效输入，且不记录原始路径。
                Log.Warning("无法解析待打开的外部文件：路径为空或不是绝对路径");
                return false;
            }

            StorageFile file;
            try
            {
                file = await StorageFile.GetFileFromPathAsync(filePath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "无法解析待打开的外部文件");
                return false;
            }

            return await LaunchAsync(
                async () => await Launcher.LaunchFileAsync(file),
                "external file");
        }

        private async Task<bool> LaunchAsync(Func<Task<bool>> launch, string context)
        {
            try
            {
                bool launched = false;
                await _uiThreadRunner.RunAsync(async () =>
                {
                    launched = await launch();
                });
                if (!launched)
                {
                    Log.Warning("Windows 未能打开外部内容，类型={LaunchContext}", context);
                }

                return launched;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "打开外部内容失败，类型={LaunchContext}", context);
                return false;
            }
        }
    }
}
