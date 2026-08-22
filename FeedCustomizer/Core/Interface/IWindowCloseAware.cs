namespace FeedCustomizer.Core.Interface
{
    public interface IWindowCloseAware
    {
        void OnWindowClosing();

        bool CanClose()
        {
            return true;
        }
    }
}
