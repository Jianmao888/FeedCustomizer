using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FeedCustomizer.Core.Constants;
using FeedCustomizer.Core.DataService;
using FeedCustomizer.Core.Models;
using FeedCustomizer.Core.Tools;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Storage;

namespace FeedCustomizer.ViewModels
{
    /// <summary>
    /// 主页的视图模型：集中管理源列表、源提供程序开关以及页面状态。
    /// 所有业务逻辑都放在这里，页面后置代码只负责导航、遮罩、对话框等 UI 交互。
    /// </summary>
    public partial class MainPageViewModel : ObservableObject
    {
        // =====================
        // 数据
        // =====================

        /// <summary>源列表，绑定到设置展开器。</summary>
        [ObservableProperty]
        public partial ObservableCollection<FeedViewModel> Feeds { get; set; } = [];

        /// <summary>是否存在源，供未来空状态展示使用。</summary>
        public bool HasFeeds => Feeds.Count > 0;

        /// <summary>已删除、等待“应用”的源列表。</summary>
        private readonly List<FeedViewModel> _deleteFeeds = [];

        /// <summary>是否存在待应用的更改。</summary>
        private bool _canApplyFeeds;

        /// <summary>初始化任务，页面 Loaded 时等待其完成。</summary>
        public Task InitializationTask { get; }

        // =====================
        // UI 状态绑定
        // =====================

        /// <summary>是否正在执行耗时操作（用于禁用控件）。</summary>
        [ObservableProperty]
        public partial bool IsLoading { get; set; } = true;

        /// <summary>常规按钮是否可用。</summary>
        [ObservableProperty]
        public partial bool IsButtonsEnabled { get; set; }

        /// <summary>“应用/取消”按钮是否可用。</summary>
        [ObservableProperty]
        public partial bool IsApplyButtonEnabled { get; set; }

        /// <summary>地区限制警告横幅是否可见。</summary>
        [ObservableProperty]
        public partial bool IsRegionWarningVisible { get; set; }

        /// <summary>自定义源提供程序是否启用。</summary>
        [ObservableProperty]
        public partial bool IsFeedProviderEnabled { get; set; }

        // =====================
        // UI 请求事件（视图模型不依赖具体控件）
        // =====================

        /// <summary>请求页面进行导航。</summary>
        public event EventHandler<MainPageNavigationRequestedEventArgs>? NavigationRequested;

        /// <summary>请求显示或隐藏加载遮罩。</summary>
        public event EventHandler<bool>? LoadingOverlayRequested;

        /// <summary>请求打开帮助文档文件。</summary>
        public event EventHandler<StorageFile>? HelpFileReady;

        // =====================
        // 初始化逻辑
        // =====================

