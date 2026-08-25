using FeedCustomizer.Core.Constants;
using FeedCustomizer.Core.DataService;
using FeedCustomizer.Core.Tools;
using FeedCustomizer.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;

namespace FeedCustomizer.ViewModels
{
    public class MainPageModel
    {
        // =====================
        // 数据
        // =====================
        public ObservableCollection<FeedViewModel> Feeds { get; set; } = [];
        public ObservableValue<bool> HasFeeds { get; } = new(false);
        public ObservableValue<bool> IsFeedProviderEnabled { get; set; } = new(false);
        private static List<FeedViewModel> DeleteFeeds { get; set; } = [];
        private bool CanApplyFeeds { get; set; } = false;
        public Task InitializationTask { get; }



        // =====================
        // UI状态绑定
        // =====================
        public ObservableValue<bool> IsLoading { get; set; } = new(true);
        public ObservableValue<bool> IsButtonsEnabled { get; set; } = new(false);
        public ObservableValue<bool> IsApplyButtonEnabled { get; set; } = new(false);
        public ObservableValue<bool> IsRegionWarningVisible { get; set; } = new(false);


        // =====================
        // 初始化逻辑
        // =====================
        public MainPageModel()
        {
            Feeds.CollectionChanged += (_, _) => HasFeeds.Value = Feeds.Count > 0;
            InitializationTask = InitiAllFeeds();
        }

        private async Task InitiAllFeeds()
        {
            await Init();
            IsRegionWarningVisible.Value = DeviceRegionTool.IsNonEuropeanUnionRegion();
            IsLoading.Value = false;
            IsButtonsEnabled.Value = true;
            IsApplyButtonEnabled.Value = CanApplyFeeds;
        }

        // =====================
        // 业务逻辑
        // =====================
        /// <summary>
        /// 初始化应用程序，检查源提供程序是否安装，并更新或复制资源文件。
        /// </summary>
        /// <returns></returns>
        public async Task Init()
        {
            if (FeedProviderEnableDataService.IsFeedProviderEnabled != null)
            {
                Debug.WriteLine("改变了开关的状态");
                Debug.WriteLine($"{IsLoading.Value}");
                IsFeedProviderEnabled.Value = (bool)FeedProviderEnableDataService.IsFeedProviderEnabled;
            }
            else
            {
                IsFeedProviderEnabled.Value = await PackageInstaller.IsFeedProviderInstalled();
            }

            if (FeedListDataService.IsEnable)
            {
                // 如果有缓存的源，则直接读取
                LoadFeedsFromDataService();
            }
            else
            {
                // 如果没有，则进行从磁盘初始化的逻辑
                // 更新或者复制源提供程序至用户数据目录
                bool isUpToDate = await ResourcesCopier.IsResourceUpToDate();
                if (!isUpToDate)
                {
                    // The provider executable can be locked by Widgets while
                    // it is registered.  Unregister it before replacing the
                    // AOT binary.  Installation is performed once below;
                    // keeping it out of this branch avoids a second
                    // uninstall/register cycle during startup.
                    if (IsFeedProviderEnabled.Value && await PackageInstaller.IsFeedProviderInstalled())
                    {
                        await PackageInstaller.UninstallFeedProvider();
                    }

                    await ResourcesCopier.ResourcesCopyAsync();
                }
                // 加载源列表
                await LoadFeedsFromXml();
            }

            // 如果有新建或者编辑的源，则将其添加进列表
            if (AddOrEditFeedDataService.Feed != null)
            {
                AddOrEditFeed(new FeedViewModel(AddOrEditFeedDataService.Feed));
                AddOrEditFeedDataService.Feed = null;
            }

            // Do not rewrite/re-register the provider on every page startup.
            // Widgets may still hold the COM server open; only refresh the
            // registration when the package is missing or its staged files
            // no longer match the current resources.
            if (IsFeedProviderEnabled.Value &&
                (!await PackageInstaller.IsFeedProviderInstalled() ||
                 !ResourcesCopier.IsRegisteredProviderCurrent()))
            {
                if (!await PackageInstaller.InstallFeedProvider())
                {
                    IsFeedProviderEnabled.Value = false;
                }
            }
        }

