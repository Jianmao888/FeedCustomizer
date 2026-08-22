using Microsoft.UI.Xaml.Controls;

namespace FeedCustomizer.Dialogs
{
    public sealed partial class MessageDialog : ContentDialog
    {
        public MessageDialog()
        {
            InitializeComponent();
        }

        public void Configure(string title, string content, string closeButtonText)
        {
            Title = title;
            MessageText.Text = content;
            CloseButtonText = closeButtonText;
        }
    }
}
