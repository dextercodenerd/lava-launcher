using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using GenericLauncher.Auth;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Screens.EmptyStateScreen;

public partial class EmptyStateViewModel : ViewModelBase
{
    private readonly ILogger? _logger;
    private readonly AuthService? _auth;

    public EmptyStateViewModel() : this(null)
    {
    }

    public EmptyStateViewModel(
        AuthService? authService = null,
        ILogger? logger = null)
    {
        _logger = logger;
        _auth = authService;
    }

    [RelayCommand]
    private async Task ClickLogin()
    {
        if (_auth is null)
        {
            return;
        }

        await _auth.AuthenticateAsync();
    }
}
