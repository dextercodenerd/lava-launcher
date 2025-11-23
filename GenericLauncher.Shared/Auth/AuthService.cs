using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GenericLauncher.Database;
using GenericLauncher.Database.Model;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Auth;

public class AuthService
{
    private readonly ILogger? _logger;
    private readonly Authenticator _auth;
    private readonly LauncherRepository _repository;

    private readonly SemaphoreSlim _lock = new(1, 1);
    public ImmutableList<Account> Accounts = [];
    public Account? ActiveAccount;

    public event EventHandler? AccountsChanged;

    public AuthService(Authenticator auth, LauncherRepository repository, ILogger? logger)
    {
        _auth = auth;
        _repository = repository;
        _logger = logger;

        RefreshAccountsAsync(null)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    _logger?.LogError(t.Exception, "Problem loading accounts from database");
                }
            });
    }

    private async Task RefreshAccountsAsync(Account? active)
    {
        var accounts = (await _repository.GetAllAccountsAsync()).ToImmutableList();

        await _lock.WaitAsync();
        try
        {
            Accounts = accounts;

            if (active is not null)
            {
                var found = Accounts.FirstOrDefault(a => a.Id == active.Id);
                if (found is not null)
                {
                    ActiveAccount = found;
                    return;
                }
            }

            // TODO: Persist and load last active account
            ActiveAccount = accounts.FirstOrDefault();
        }
        finally
        {
            _lock.Release();
            AccountsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task<Account> AuthenticateAsync()
    {
        var acc = await _auth.AuthenticateAsync();
        var accState = XstsFailureToXboxAccountState(acc);

        var account = new Account(
            acc.UniqueUserId,
            accState,
            acc.Profile?.Id,
            acc.XboxUserId,
            acc.Profile?.Name,
            acc.HasMinecraft,
            acc.Profile?.Skins.FirstOrDefault(s => s.State == "ACTIVE")?.Url,
            acc.Profile?.Capes.FirstOrDefault(s => s.State == "ACTIVE")?.Url,
            acc.MinecraftAccessToken,
            acc.MicrosoftRefreshToken,
            acc.ExpiresAt
        );

        await _repository.SaveAccountAsync(account);
        await RefreshAccountsAsync(account);

        return account;
    }

    private static XboxAccountState XstsFailureToXboxAccountState(MinecraftAccount acc)
    {
        var accState = acc.XboxAccountProblem switch
        {
            null => XboxAccountState.Ok,
            XstsFailureReason.XboxAccountMissing => XboxAccountState.Missing,
            XstsFailureReason.XboxAccountBanned => XboxAccountState.Banned,
            XstsFailureReason.XboxAccountNotAvailable => XboxAccountState.NotAvailable,
            XstsFailureReason.AgeVerificationRequired => XboxAccountState.AgeVerificationMissing,
            _ => XboxAccountState.Unknown,
        };
        return accState;
    }

    public async Task<Account> AuthenticateAccountAsync(Account acc)
    {
        var refreshedAccount = await _auth.AuthenticateWithMsRefreshTokenAsync(acc.RefreshToken);
        var accState = XstsFailureToXboxAccountState(refreshedAccount);

        var newAcc = new Account(
            refreshedAccount.UniqueUserId,
            accState,
            refreshedAccount.Profile?.Id,
            refreshedAccount.XboxUserId,
            refreshedAccount.Profile?.Name,
            refreshedAccount.HasMinecraft,
            refreshedAccount.Profile?.Skins.FirstOrDefault(s => s.State == "ACTIVE")?.Url,
            refreshedAccount.Profile?.Capes.FirstOrDefault(s => s.State == "ACTIVE")?.Url,
            refreshedAccount.MinecraftAccessToken,
            refreshedAccount.MicrosoftRefreshToken,
            refreshedAccount.ExpiresAt
        );

        await _repository.SaveAccountAsync(newAcc);
        await RefreshAccountsAsync(newAcc);

        return newAcc;
    }
}
