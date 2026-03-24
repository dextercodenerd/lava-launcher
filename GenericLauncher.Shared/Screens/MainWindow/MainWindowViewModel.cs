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
using GenericLauncher.Database.Model;
using GenericLauncher.InstanceMods;
using GenericLauncher.Minecraft;
using GenericLauncher.Misc;
using GenericLauncher.Model;
using GenericLauncher.Modrinth;
using GenericLauncher.Modrinth.Json;
using GenericLauncher.Navigation;
using GenericLauncher.Screens.HomeScreen;
using GenericLauncher.Screens.InstanceDetails;
using GenericLauncher.Screens.ModrinthProjectDetails;
using GenericLauncher.Screens.ModrinthSearch;
using GenericLauncher.Screens.NewInstanceDialog;
using GenericLauncher.Screens.ProfileScreen;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Screens.MainWindow;

public partial class MainWindowViewModel : ViewModelBase
{
    private const double DefaultSpace = 16;
    private const double QuickPlayPanelHeight = 100;

    private readonly ILogger? _logger;
    private readonly AuthService? _auth;
    private readonly MinecraftLauncher? _minecraftLauncher;
    private readonly ModrinthApiClient? _modrinthApiClient;
    private readonly InstanceModsManager? _instanceModsManager;

    [ObservableProperty] private string _appTitle = Product.Name;

    [ObservableProperty] private Thickness _mainContentBottomMargin = new(0, 84, 0, 0);
    [ObservableProperty] private bool _quickPlayVisible = true;

    [ObservableProperty] private ObservableCollection<AccountListItem> _accounts = [];
    [ObservableProperty] private AccountListItem? _selectedAccount;

    // Navigation Component
    public StackNavigationViewModel Navigation { get; }

    public HomeViewModel HomeViewModel { get; }
    public ProfileViewModel ProfileViewModel { get; }
    public NewInstanceDialogViewModel NewInstanceDialogViewModel { get; }
    public ModrinthSearchViewModel ModrinthSearchViewModel { get; }

    // Design preview constructor
    public MainWindowViewModel() : this(null)
    {
    }

    public MainWindowViewModel(
        AuthService? authService = null,
        MinecraftLauncher? minecraftLauncher = null,
        ModrinthApiClient? modrinthApiClient = null,
        InstanceModsManager? instanceModsManager = null,
        ILogger? logger = null)
    {
        _logger = logger;
        _auth = authService;
        _minecraftLauncher = minecraftLauncher;
        _modrinthApiClient = modrinthApiClient;
        _instanceModsManager = instanceModsManager;

        Navigation = new StackNavigationViewModel();

        // TODO: Loading logic might need adjustment, skipping for now to focus on Main Nav
        // CurrentViewModel = new LoadingViewModel();

        HomeViewModel = new HomeViewModel(
            authService,
            minecraftLauncher,
            App.LoggerFactory?.CreateLogger(nameof(HomeViewModel)),
            GoToInstanceDetails);

        Navigation.SetRoot(HomeViewModel);

        ProfileViewModel = new ProfileViewModel(
            authService,
            App.LoggerFactory?.CreateLogger(nameof(ProfileViewModel)));

        NewInstanceDialogViewModel = new NewInstanceDialogViewModel(
            minecraftLauncher,
            App.LoggerFactory?.CreateLogger(nameof(NewInstanceDialogViewModel)));

        ModrinthSearchViewModel = new ModrinthSearchViewModel(
            modrinthApiClient,
            instanceModsManager,
            ModrinthSearchContext.CreateRoot(),
            GoToModrinthProjectDetails,
            null,
            App.LoggerFactory?.CreateLogger(nameof(ModrinthSearchViewModel)));

        if (_minecraftLauncher is null || _auth is null)
        {
            return;
        }

        UpdateAccountsUi(_auth.Accounts, _auth.ActiveAccount);
        _auth.AccountsChanged += OnAuthAccountChanged;
        // We don't listen to _auth.ActiveAccount changes here, because here we ara making the changes themselves.
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

        if (accounts.Count > 0 && Navigation.CurrentPage is null)
        {
            // Switch to home only from Empty state
            Navigation.SetRoot(HomeViewModel);
        }
        else if (accounts.Count == 0)
        {
            // Switch to empty profile screen, when there are no accounts
            // CurrentViewModel = ProfileViewModel; // TODO: Migrate Profile
        }

        var accountToSelect = selectedAccount is null
            ? null
            : Accounts.FirstOrDefault(a => a.Account?.Id == selectedAccount.Id);
        if (accountToSelect is null && Accounts.Count > 0)
        {
            accountToSelect = Accounts.FirstOrDefault(a => !a.IsLogin);
        }

        SelectedAccount = accountToSelect;
    }

    [RelayCommand]
    private void OnClickLibrary()
    {
        Navigation.SetRoot(HomeViewModel);
    }

    [RelayCommand]
    private void OnClickNewInstance()
    {
        NewInstanceDialogViewModel.ShowNewMinecraftInstanceDialog = true;
    }

    [RelayCommand]
    private void OnClickAccount()
    {
        Navigation.SetRoot(ProfileViewModel);
    }

    [RelayCommand]
    private void OnClickModrinthSearch()
    {
        Navigation.SetRoot(ModrinthSearchViewModel);
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
            _auth?.SetActiveAccountAsync(value?.Account)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        _logger?.LogError(t.Exception, "Failed to set active account");
                    }
                });
            return;
        }

        // Switch to profile screen and trigger the login flow
        Navigation.SetRoot(ProfileViewModel);
        if (!ProfileViewModel.ClickLoginCommand.IsRunning)
        {
            // We fire and forget the login task, because error handling is in the ViewModel
            Dispatcher.UIThread.Post(() => ProfileViewModel.ClickLoginCommand.Execute(null));
        }

        var activeAccountItem = Accounts.FirstOrDefault(a => a.Account?.Id == _auth?.ActiveAccount?.Id);
        if (activeAccountItem is null && Accounts.Count > 0)
        {
            activeAccountItem = Accounts.FirstOrDefault(a => !a.IsLogin);
        }

        Dispatcher.UIThread.Post(() => SelectedAccount = activeAccountItem);
    }

    private void GoToInstanceDetails(MinecraftInstance instance)
    {
        var vm = new InstanceDetailsViewModel(
            instance,
            _auth,
            _minecraftLauncher,
            _instanceModsManager,
            _modrinthApiClient,
            GoToModrinthProjectDetails,
            App.LoggerFactory?.CreateLogger(nameof(InstanceDetailsViewModel)));

        Navigation.Push(vm);
    }

    private void GoToModrinthProjectDetails(ModrinthSearchResult searchResult, ModrinthSearchContext searchContext)
    {
        var vm = new ModrinthProjectDetailsViewModel(
            searchResult,
            _modrinthApiClient,
            _instanceModsManager,
            searchContext,
            App.LoggerFactory?.CreateLogger(nameof(ModrinthProjectDetailsViewModel)));

        Navigation.Push(vm);
    }

    private void GoToHome()
    {
        Navigation.SetRoot(HomeViewModel);
    }
}
