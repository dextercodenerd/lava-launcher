using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GenericLauncher.Database;
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
        if (acc.Profile is null)
        {
            // TODO: throw a custom exception?
            throw new InvalidOperationException("user doesn't have a Minecraft profile");
        }

        var account = new Account(
            acc.Profile.Id,
            acc.XboxUserId,
            acc.Profile.Name,
            acc.HasMinecraft,
            acc.Profile.Skins.FirstOrDefault(s => s.State == "ACTIVE")?.Url,
            acc.Profile.Capes.FirstOrDefault(s => s.State == "ACTIVE")?.Url,
            acc.MinecraftAccessToken,
            acc.MsRefreshToken,
            acc.ExpiresAt
        );

        await _repository.SaveAccountAsync(account);
        await RefreshAccountsAsync(account);

        return account;
    }

    public async Task<Account> AuthenticateAccountAsync(Account acc)
    {
        var refreshedAccount = await _auth.AuthenticateWithMsRefreshTokenAsync(acc.RefreshToken)
                               ?? throw new InvalidOperationException("problem refreshing MS token");

        if (refreshedAccount.Profile is null)
        {
            // TODO: throw a custom exception?
            throw new InvalidOperationException("user doesn't have a Minecraft profile");
        }

        var newAcc = new Account(
            refreshedAccount.Profile.Id,
            refreshedAccount.XboxUserId,
            refreshedAccount.Profile.Name,
            refreshedAccount.HasMinecraft,
            refreshedAccount.Profile.Skins.FirstOrDefault(s => s.State == "ACTIVE")?.Url,
            refreshedAccount.Profile.Capes.FirstOrDefault(s => s.State == "ACTIVE")?.Url,
            refreshedAccount.MinecraftAccessToken,
            refreshedAccount.MsRefreshToken,
            refreshedAccount.ExpiresAt
        );

        await _repository.SaveAccountAsync(newAcc);
        await RefreshAccountsAsync(newAcc);

        return newAcc;
    }
}
