using FeedCustomizer.Core.Constants;
using FeedCustomizer.Core.Tools;
using FeedCustomizer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.IO;
using System.Threading.Tasks;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FeedCustomizer.Pages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class AddFeedPage : Page
    {
        private FeedViewModel FeedViewModel;
        private bool _iconModeInitialized;
        private bool _suppressIconModeChange;

        public AddFeedPage()
        {
            InitializeComponent();
            FeedViewModel = new FeedViewModel();
            _iconModeInitialized = true;
            UpdateIconModeVisibility();
            UpdateImagePreviews();
        }

        /// <summary>
        /// Called when the Page is navigated to.
        /// </summary>
        /// <param name="e"></param>
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is FeedViewModel feedViewModel)
            {
                FeedViewModel = feedViewModel;

                _suppressIconModeChange = true;
                bool usesWebsiteIcon = Path.GetFileName(feedViewModel.ImagePath)
                    .StartsWith("web-", StringComparison.OrdinalIgnoreCase);
                WebsiteIconRadioButton.IsChecked = usesWebsiteIcon;
                CustomIconRadioButton.IsChecked = !usesWebsiteIcon;
                UpdateImagePreviews(feedViewModel.BitmapImage);
                UpdateIconModeVisibility();
                _suppressIconModeChange = false;
            }
        }

        private void OnIconModeChanged(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;
            UpdateIconModeVisibility();

            if (_iconModeInitialized && !_suppressIconModeChange)
            {
                FeedViewModel.MarkIconModeChanged();
            }
        }

        private void UpdateIconModeVisibility()
        {
            if (WebsiteImageControls is null ||
                CustomImageControls is null ||
                WebsiteIconRadioButton is null ||
                CustomIconRadioButton is null)
            {
                return;
            }

            WebsiteImageControls.Visibility = WebsiteIconRadioButton.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
            CustomImageControls.Visibility = CustomIconRadioButton.IsChecked == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private async void OnSelectImageButtonClick(object sender, RoutedEventArgs e)
        {
            // 标记参数为已使用以消除 IDE0060 警告
            _ = sender;
            _ = e;

            if (await FeedViewModel.SelectImage() is BitmapImage bitmapImage)
            {
                UpdateImagePreviews(bitmapImage);
            }
        }

        private void OnClearImageButtonClick(object sender, RoutedEventArgs e)
        {
            // 标记参数为已使用以消除 IDE0060 警告
            _ = sender;
            _ = e;

            FeedViewModel.ClearImage();
            UpdateImagePreviews();
        }

        protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
        {
            base.OnNavigatingFrom(e);

            // 撤销更改
            FeedViewModel.CancelChanges();
        }

        private async void OnSaveButtonClick(object sender, RoutedEventArgs e)
        {
            _ = sender;
            _ = e;

            if (App.MainWindow is not MainWindow window)
            {
                return;
            }

            SaveButton.IsEnabled = false;
            Uri? websiteUri = null;
            bool overlayVisible = false;
            try
            {
                websiteUri = FeedViewModel.NormalizeUrl();

                if (WebsiteIconRadioButton.IsChecked == true)
                {
                    window.SetLoadingOverlayVisible(true);
                    overlayVisible = true;
                    BitmapImage downloadedImage = await FeedViewModel.PrepareWebsiteIconAsync(websiteUri);
                    UpdateImagePreviews(downloadedImage);
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }

                FeedViewModel.CommitChanges();
                if (overlayVisible)
                {
                    window.SetLoadingOverlayVisible(false);
                    overlayVisible = false;
                }
                // 添加页始终返回主页；在无返回栈的情况下补导航，避免保存后停留在当前页。
                if (Frame.CanGoBack)
                {
                    Frame.GoBack();
                }
                else
                {
                    Frame.Navigate(typeof(MainPage));
                }
            }
            catch (Exception ex)
            {
                if (overlayVisible)
                {
                    window.SetLoadingOverlayVisible(false);
                    overlayVisible = false;
                }
                SaveButton.IsEnabled = FeedViewModel.CanSave.Value;
                string details = string.Join(
                    Environment.NewLine,
                    $"URL: {websiteUri?.AbsoluteUri ?? FeedViewModel.Url}",
                    $"Exception: {ex.GetType().FullName}",
                    $"Message: {ex.Message}",
                    string.Empty,
                    ex.ToString());
                await DialogService.ShowWebIconFetchErrorAsync(details);
            }
            finally
            {
                if (overlayVisible)
                {
                    window.SetLoadingOverlayVisible(false);
                }
            }
        }

        private void UpdateImagePreviews(BitmapImage? bitmapImage = null)
        {
            bool usesDefaultImage = string.Equals(
                Path.GetFileName(FeedViewModel.ImagePath),
                Constants.DefaultImageName,
                StringComparison.OrdinalIgnoreCase);
            Visibility placeholderVisibility = usesDefaultImage
                ? Visibility.Visible
                : Visibility.Collapsed;
            Visibility imageVisibility = usesDefaultImage
                ? Visibility.Collapsed
                : Visibility.Visible;

            WebsiteDefaultIcon.Visibility = placeholderVisibility;
            CustomDefaultIcon.Visibility = placeholderVisibility;
            WebsitePreviewImage.Visibility = imageVisibility;
            PreviewImage.Visibility = imageVisibility;

            if (!usesDefaultImage)
            {
                bitmapImage ??= FeedViewModel.BitmapImage;
                WebsitePreviewImage.Source = bitmapImage;
                PreviewImage.Source = bitmapImage;
            }
        }

        private void OnCancelButtonClick(object sender, RoutedEventArgs e)
        {
            // 标记参数为已使用以消除 IDE0060 警告
            _ = sender;
            _ = e;

            Frame.GoBack();
        }
    }
}
