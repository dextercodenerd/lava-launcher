using System;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GenericLauncher.Auth;
using GenericLauncher.Database;
using GenericLauncher.Minecraft;
using GenericLauncher.Misc;
using GenericLauncher.Model;
using GenericLauncher.Screens.EmptyStateScreen;
using GenericLauncher.Screens.HomeScreen;
using GenericLauncher.Screens.NewInstanceDialog;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Screens.MainWindow;

public partial class MainWindowViewModel : ViewModelBase
{
    private const double DefaultSpace = 16;
    private const double QuickPlayPanelHeight = 100;

    private readonly ILogger? _logger;
    private readonly AuthService? _auth;
    private readonly MinecraftLauncher? _minecraftLauncher;

    [ObservableProperty] private string _appTitle = Product.Name;

    [ObservableProperty] private Thickness _mainContentBottomMargin = new(0, 84, 0, 0);
    [ObservableProperty] private bool _quickPlayVisible = true;

    [ObservableProperty] private ObservableCollection<AccountListItem> _accounts = [];
    [ObservableProperty] private AccountListItem? _selectedAccount;

    [ObservableProperty] private ViewModelBase _currentViewModel;

    public HomeViewModel HomeViewModel { get; }
    public NewInstanceDialogViewModel NewInstanceDialogViewModel { get; }

    // Design preview constructor
    public MainWindowViewModel() : this(null)
    {
    }

    public MainWindowViewModel(
        AuthService? authService = null,
        MinecraftLauncher? minecraftLauncher = null,
        ILogger? logger = null)
    {
        _logger = logger;
        _auth = authService;
        _minecraftLauncher = minecraftLauncher;

        CurrentViewModel =
            new EmptyStateViewModel(authService, App.LoggerFactory?.CreateLogger(nameof(EmptyStateViewModel)));

        HomeViewModel = new HomeViewModel(authService,
            minecraftLauncher,
            App.LoggerFactory?.CreateLogger(nameof(HomeViewModel)));

        NewInstanceDialogViewModel = new NewInstanceDialogViewModel(
            minecraftLauncher,
            App.LoggerFactory?.CreateLogger(nameof(NewInstanceDialogViewModel)));

        if (_minecraftLauncher is null || _auth is null)
        {
            return;
        }

        UpdateAccountsUi(_auth.Accounts, _auth.ActiveAccount);
        _auth.AccountsChanged += OnAuthAccountChanged;
    }

    private void OnAuthAccountChanged(object? sender, EventArgs e)
    {
        if (_auth is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => { UpdateAccountsUi(_auth.Accounts, _auth.ActiveAccount); });
    }

    private void UpdateAccountsUi(ImmutableList<Account> accounts, Account? selectedAccount)
    {
        Dispatcher.UIThread.VerifyAccess();

        Accounts.Clear();

        foreach (var acc in accounts)
        {
            Accounts.Add(new AccountListItem(acc, false));
        }

        Accounts.Add(new AccountListItem(null, true));

        if (accounts.Count > 0)
        {
            CurrentViewModel = HomeViewModel;
        }
        else if (CurrentViewModel is not EmptyStateViewModel)
        {
            CurrentViewModel =
                new EmptyStateViewModel(_auth, App.LoggerFactory?.CreateLogger(nameof(EmptyStateViewModel)));
        }

        if (selectedAccount is null)
        {
            SelectedAccount = null;
            return;
        }

        SelectedAccount = Accounts.FirstOrDefault(a => a.Account?.Id == selectedAccount.Id);
    }

    [RelayCommand]
    private void OnClickNewInstance()
    {
        NewInstanceDialogViewModel.ShowNewMinecraftInstanceDialog = true;
    }

    private async Task Login()
    {
        if (_auth is null)
        {
            // UI designer
            return;
        }

        try
        {
            await _auth.AuthenticateAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "problem logging in into Minecraft");
        }
    }

    private async Task ToggleExpand()
    {
        if (MainContentBottomMargin.Top == QuickPlayPanelHeight - DefaultSpace)
        {
            MainContentBottomMargin = new Thickness(0);

            // TODO: This "await" blocks this `RelayCommand`, thus it cannot be executed while the margin is collapsing.
            //  This is not a problem now, but if we will need this quick collapse/expand in the future, we must track
            //  the `Task` and cancel it. Or find another way of tracking the animation.

            // The wait is just a quick-hack and is not 100% precise. It is good enough though, because we need to hide
            // the panel only because its anti-aliased rounded corners add visual artifacts when collapsed. We cannot
            // hide it instantly, because that would be ugly flicker, but timing its hiding almost perfectly isn't
            // noticeable.
            await Task.Delay(250);
            QuickPlayVisible = false;
        }
        else
        {
            MainContentBottomMargin = new Thickness(0, QuickPlayPanelHeight - DefaultSpace, 0, 0);
            QuickPlayVisible = true;
        }
    }

    partial void OnSelectedAccountChanged(AccountListItem? value)
    {
        if (value?.IsLogin != true)
        {
            return;
        }

        Login()
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger?.LogError(t.Exception, "login problem");
                }
            });
    }
}
