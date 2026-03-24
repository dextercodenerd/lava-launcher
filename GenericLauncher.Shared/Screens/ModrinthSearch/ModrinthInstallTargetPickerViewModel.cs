using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GenericLauncher.InstanceMods;

namespace GenericLauncher.Screens.ModrinthSearch;

public partial class ModrinthInstallTargetPickerViewModel : ObservableObject
{
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _title = "Select Instance";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private ObservableCollection<CompatibleInstanceInstallTarget> _targets = [];

    public void Reset()
    {
        IsLoading = false;
        Message = "";
        Targets.Clear();
    }
}