        /// <summary>
        /// 从Xml文件加载源列表
        /// </summary>
        /// <returns></returns>
        public async Task LoadFeedsFromXml()
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
        /// 从DataService中加载数据
        /// </summary>
        public void LoadFeedsFromDataService()
        {
            foreach (var item in FeedListDataService.FeedList)
            {
                Feeds.Add(new FeedViewModel(item));
            }
            foreach (var item in FeedListDataService.DeleteFeedList)
            {
                DeleteFeeds.Add(new FeedViewModel(item));
            }
            CanApplyFeeds = FeedListDataService.CanAppltFeeds;
        }

        public void RefreshAfterNavigation()
        {
            if (AddOrEditFeedDataService.Feed != null)
            {
                AddOrEditFeed(new FeedViewModel(AddOrEditFeedDataService.Feed));
                AddOrEditFeedDataService.Feed = null;
            }

            IsLoading.Value = false;
            IsButtonsEnabled.Value = true;
            IsApplyButtonEnabled.Value = CanApplyFeeds;
        }

        /// <summary>
        /// 检查是否需要显示首次运行安全警告
        /// </summary>
        /// <returns></returns>
        public static bool CheckFirstRunDialog()
        {
            bool isFirstRun = true; // 默认认为是第一次

            if (SettingsLoader.ContainsKey("IsFirstRun"))
            {
                isFirstRun = (bool)SettingsLoader.GetSettingsOption("IsFirstRun");
            }
            return isFirstRun;
        }

        /// <summary>
        /// 将首次运行标志设置为false
        /// </summary>
        public static void SetFirstRunFalg()
        {
            SettingsLoader.SetSettingsOption("IsFirstRun", false);
        }

        /// <summary>
        /// 禁用所有控件
        /// </summary>
        public void DisableAllControl()
        {
            IsLoading.Value = true;
            IsButtonsEnabled.Value = false;
            IsApplyButtonEnabled.Value = false;
        }

        /// <summary>
        /// 编辑或添加源
        /// </summary>
        /// <param name="FeedViewModel"></param>
        public void AddOrEditFeed(FeedViewModel FeedViewModel)
        {
            if (UpdateObject(Feeds, FeedViewModel))
            {
                Debug.WriteLine($"Feed with ID {FeedViewModel.IsEdited} updated.");
                Debug.WriteLine($"Feed with ID {FeedViewModel.GetHashCode()} updated.");
                if (FeedViewModel.IsEdited)
                {
                    FeedViewModel.IsEdited = false;
                    CanApplyFeeds = true;
                    IsApplyButtonEnabled.Value = CanApplyFeeds;
                }
            }
            else
            {
                Feeds.Add(FeedViewModel);
                FeedViewModel.IsEdited = false;
                CanApplyFeeds = true;
                IsApplyButtonEnabled.Value = CanApplyFeeds;
            }
        }

        /// <summary>
        /// 删除源
        /// </summary>
        /// <param name="FeedViewModel"></param>
        public void DeleteFeed(FeedViewModel FeedViewModel)
        {
            if (Feeds.Remove(FeedViewModel))
            {
                DeleteFeeds.Add(FeedViewModel);
                CanApplyFeeds = true;
                IsApplyButtonEnabled.Value = CanApplyFeeds;
            }

            IsLoading.Value = false;
            IsButtonsEnabled.Value = true;
        }

