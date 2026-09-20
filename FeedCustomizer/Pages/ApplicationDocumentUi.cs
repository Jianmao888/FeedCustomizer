using FeedCustomizer.Core.Documents;
using FeedCustomizer.Core.Infrastructure.Logging;
using FeedCustomizer.Core.Tools;
using Microsoft.Windows.ApplicationModel.Resources;
using System;
using System.Threading.Tasks;
using Windows.Storage;

namespace FeedCustomizer.Pages
{
    /// <summary>
    /// 应用文档的 UI 适配入口。文档服务已完成同步和路径约束，
    /// 此处只将经过验证的文件交给系统默认关联程序，并统一处理显示失败。
    /// </summary>
    internal static class ApplicationDocumentUi
    {
        private static readonly IAppLog Log = AppLog.For("ApplicationDocumentUi");

        /// <summary>将已准备好的文档交给 Windows 打开。</summary>
        internal static async Task OpenAsync(ApplicationDocumentOpenRequestedEventArgs request)
        {
            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(request.FilePath);
                if (App.MainWindow is MainWindow window)
                {
                    await window.ExternalLaunch.OpenFileAsync(file);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "把应用文档交给 Windows 打开失败，类型={DocumentKind}", request.Kind);
                try
                {
                    await ShowFailureAsync(ex.Message);
                }
                catch (Exception dialogException)
                {
                    // 打开文档失败已经有诊断；对话框异常只能记录，不能成为 UI 未处理异常。
                    Log.Error(dialogException, "显示应用文档打开错误对话框失败");
                }
            }
        }

        /// <summary>展示文档准备或打开失败的本地化提示。</summary>
        internal static Task ShowFailureAsync(string diagnostic)
        {
            var resources = new ResourceLoader();
            string content = resources.GetString("DocumentOpenFailureMessage");
            if (!string.IsNullOrWhiteSpace(diagnostic))
            {
                content += Environment.NewLine + Environment.NewLine + diagnostic;
            }

            return DialogService.ShowMessageAsync(
                resources.GetString("DocumentOpenFailureTitle"),
                content,
                resources.GetString("DialogOK"));
        }
    }
}
