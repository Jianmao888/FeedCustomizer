using Microsoft.UI.Xaml.Controls;

namespace FeedCustomizer.Dialogs
{
    public sealed partial class ConfirmDialog : ContentDialog
    {
        public ConfirmDialog()
        {
            InitializeComponent();
        }

        public void Configure(string title, string content, string primaryButtonText, string closeButtonText)
        {
            Title = title;
            ConfirmText.Text = content;
            PrimaryButtonText = primaryButtonText;
            CloseButtonText = closeButtonText;
        }
    }
}
