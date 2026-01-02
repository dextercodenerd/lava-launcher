using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GenericLauncher.Auth;
using GenericLauncher.Database.Model;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Screens.ProfileScreen;

public partial class ProfileViewModel : ViewModelBase
{
    private readonly ILogger? _logger;
    private readonly AuthService? _auth;

    [ObservableProperty] private Account? _account = null;

    public string ScreenTitle
    {
        get
        {
            if (string.IsNullOrEmpty(Account?.Username))
            {
                return Account is null
                    ? "No Minecraft Account"
                    : XboxStateTitle;
            }

            return Account.Username;
        }
    }

    public bool HasMicrosoftAccount => Account is not null;

    public bool HasMinecraftLicense => Account?.HasMinecraftLicense == true;

    public bool HasAccountProblem =>
        HasMicrosoftAccount && (!HasMinecraftLicense || Account?.XboxAccountState != XboxAccountState.Ok);

    public string AccountProblemTitle
    {
        get
        {
            if (Account?.XboxAccountState != XboxAccountState.Ok)
            {
                return XboxStateTitle;
            }

            if (!HasMinecraftLicense)
            {
                return "No Minecraft license";
            }

            return "";
        }
    }

    public string AccountProblemMessage
    {
        get
        {
            if (Account?.XboxAccountState != XboxAccountState.Ok)
            {
                return XboxStateMessage;
            }

            if (!HasMinecraftLicense)
            {
                return
                    "This account does not have a Minecraft license. You need to purchase Minecraft to play the game.";
            }

            return "";
        }
    }

    public string XboxStateTitle => Account?.XboxAccountState switch
    {
        null => "Not logged in",
        XboxAccountState.Ok => "All good",
        XboxAccountState.Missing => "Xbox account missing",
        XboxAccountState.Banned => "Xbox account banned",
        XboxAccountState.NotAvailable => "Xbox account not available",
        XboxAccountState.AgeVerificationMissing => "Xbox account age verification required",
        _ => "Xbox Account Issue",
    };

    public string XboxStateMessage => Account?.XboxAccountState switch
    {
        XboxAccountState.Ok => "All good",
        XboxAccountState.Missing =>
            "Your Xbox account appears to be missing or not properly linked to your Microsoft account.",
        XboxAccountState.Banned =>
            "Your Xbox account has been banned. Please contact Xbox support for more information.",
        XboxAccountState.NotAvailable => "Xbox services are not available for this account in your region.",
        XboxAccountState.AgeVerificationMissing =>
            "Age verification is required for this Xbox account. Please complete the verification process.",
        XboxAccountState.Unknown => "Unable to determine the state of your Xbox account.",
        _ => "There is an issue with your Xbox account.",
    };

    public ProfileViewModel() : this(null)
    {
    }

    public ProfileViewModel(
        AuthService? authService = null,
        ILogger? logger = null)
    {
        _logger = logger;
        _auth = authService;

        Account = _auth?.ActiveAccount;
        _auth?.ActiveAccountChanged += OnActiveAccountChanged;
    }

    [RelayCommand]
    private async Task OnClickRefreshAccount()
    {
        if (_auth is null || Account is null)
        {
            return;
        }

        // TODO: Show some "refreshing" UI

        try
        {
            var newAcc = await _auth.AuthenticateAccountAsync(Account);
            _logger?.LogDebug("Refreshed account: {acc}", newAcc);

            // TODO: Check of the newAcc still has problems
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Problem with MS account");
        }
    }

    [RelayCommand]
    private async Task OnClickLogin()
    {
        if (_auth is null)
        {
            return;
        }

        var account = await _auth.AuthenticateAsync();
    }

    [RelayCommand]
    private async Task OnClickLogout()
    {
        if (_auth is null || Account is null)
        {
            return;
        }

        var loggedOut = await _auth.LogOutAsync(Account);

        // TODO: Handle only when the logging out failed. Success automatically updates the UI
        //  because the accounts changed.
    }

    private void OnActiveAccountChanged(object? sender, EventArgs eventArgs)
    {
        Account = _auth?.ActiveAccount;
    }

    partial void OnAccountChanged(Account? value)
    {
        // Manually report computed properties' changes
        OnPropertyChanged(nameof(ScreenTitle));
        OnPropertyChanged(nameof(HasMicrosoftAccount));
        OnPropertyChanged(nameof(HasAccountProblem));
        OnPropertyChanged(nameof(AccountProblemTitle));
        OnPropertyChanged(nameof(AccountProblemMessage));
        OnPropertyChanged(nameof(HasMinecraftLicense));
        OnPropertyChanged(nameof(XboxStateTitle));
        OnPropertyChanged(nameof(XboxStateMessage));
    }
}
