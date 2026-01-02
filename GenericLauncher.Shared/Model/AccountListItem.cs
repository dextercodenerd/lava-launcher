using GenericLauncher.Database.Model;

namespace GenericLauncher.Model;

/// <summary>
/// The easiest way to add extra item into a ComboBox is to have the real data wrapped and an extra
/// flag for easy template switching.
/// </summary>
/// <param name="Account"></param>
/// <param name="IsLogin">true for a special item in the accounts list that will trigger new login</param>
public record AccountListItem(
    Account? Account = null,
    bool IsLogin = false
);