        /// <summary>
        /// 保存源列表到Xml文件，并根据是否启用源提供程序来安装或卸载源提供程序
        /// </summary>
        /// <returns></returns>
        public async Task SaveFeeds()
        {
            try
            {
                if (IsFeedProviderEnabled)
                    await PackageInstaller.UninstallFeedProvider();
                List<Feed> feedItems = [];
                foreach (var feedViewModel in Feeds)
                {
                    feedItems.Add(feedViewModel.FeedItem);
                }
                await ManifestXmlService.Write(feedItems);
                if (IsFeedProviderEnabled)
                    await PackageInstaller.InstallFeedProvider();
                DeleteFeeds.Clear();
                CanApplyFeeds = false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving feeds: {ex.Message}");
            }
            finally
            {
                IsApplyButtonEnabled.Value = CanApplyFeeds;
                IsButtonsEnabled.Value = true;
                IsLoading.Value = false;
            }
        }

        public async Task CancelPendingChanges()
        {
            try
            {
                Feeds.Clear();
                DeleteFeeds.Clear();
                FeedListDataService.FeedList.Clear();
                FeedListDataService.DeleteFeedList.Clear();
                FeedListDataService.CanAppltFeeds = false;
                FeedListDataService.IsEnable = false;
                AddOrEditFeedDataService.Feed = null;
                await LoadFeedsFromXml();
                CanApplyFeeds = false;
            }
            finally
            {
                IsApplyButtonEnabled.Value = CanApplyFeeds;
                IsButtonsEnabled.Value = true;
                IsLoading.Value = false;
            }
        }

        /// <summary>
        /// 启用或关闭自定义源
        /// </summary>
        /// <returns></returns>
        public async Task EnableOrDisableFeedProvider()
        {
            try
            {
                if (!IsFeedProviderEnabled.Value)
                {
                    await PackageInstaller.UninstallFeedProvider();
                }
                else if (!await PackageInstaller.InstallFeedProvider())
                {
                    IsFeedProviderEnabled.Value = false;
                }
            }
            finally
            {
                IsLoading.Value = false;
                IsButtonsEnabled.Value = true;
                IsApplyButtonEnabled.Value = CanApplyFeeds;
            }
        }

        /// <summary>
        /// 更新ObservableCollection中的对象，如果找到匹配的Id，则替换该对象
        /// </summary>
        /// <param name="list"></param>
        /// <param name="newObj"></param>
        /// <returns></returns>
        private static bool UpdateObject(ObservableCollection<FeedViewModel> list, FeedViewModel newObj)
        {
            // 查找匹配的索引
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
                list[index] = newObj;  // 替换
                return true;
            }

            return false;
        }

        /// <summary>
        /// 清理图片目录中不再被任何源引用的图片。
        /// 在 UI 线程快照当前引用，随后将 Xml 读取与文件删除放到后台线程执行，
        /// 避免阻塞启动流程与界面。
        /// </summary>
        public Task CleanUpUnusedImagesAsync()
        {
            var referencedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var feed in Feeds)
            {
                referencedPaths.Add(feed.FeedItem.ImagePath);
            }

            foreach (var feed in DeleteFeeds)
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
        /// 将当前数据缓存至DataService单例中
        /// </summary>
        public void SaveListToDataService()
        {
            FeedListDataService.FeedList.Clear();
            FeedListDataService.DeleteFeedList.Clear();
            foreach (var item in Feeds)
            {
                FeedListDataService.FeedList.Add(item.FeedItem);
            }
            foreach (var item in DeleteFeeds)
            {
                FeedListDataService.DeleteFeedList.Add(item.FeedItem);
            }
            FeedListDataService.CanAppltFeeds = CanApplyFeeds;
            FeedListDataService.IsEnable = true;
            FeedProviderEnableDataService.IsFeedProviderEnabled = IsFeedProviderEnabled.Value;
        }

        public bool CanAddFeed()
        {
            if (Feeds.Count >= Constants.MaxFeedNum) return false;

            return true;
        }

        /// <summary>
        /// 打开帮助文档
        /// </summary>
        public async Task<Windows.Storage.StorageFile?> GetHelpFileAsync()
        {
            return await GetHelpTool.GetHelpFileAsync();
        }
    }
}
