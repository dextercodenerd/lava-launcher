using System.Threading.Tasks;
using Avalonia.Media;
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

    public bool HasMinecraftLicense => Account?.HasMinecraftLicense == true;

    public bool ShowXboxStateMessage => Account?.XboxAccountState != XboxAccountState.Ok;

    public string XboxStateTitle => Account?.XboxAccountState switch
    {
        XboxAccountState.Ok => "All good",
        XboxAccountState.Missing => "Xbox Account Missing",
        XboxAccountState.Banned => "Xbox Account Banned",
        XboxAccountState.NotAvailable => "Xbox Account Not Available",
        XboxAccountState.AgeVerificationMissing => "Age Verification Required",
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

    public IBrush XboxStateBackground => Account?.XboxAccountState switch
    {
        XboxAccountState.Banned => new SolidColorBrush(Color.Parse("#F8D7DA")),
        XboxAccountState.AgeVerificationMissing => new SolidColorBrush(Color.Parse("#FFF3CD")),
        _ => new SolidColorBrush(Color.Parse("#D1ECF1")),
    };

    public IBrush XboxStateTitleColor => Account?.XboxAccountState switch
    {
        XboxAccountState.Banned => new SolidColorBrush(Color.Parse("#721C24")),
        XboxAccountState.AgeVerificationMissing => new SolidColorBrush(Color.Parse("#856404")),
        _ => new SolidColorBrush(Color.Parse("#0C5460")),
    };

    public bool ShowRefreshButton => Account is null || Account?.XboxAccountState != XboxAccountState.Ok;

    public ProfileViewModel() : this(null)
    {
    }

    public ProfileViewModel(
        AuthService? authService = null,
        ILogger? logger = null)
    {
        _logger = logger;
        _auth = authService;
    }

    [RelayCommand]
    private async Task OnClickRefreshAccount()
    {
        if (_auth is null || Account is null)
        {
            return;
        }

        await _auth.AuthenticateAccountAsync(Account);
    }

    partial void OnAccountChanged(Account? value)
    {
        // Manually report computed properties' changes
        OnPropertyChanged(nameof(HasMinecraftLicense));
        OnPropertyChanged(nameof(ShowXboxStateMessage));
        OnPropertyChanged(nameof(XboxStateTitle));
        OnPropertyChanged(nameof(XboxStateMessage));
        OnPropertyChanged(nameof(XboxStateBackground));
        OnPropertyChanged(nameof(XboxStateTitleColor));
        OnPropertyChanged(nameof(ShowRefreshButton));
    }
}
