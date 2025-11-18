using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using GenericLauncher.Model;

namespace GenericLauncher.Controls;

public class AccountSelector : TemplatedControl
{
    public static readonly StyledProperty<IEnumerable<AccountListItem>?> AccountsProperty =
        AvaloniaProperty.Register<AccountSelector, IEnumerable<AccountListItem>?>(
            nameof(Accounts));

    public IEnumerable<AccountListItem>? Accounts
    {
        get => GetValue(AccountsProperty);
        set => SetValue(AccountsProperty, value);
    }

    public static readonly StyledProperty<AccountListItem?> SelectedAccountProperty =
        AvaloniaProperty.Register<AccountSelector, AccountListItem?>(
            nameof(SelectedAccount),
            defaultBindingMode: BindingMode.TwoWay);

    public AccountListItem? SelectedAccount
    {
        get => GetValue(SelectedAccountProperty);
        set => SetValue(SelectedAccountProperty, value);
    }

    public static readonly StyledProperty<string?> AccountNameProperty =
        AvaloniaProperty.Register<AccountSelector, string?>(
            nameof(AccountName));

    public string? AccountName
    {
        get => GetValue(AccountNameProperty);
        set => SetValue(AccountNameProperty, value);
    }

    public static readonly StyledProperty<string?> AvatarUrlProperty =
        AvaloniaProperty.Register<AccountSelector, string?>(
            nameof(AvatarUrl));

    public string? AvatarUrl
    {
        get => GetValue(AvatarUrlProperty);
        set => SetValue(AvatarUrlProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SelectedAccountProperty)
        {
            UpdateDisplayProperties();
        }
    }

    private void UpdateDisplayProperties()
    {
        if (SelectedAccount is not null)
        {
            AccountName = SelectedAccount.Account?.Username;
            AvatarUrl = SelectedAccount.Account?.SkinUrl;
        }
        else
        {
            AccountName = "Select an account";
            AvatarUrl = null;
        }
    }
}
