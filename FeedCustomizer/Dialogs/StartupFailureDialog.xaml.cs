using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace FeedCustomizer.Dialogs
{
    public sealed partial class StartupFailureDialog : ContentDialog
    {
        public StartupFailureDialog()
        {
            InitializeComponent();
            SecondaryButtonClick += OnCopyButtonClick;
        }

        public void Configure(string title, string details)
        {
            Title = title;
            ErrorTextBox.Text = details;
        }

        private void OnCopyButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var package = new DataPackage();
            package.SetText(ErrorTextBox.Text);
            Clipboard.SetContent(package);
            args.Cancel = true;
        }
    }
}
