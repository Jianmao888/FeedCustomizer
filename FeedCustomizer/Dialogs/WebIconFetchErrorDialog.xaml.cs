using Microsoft.UI.Xaml.Controls;

namespace FeedCustomizer.Dialogs
{
    public sealed partial class WebIconFetchErrorDialog : ContentDialog
    {
        public WebIconFetchErrorDialog()
        {
            InitializeComponent();
        }

        public void Configure(string details)
        {
            ErrorTextBox.Text = details;
        }
    }
}
