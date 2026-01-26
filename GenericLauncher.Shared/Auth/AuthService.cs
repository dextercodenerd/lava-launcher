using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GenericLauncher.Database;
using GenericLauncher.Database.Model;
using Microsoft.Extensions.Logging;

namespace GenericLauncher.Auth;

public enum LoginStep
{
    BrowserLogin,
    MicrosoftToken,
    XboxLive,
    MinecraftAuth,
    MinecraftProfile,
}

public class AuthService
{
    private readonly ILogger? _logger;
    private readonly Authenticator _auth;
    private readonly LauncherRepository _repository;

    private readonly SemaphoreSlim _lock = new(1, 1);

    // We need manual backing fields to control the exact moment of state update vs event firing.
    // This allows us to update the state effectively "atomically" from the perspective of the UI thread
    // which listens to the events. If we used auto-properties, we couldn't update both Accounts and 
    // ActiveAccount before firing the first event, leading to a glitch states where the UI sees
    // new Accounts but old ActiveAccount.
    private ImmutableList<Account> _accounts = [];
    public ImmutableList<Account> Accounts => _accounts;
    private Account? _activeAccount;
    public Account? ActiveAccount => _activeAccount;

    private readonly SemaphoreSlim _authGate = new(1, 1);
    private readonly SemaphoreSlim _authCtsGate = new(1, 1);
    private CancellationTokenSource? _authCts;
    private const int AuthTimeoutMinutes = 1;

    public event EventHandler? AccountsChanged;
    public event EventHandler? ActiveAccountChanged;

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

    // We need an async setter with a lock to prevent race conditions with RefreshAccountsAsync().
    // RefreshAccountsAsync() runs on a background thread and updates the ActiveAccount.
    // If the UI tries to set the ActiveAccount at the same time (e.g., user selecting a different
    // account in the combobox), we need to ensure one update doesn't overwrite the other
    // inconsistently. Property setters cannot be async, hence the method.
    public async Task SetActiveAccountAsync(Account? account)
    {
        var changed = false;
        await _lock.WaitAsync();
        try
        {
            if (!Equals(_activeAccount, account))
            {
                _activeAccount = account;
                changed = true;
            }
        }
        finally
        {
            _lock.Release();
        }

        if (changed)
        {
            ActiveAccountChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task RefreshAccountsAsync(Account? active)
    {
        // TODO: Set a better username for UI, when the MS account doesn't have an Xbox account
        var accounts = (await _repository.GetAllAccountsAsync())
            .Select(a => a.Username is not null
                ? a
                : a with { Username = "New Account", })
            .ToImmutableList();

        var activeChanged = false;

        await _lock.WaitAsync();
        try
        {
            _accounts = accounts;

            Account? newActive = null;

            if (active is not null)
            {
                newActive = _accounts.FirstOrDefault(a => a.Id == active.Id);
            }

            if (newActive is null)
            {
                // TODO: Persist and load the ActiveAccount
                newActive = _accounts.FirstOrDefault();
            }

            if (!Equals(_activeAccount, newActive))
            {
                _activeAccount = newActive;
                activeChanged = true;
            }
        }
        finally
        {
            _lock.Release();
        }

        AccountsChanged?.Invoke(this, EventArgs.Empty);

        if (activeChanged)
        {
            ActiveAccountChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task<Account> AuthenticateAsync(IProgress<LoginStep>? progress = null)
    {
        await _authGate.WaitAsync().ConfigureAwait(false);

        try
        {
            // Define cts with `using` so it auto-disposes at end of scope automatically. We use
            // separate cancellation token sources, so we can distinguish if the cancellation was an
            // automatic time-out or a manually triggered cancellation.
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(AuthTimeoutMinutes));
            using var manualCts = new CancellationTokenSource();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, manualCts.Token);

            await _authCtsGate.WaitAsync().ConfigureAwait(false);
            _authCts = manualCts;
            _authCtsGate.Release();

            try
            {
                var acc = await _auth.AuthenticateAsync(linkedCts.Token, progress).ConfigureAwait(false);
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
            catch (OperationCanceledException)
            {
                if (timeoutCts.IsCancellationRequested)
                {
                    _logger?.LogWarning("Authentication timed out");
                    throw new TimeoutException("Authentication timed out");
                }

                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Authentication failed");
                throw;
            }
            finally
            {
                await _authCtsGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    // Only null it out if it's still OUR cts
                    if (ReferenceEquals(_authCts, manualCts))
                    {
                        _authCts = null;
                    }
                }
                finally
                {
                    _authCtsGate.Release();
                }
            }
        }
        finally
        {
            _authGate.Release();
        }
    }

    public async Task<bool> CancelRunningAuthAsync()
    {
        await _authCtsGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_authCts is null)
            {
                _logger?.LogDebug("No authentication in progress to cancel");
                return false;
            }

            _logger?.LogInformation("Cancelling authentication flow");

            await _authCts.CancelAsync().ConfigureAwait(false);
            return true;
        }
        catch (ObjectDisposedException)
        {
            _logger?.LogDebug("Authentication CTS already disposed during cancellation attempt");
            return false;
        }
        finally
        {
            _authCtsGate.Release();
        }
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

    public async Task<Account> AuthenticateAccountAsync(Account acc, bool force = false)
    {
        // TODO: Call this only if the MC token is expired, or near expiration, or force == true,
        //  because this refreshes all the tokens and slows down the start time.

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

    public async Task<bool> LogOutAsync(Account account)
    {
        var success = await _repository.RemoveAccountAsync(account);
        await RefreshAccountsAsync(account);
        return success;
    }
}
