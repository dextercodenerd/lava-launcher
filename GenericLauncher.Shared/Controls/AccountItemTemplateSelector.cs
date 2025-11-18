using Avalonia.Controls;
using Avalonia.Controls.Templates;
using GenericLauncher.Model;

namespace GenericLauncher.Controls;

public class AccountItemTemplateSelector : IDataTemplate
{
    public IDataTemplate? ItemTemplate { get; set; }
    public IDataTemplate? LoginTemplate { get; set; }


    public Control? Build(object? param)
    {
        if (param is AccountListItem item)
        {
            if (item.IsLogin)
            {
                return LoginTemplate?.Build(param);
            }
            else
            {
                return ItemTemplate?.Build(param);
            }
        }

        return null;
    }

    public bool Match(object? data)
    {
        return data is AccountListItem;
    }
}