        public MainPageViewModel()
        {
            // 源集合变化时同步 HasFeeds，便于未来展示空状态。
            Feeds.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFeeds));
            InitializationTask = InitializeAllFeedsAsync();
        }

        /// <summary>
        /// 初始化所有源数据，并在完成后设置页面状态。
        /// </summary>
        private async Task InitializeAllFeedsAsync()
        {
            await InitAsync();

            // TODO 稍后要修改这里的横幅展示逻辑
            // 如果系统策略已解限，则不展示地区限制警告横幅。
            bool isNonEu = DeviceRegionTool.IsNonEuropeanUnionRegion();
            bool isPolicyEnabled = await RegionPolicyService.IsThirdPartyWidgetFeedEnabledAsync();
            IsRegionWarningVisible = isNonEu && !isPolicyEnabled;

            IsLoading = false;
            IsButtonsEnabled = true;
            IsApplyButtonEnabled = _canApplyFeeds;
        }

        /// <summary>
        /// 初始化应用程序：检查源提供程序是否安装，并更新或复制资源文件。
        /// </summary>
        private async Task InitAsync()
        {
            // 优先使用内存中缓存的开关状态，否则回退到系统安装状态。
            if (FeedProviderEnableDataService.IsFeedProviderEnabled is bool cachedEnabled)
            {
                IsFeedProviderEnabled = cachedEnabled;
            }
            else
            {
                IsFeedProviderEnabled = await PackageInstaller.IsFeedProviderInstalled();
            }

            if (FeedListDataService.IsEnable)
            {
                // 有缓存的源，直接读取。
                LoadFeedsFromDataService();
            }
            else
            {
                // 没有缓存，则执行从磁盘初始化的逻辑。
                // 更新或复制源提供程序至用户数据目录。
                bool isUpToDate = await ResourcesCopier.IsResourceUpToDate();
                if (!isUpToDate)
                {
                    // 源提供程序的可执行文件可能被小组件占用而锁定。
                    // 在替换 AOT 二进制文件前先注销它；安装只执行一次，
                    // 放在此分支外可避免启动期间的第二次卸载/注册循环。
                    if (IsFeedProviderEnabled && await PackageInstaller.IsFeedProviderInstalled())
                    {
                        await PackageInstaller.UninstallFeedProvider();
                    }

                    await ResourcesCopier.ResourcesCopyAsync();
                }

                // 加载源列表。
                await LoadFeedsFromXmlAsync();
            }

            // 如果有新建或编辑的源，则将其加入列表。
            if (AddOrEditFeedDataService.Feed is Feed feed)
            {
                AddOrEditFeed(new FeedViewModel(feed));
                AddOrEditFeedDataService.Feed = null;
            }

            // 不要在每次页面启动时都重写/重新注册源提供程序。
            // 小组件可能仍占用 COM 服务器；仅当包缺失或暂存文件
            // 与当前资源不一致时才刷新注册。
            if (IsFeedProviderEnabled &&
                (!await PackageInstaller.IsFeedProviderInstalled() ||
                 !ResourcesCopier.IsRegisteredProviderCurrent()))
            {
                if (!await PackageInstaller.InstallFeedProvider())
                {
                    IsFeedProviderEnabled = false;
                }
            }
        }

        /// <summary>
        /// 从 Xml 文件加载源列表。
        /// </summary>
        private async Task LoadFeedsFromXmlAsync()
        {
            Feeds.Clear();
            try
            {
                var feedItems = await ManifestXmlService.Read();
                foreach (var item in feedItems)
                {
                    Feeds.Add(new FeedViewModel(item));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading feeds: {ex.Message}");
            }
        }

        /// <summary>
        /// 从内存数据服务中加载源列表与待删除列表。
        /// </summary>
        private void LoadFeedsFromDataService()
        {
            foreach (var item in FeedListDataService.FeedList)
            {
                Feeds.Add(new FeedViewModel(item));
            }

            foreach (var item in FeedListDataService.DeleteFeedList)
            {
                _deleteFeeds.Add(new FeedViewModel(item));
            }

            _canApplyFeeds = FeedListDataService.CanAppltFeeds;
        }

        /// <summary>
        /// 从添加/编辑页返回后刷新状态。
        /// </summary>
        public void RefreshAfterNavigation()
        {
            if (AddOrEditFeedDataService.Feed is Feed feed)
            {
                AddOrEditFeed(new FeedViewModel(feed));
                AddOrEditFeedDataService.Feed = null;
            }

            IsLoading = false;
            IsButtonsEnabled = true;
            IsApplyButtonEnabled = _canApplyFeeds;
        }

        // =====================
        // 业务逻辑
        // =====================

        /// <summary>
        /// 进入忙碌状态：禁用相关控件，防止用户重复操作。
        /// </summary>
        private void BeginLoading()
        {
            IsLoading = true;
            IsButtonsEnabled = false;
            IsApplyButtonEnabled = false;
        }

        /// <summary>
        /// 结束忙碌状态：恢复控件，并同步“应用”按钮的可用性。
        /// </summary>
        private void EndLoading()
        {
            IsButtonsEnabled = true;
            IsApplyButtonEnabled = _canApplyFeeds;
            IsLoading = false;
        }

        /// <summary>
        /// 更新是否存在待应用更改，并同步“应用/取消”按钮状态。
        /// </summary>
        private void MarkCanApplyFeeds(bool canApply)
        {
            _canApplyFeeds = canApply;
            IsApplyButtonEnabled = canApply;
        }

        /// <summary>
        /// 编辑已有源：保存当前列表到数据服务后请求导航到添加/编辑页。
        /// </summary>
        public void EditFeed(FeedViewModel feed)
        {
            if (IsLoading)
            {
                return;
            }

            SaveListToDataService();
            NavigationRequested?.Invoke(
                this,
                new MainPageNavigationRequestedEventArgs(MainPageNavigationTarget.AddFeed, feed));
        }

        /// <summary>
        /// 删除源。
        /// </summary>
        public void DeleteFeed(FeedViewModel feed)
        {
            BeginLoading();
            if (Feeds.Remove(feed))
            {
                _deleteFeeds.Add(feed);
                MarkCanApplyFeeds(true);
            }

            EndLoading();
        }

        /// <summary>
        /// 编辑或添加源。
        /// </summary>
        private void AddOrEditFeed(FeedViewModel feed)
        {
            if (UpdateObject(Feeds, feed))
            {
                Debug.WriteLine($"Feed with ID {feed.Id} updated.");
                if (feed.IsEdited)
                {
                    feed.IsEdited = false;
                    MarkCanApplyFeeds(true);
                }
            }
            else
            {
                Feeds.Add(feed);
                feed.IsEdited = false;
                MarkCanApplyFeeds(true);
            }
        }

        /// <summary>
        /// 保存源列表到 Xml 文件，并根据开关状态安装或卸载源提供程序。
        /// </summary>
        private async Task SaveFeedsAsync()
        {
            BeginLoading();
            try
            {
                if (IsFeedProviderEnabled)
                {
                    await PackageInstaller.UninstallFeedProvider();
                }

                var feedItems = new List<Feed>();
                foreach (var feedViewModel in Feeds)
                {
                    feedItems.Add(feedViewModel.FeedItem);
                }

                await ManifestXmlService.Write(feedItems);

                if (IsFeedProviderEnabled)
                {
                    await PackageInstaller.InstallFeedProvider();
                }

                _deleteFeeds.Clear();
                _canApplyFeeds = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving feeds: {ex.Message}");
            }
            finally
            {
                EndLoading();
            }
        }

        /// <summary>
        /// 取消待应用的更改，并重新从 Xml 加载源列表。
        /// </summary>
        private async Task CancelPendingChangesAsync()
        {
            BeginLoading();
            try
            {
                Feeds.Clear();
                _deleteFeeds.Clear();
                FeedListDataService.FeedList.Clear();
                FeedListDataService.DeleteFeedList.Clear();
                FeedListDataService.CanAppltFeeds = false;
                FeedListDataService.IsEnable = false;
                AddOrEditFeedDataService.Feed = null;
                await LoadFeedsFromXmlAsync();
                _canApplyFeeds = false;
            }
            finally
            {
                EndLoading();
            }
        }

        /// <summary>
        /// 启用或关闭自定义源提供程序。
        /// </summary>
        public async Task EnableOrDisableFeedProviderAsync()
        {
            BeginLoading();
            try
            {
                if (!IsFeedProviderEnabled)
                {
                    await PackageInstaller.UninstallFeedProvider();
                }
                else if (!await PackageInstaller.InstallFeedProvider())
                {
                    IsFeedProviderEnabled = false;
                }
            }
            finally
            {
                EndLoading();
            }
        }

        /// <summary>
        /// 更新 ObservableCollection 中的对象；找到相同 Id 则替换。
        /// </summary>
        private static bool UpdateObject(ObservableCollection<FeedViewModel> list, FeedViewModel newObj)
        {
            int index = -1;

            foreach (var item in list)
            {
                if (item.Id == newObj.Id)
                {
                    index = list.IndexOf(item);
                    break;
                }
            }

            if (index >= 0)
            {
                list[index] = newObj;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 清理图片目录中不再被任何源引用的图片。
        /// 在 UI 线程快照当前引用，随后把 Xml 读取与文件删除放到后台线程执行，
        /// 避免阻塞启动流程与界面。
        /// </summary>
        public Task CleanUpUnusedImagesAsync()
        {
            var referencedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var feed in Feeds)
            {
                referencedPaths.Add(feed.FeedItem.ImagePath);
            }

            foreach (var feed in _deleteFeeds)
            {
                referencedPaths.Add(feed.FeedItem.ImagePath);
            }

            return Task.Run(async () =>
            {
                try
                {
                    foreach (var item in await ManifestXmlService.Read())
                    {
                        referencedPaths.Add(item.ImagePath);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"清理图片缓存时读取 Xml 失败: {ex.Message}");
                }

                ImageHelper.DeleteUnreferencedImages(referencedPaths);
            });
        }

        /// <summary>
        /// 将当前数据缓存至 DataService 单例，供页面往返时复用。
        /// </summary>
        private void SaveListToDataService()
        {
            FeedListDataService.FeedList.Clear();
            FeedListDataService.DeleteFeedList.Clear();

            foreach (var item in Feeds)
            {
                FeedListDataService.FeedList.Add(item.FeedItem);
            }

            foreach (var item in _deleteFeeds)
            {
                FeedListDataService.DeleteFeedList.Add(item.FeedItem);
            }

            FeedListDataService.CanAppltFeeds = _canApplyFeeds;
            FeedListDataService.IsEnable = true;
            FeedProviderEnableDataService.IsFeedProviderEnabled = IsFeedProviderEnabled;
        }

        /// <summary>
        /// 是否还能继续添加源。
        /// </summary>
        private bool CanAddFeed()
        {
            return Feeds.Count < Constants.MaxFeedNum;
        }

        /// <summary>
        /// 获取帮助文档文件。
        /// </summary>
        private static async Task<StorageFile?> GetHelpFileAsync()
        {
            return await GetHelpTool.GetHelpFileAsync();
        }

        // =====================
        // 命令（绑定到页面按钮）
        // =====================

        /// <summary>添加源：保存当前状态后请求导航到添加页。</summary>
        [RelayCommand]
        private void AddFeed()
        {
            if (IsLoading || !CanAddFeed())
            {
                return;
            }

            SaveListToDataService();
            NavigationRequested?.Invoke(
                this,
                new MainPageNavigationRequestedEventArgs(MainPageNavigationTarget.AddFeed));
        }

        /// <summary>应用更改：显示遮罩并保存源列表。</summary>
        [RelayCommand]
        private async Task ApplyFeedsAsync()
        {
            if (IsLoading)
            {
                return;
            }

            LoadingOverlayRequested?.Invoke(this, true);
            try
            {
                await SaveFeedsAsync();
            }
            finally
            {
                LoadingOverlayRequested?.Invoke(this, false);
            }
        }

        /// <summary>取消更改：显示遮罩并重新加载源列表。</summary>
        [RelayCommand]
        private async Task CancelChangesAsync()
        {
            if (IsLoading)
            {
                return;
            }

            LoadingOverlayRequested?.Invoke(this, true);
            try
            {
                await CancelPendingChangesAsync();
            }
            finally
            {
                LoadingOverlayRequested?.Invoke(this, false);
            }
        }

        /// <summary>获取帮助文档，并请求打开。</summary>
        [RelayCommand]
        private async Task GetHelpAsync()
        {
            if (await GetHelpFileAsync() is StorageFile file)
            {
                HelpFileReady?.Invoke(this, file);
            }
        }

        /// <summary>打开“关于”页面：先保存当前状态。</summary>
        [RelayCommand]
        private void About()
        {
            SaveListToDataService();
            NavigationRequested?.Invoke(
                this,
                new MainPageNavigationRequestedEventArgs(MainPageNavigationTarget.Settings));
        }

        /// <summary>跳转到解除地区限制的设置页。</summary>
        [RelayCommand]
        private void UnlockRegionPolicy()
        {
            SaveListToDataService();
            NavigationRequested?.Invoke(
                this,
                new MainPageNavigationRequestedEventArgs(
                    MainPageNavigationTarget.RegionPolicySettings,
                    Constants.RegionPolicy.NavigationParameter));
        }
    }

    /// <summary>
    /// 主页可触发的导航目标。
    /// </summary>
    public enum MainPageNavigationTarget
    {
        AddFeed,
        Settings,
        RegionPolicySettings,
    }

    /// <summary>
    /// 导航请求参数，视图模型通过它告知视图要导航到哪个页面。
    /// </summary>
    public sealed class MainPageNavigationRequestedEventArgs(MainPageNavigationTarget target, object? parameter = null) : EventArgs
    {
        public MainPageNavigationTarget Target { get; } = target;

        public object? Parameter { get; } = parameter;
    }
}
