using System.Collections.Generic;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
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

    public static readonly StyledProperty<ICommand?> ClickAccountProperty =
        AvaloniaProperty.Register<AccountSelector, ICommand?>(
            nameof(ClickAccount));

    public ICommand? ClickAccount
    {
        get => GetValue(ClickAccountProperty);
        set => SetValue(ClickAccountProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        e.NameScope.Get<Border>("PART_AvatarContainer").PointerPressed += OnClickAvatarHandler;
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

    private void OnClickAvatarHandler(object? sender, PointerPressedEventArgs args)
    {
        if (!Equals(sender, args.Source))
        {
            return;
        }

        ClickAccount?.Execute(null);
    }
}
