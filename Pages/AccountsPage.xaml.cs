using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Tman.Core;

namespace Tman.Pages;

public partial class AccountsPage : UserControl, IPage
{
    LocalAccountData? _data;
    bool _busy;

    public AccountsPage()
    {
        InitializeComponent();
        Ui.SetupGrid(Users);
        Ui.SetupGrid(Groups);
        Ui.SetupGrid(Members);
        SizeChanged += (_, _) => UserDetails.MaxHeight = Math.Max(64, ActualHeight * 0.2);
        Load();
    }

    async void Load()
    {
        CountText.Text = "Loading accounts…";
        var selectedUser = (Users.SelectedItem as LocalAccount)?.Name;
        var selectedGroup = (Groups.SelectedItem as LocalGroup)?.Name;
        LocalAccountData data;
        try
        {
            data = await Task.Run(LocalAccounts.Read);
        }
        catch (Exception ex)
        {
            data = new LocalAccountData(new(), new(), "Administrators", "Couldn't read accounts: " + ex.Message, "Couldn't read groups: " + ex.Message);
        }
        _data = data;
        Users.ItemsSource = data.Accounts;
        Groups.ItemsSource = data.Groups;
        Users.SelectedItem = data.Accounts.FirstOrDefault(a => a.Name == selectedUser);
        Groups.SelectedItem = data.Groups.FirstOrDefault(g => g.Name == selectedGroup);
        CountText.Text = $"{data.Accounts.Count} accounts · {data.Accounts.Count(a => a.Enabled)} enabled · {data.Accounts.Count(a => a.IsAdmin)} administrators · {data.Groups.Count} groups";
        UsersEmpty.Text = data.AccountError ?? "Windows didn't report any local accounts.";
        UsersEmpty.Visibility = data.Accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GroupsEmpty.Text = data.GroupError ?? "Windows didn't report any local groups.";
        GroupsEmpty.Visibility = data.Groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Users_SelectionChanged(this, null!);
        Groups_SelectionChanged(this, null!);
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => Load();

    LocalAccount? Selected => Users.SelectedItem as LocalAccount;

    void Users_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var a = Selected;
        bool can = MainWindow.IsAdmin && !_busy;
        EnableButton.IsEnabled = can && a is { Enabled: false };
        DisableButton.IsEnabled = can && a is { Enabled: true };
        AddAdminButton.IsEnabled = can && a is { IsAdmin: false };
        RemoveAdminButton.IsEnabled = can && a is { IsAdmin: true };
        if (a == null)
        {
            UserDetails.Visibility = Visibility.Collapsed;
            return;
        }
        UserDetails.Visibility = Visibility.Visible;
        UserTitle.Text = a.FullName.Length > 0 ? $"{a.Name} ({a.FullName})" : a.Name;
        UserGroups.Text = a.Groups.Count > 0 ? "Member of: " + a.GroupsText : _data?.GroupError ?? "Not a member of any local group.";
        var notes = new List<string>();
        if (a.IsCurrent) notes.Add("This is the account Tman is running under.");
        if (a.LockedOut) notes.Add("Locked out after too many wrong passwords.");
        if (!a.PasswordRequired) notes.Add("Windows doesn't insist on a password for this account, though it may still have one.");
        if (a.Comment.Length > 0) notes.Add(a.Comment);
        UserNote.Text = string.Join(" ", notes);
        UserNote.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Groups_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var g = Groups.SelectedItem as LocalGroup;
        Members.ItemsSource = g?.Members;
        MembersTitle.Text = g != null ? $"MEMBERS OF {g.Name.ToUpperInvariant()}" : "MEMBERS";
        MembersEmpty.Text = g == null ? "Pick a group to see who's in it." : $"{g.Name} has no members.";
        MembersEmpty.Visibility = g == null || g.Members.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Enable_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Enabled: false } a) return;
        var builtIn = a.Name.Equals("Administrator", StringComparison.OrdinalIgnoreCase) || a.Name.Equals("Guest", StringComparison.OrdinalIgnoreCase)
            ? "\n\nThis is a built-in account that Windows leaves off for safety. Check it has a password before you rely on it." : "";
        if (Ui.Confirm($"Enable the account \"{a.Name}\"?\n\nAnyone who knows its password can sign in to this PC again.{builtIn}{CurrentWarning(a)}"))
            Change(a, $"enable {a.Name}", refuseIfLast: false, () => LocalAccounts.SetEnabled(a.Name, true));
    }

    void Disable_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Enabled: true } a) return;
        if (Ui.Confirm($"Disable the account \"{a.Name}\"?\n\nIt won't be able to sign in until it's enabled again. Its files and settings stay as they are.{CurrentWarning(a)}"))
            Change(a, $"disable {a.Name}", refuseIfLast: true, () => LocalAccounts.SetEnabled(a.Name, false));
    }

    void AddAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsAdmin: false } a || _data is not { } data) return;
        if (Ui.Confirm($"Add \"{a.Name}\" to {data.AdminGroup}?\n\nThe account gets full control of this PC, including every other account's files. It takes effect the next time the account signs in.{CurrentWarning(a)}"))
            Change(a, $"add {a.Name} to {data.AdminGroup}", refuseIfLast: false, () => LocalAccounts.SetGroupMember(data.AdminGroup, a.Name, true));
    }

    void RemoveAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { IsAdmin: true } a || _data is not { } data) return;
        if (Ui.Confirm($"Remove \"{a.Name}\" from {data.AdminGroup}?\n\nThe account becomes a standard user at its next sign-in. Anything that needs administrator rights will then need another administrator's password.{CurrentWarning(a)}"))
            Change(a, $"remove {a.Name} from {data.AdminGroup}", refuseIfLast: true, () => LocalAccounts.SetGroupMember(data.AdminGroup, a.Name, false));
    }

    static string CurrentWarning(LocalAccount a) =>
        a.IsCurrent ? "\n\nThis is the account you're using right now, so this changes your own access." : "";

    /// <summary>Applies a change. The last-administrator check runs on fresh data, in case something changed since the list loaded.</summary>
    async void Change(LocalAccount account, string description, bool refuseIfLast, Func<string?> change)
    {
        if (_busy) return;
        _busy = true;
        Users_SelectionChanged(this, null!);
        var error = await Task.Run(() =>
        {
            try
            {
                return (refuseIfLast ? LocalAccounts.LastAdminProblem(account.Name) : null) ?? change();
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        });
        _busy = false;
        if (error != null) Ui.Error($"Couldn't {description}.\n\n{error}");
        Load();
    }

    void OpenUserAccounts_Click(object sender, RoutedEventArgs e) => Open("netplwiz", "User Accounts");

    void OpenLocalUsers_Click(object sender, RoutedEventArgs e) => Open("lusrmgr.msc", "Local Users and Groups");

    static void Open(string file, string name)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The UAC prompt was cancelled.
        }
        catch (Exception ex)
        {
            Ui.Error($"Couldn't open {name}: {ex.Message}\n\nWindows Home doesn't include Local Users and Groups.");
        }
    }
}
