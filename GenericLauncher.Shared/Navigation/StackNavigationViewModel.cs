using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GenericLauncher.Navigation;

public partial class StackNavigationViewModel : ViewModelBase
{
    private readonly Stack<IPageViewModel> _backStack = new();

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanGoBack))] [NotifyCanExecuteChangedFor(nameof(PopCommand))]
    private IPageViewModel? _currentPage;

    public bool CanGoBack => _backStack.Count > 0;

    public void Push(IPageViewModel page)
    {
        if (CurrentPage is not null)
        {
            _backStack.Push(CurrentPage);
        }

        CurrentPage = page;
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    public void Pop()
    {
        if (_backStack.Count == 0)
        {
            return;
        }

        var oldPage = CurrentPage;
        CurrentPage = _backStack.Pop();

        if (oldPage is IDisposable disposable && !oldPage.IsRootScreen)
        {
            disposable.Dispose();
        }
    }

    public void SetRoot(IPageViewModel page)
    {
        // Dispose current page
        if (CurrentPage is IDisposable disposableCurrent && !CurrentPage.IsRootScreen)
        {
            disposableCurrent.Dispose();
        }

        // Dispose entire backstack
        foreach (var item in _backStack)
        {
            if (item is IDisposable disposableItem && !item.IsRootScreen)
            {
                disposableItem.Dispose();
            }
        }

        _backStack.Clear();
        CurrentPage = page;
        OnPropertyChanged(nameof(CanGoBack)); // Stack cleared, so explicitly notify
        PopCommand.NotifyCanExecuteChanged();
    }
}
