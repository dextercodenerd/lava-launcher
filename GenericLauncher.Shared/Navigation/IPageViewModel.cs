using System.ComponentModel;

namespace GenericLauncher.Navigation;

public interface IPageViewModel : INotifyPropertyChanged
{
    string Title { get; }

    // Default: false. Most pages are distinct instances (transient).
    // If true, the Navigation Component will NEVER Dispose this page.
    bool IsRootScreen => false;
}
