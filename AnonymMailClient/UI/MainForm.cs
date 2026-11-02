using AnonymMailClient.Models;
using AnonymMailClient.Services;
using AnonymMailClient.Storage;
using AnonymMailClient.Tor;

namespace AnonymMailClient.UI;

public sealed class MainForm : Form
{
    readonly NotifyIcon notifyIcon;
    readonly ContextMenuStrip trayMenu;

    readonly Label lblTorStatus;
    readonly Button btnCheckAll;
    readonly Button btnCompose;
    readonly Button btnNewAccount;
    readonly Button btnOpenData;

    readonly ListBox lstAccounts;
    readonly Button btnDeleteAccount;

    readonly Label lblCurrentAccount;
    readonly Button btnCopyEmail;
    readonly Button btnRefreshAccount;
    readonly Label lblLastCheck;

    readonly ListView lstMessages;
    readonly Panel pnlMessageDetail;
    readonly Label lblDetailSubject;
    readonly Label lblDetailMeta;
    readonly TextBox txtDetailBody;

    MailAccount? selectedAccount;
    MailMessageSummary? selectedMessage;
    bool isExitingFromTray;

    public MainForm()
    {
        Text = "AnonymMailClient - Anonymous Tor Email";
        Size = new Size(1060, 680);
        MinimumSize = new Size(820, 500);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(18, 18, 20);
        ForeColor = Color.FromArgb(240, 240, 245);
        Font = new Font("Segoe UI", 9.5F, FontStyle.Regular);

        // System Tray Menu & NotifyIcon
        trayMenu = new ContextMenuStrip();
        var menuOpen = trayMenu.Items.Add("Open AnonymMailClient", null, (_, _) => RestoreFromTray());
        menuOpen.Font = new Font(trayMenu.Font, FontStyle.Bold);
        trayMenu.Items.Add("Check Mail Now", null, async (_, _) => await BackgroundMailMonitor.CheckAllNowAsync());
        trayMenu.Items.Add("Compose Email...", null, (_, _) => OpenComposeDialog());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Exit", null, (_, _) =>
        {
            isExitingFromTray = true;
            Application.Exit();
        });

        Icon? appIcon = null;
        try
        {
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("AnonymMailClient.appicon.ico");
            if (stream != null)
                appIcon = new Icon(stream);
            else if (File.Exists(Path.Combine(AppPaths.Root, "appicon.ico")))
                appIcon = new Icon(Path.Combine(AppPaths.Root, "appicon.ico"));
        }
        catch { }

        if (appIcon != null)
            Icon = appIcon;

        notifyIcon = new NotifyIcon
        {
            Text = "AnonymMailClient",
            ContextMenuStrip = trayMenu,
            Visible = true,
            Icon = appIcon ?? SystemIcons.Shield
        };
        notifyIcon.DoubleClick += (_, _) => RestoreFromTray();

        // ── TOP HEADER BAR ──
        var pnlHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = 56,
            BackColor = Color.FromArgb(24, 24, 27),
            Padding = new Padding(16, 10, 16, 10)
        };

        var lblAppTitle = new Label
        {
            Text = "AnonymMailClient",
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(14, 15)
        };

        lblTorStatus = new Label
        {
            Text = "● Tor: Initializing...",
            ForeColor = Color.FromArgb(251, 191, 36),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(180, 18)
        };

        btnCheckAll = CreateHeaderButton("🔄 Check Now", 360, (_, _) => _ = CheckAllSafeAsync());
        btnCompose = CreateHeaderButton("✉️ Compose", 480, (_, _) => OpenComposeDialog());
        btnNewAccount = CreateHeaderButton("➕ New Account", 590, (_, _) => OpenNewAccountDialog());
        btnOpenData = CreateHeaderButton("📁 Data Folder", 725, (_, _) => OpenDataFolder());

        pnlHeader.Controls.Add(lblAppTitle);
        pnlHeader.Controls.Add(lblTorStatus);
        pnlHeader.Controls.Add(btnCheckAll);
        pnlHeader.Controls.Add(btnCompose);
        pnlHeader.Controls.Add(btnNewAccount);
        pnlHeader.Controls.Add(btnOpenData);

        // ── MAIN SPLIT CONTAINER ──
        var splitMain = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 270,
            SplitterWidth = 4,
            BackColor = Color.FromArgb(39, 39, 42)
        };

        // ── LEFT SIDEBAR (Accounts) ──
        var pnlLeft = splitMain.Panel1;
        pnlLeft.BackColor = Color.FromArgb(24, 24, 27);
        pnlLeft.Padding = new Padding(12, 10, 12, 10);

        var lblAccountsHeader = new Label
        {
            Text = "ACCOUNTS",
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(161, 161, 170),
            Dock = DockStyle.Top,
            Height = 24
        };

        lstAccounts = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(30, 30, 34),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
            ItemHeight = 26
        };
        lstAccounts.SelectedIndexChanged += (_, _) => OnSelectedAccountChanged();

        var pnlAccountButtons = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            Padding = new Padding(0, 6, 0, 0)
        };

        btnDeleteAccount = new Button
        {
            Text = "🗑️ Delete Account",
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(63, 63, 70),
            ForeColor = Color.FromArgb(248, 113, 113),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        btnDeleteAccount.FlatAppearance.BorderSize = 0;
        btnDeleteAccount.Click += (_, _) => OnDeleteAccountClicked();
        pnlAccountButtons.Controls.Add(btnDeleteAccount);

        pnlLeft.Controls.Add(lstAccounts);
        pnlLeft.Controls.Add(lblAccountsHeader);
        pnlLeft.Controls.Add(pnlAccountButtons);

        // ── RIGHT CONTENT (Messages & Detail) ──
        var pnlRight = splitMain.Panel2;
        pnlRight.BackColor = Color.FromArgb(18, 18, 20);

        // Account Top Bar
        var pnlAccountTop = new Panel
        {
            Dock = DockStyle.Top,
            Height = 44,
            BackColor = Color.FromArgb(28, 28, 32),
            Padding = new Padding(14, 8, 14, 8)
        };

        lblCurrentAccount = new Label
        {
            Text = "No account selected",
            Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(14, 10)
        };

        btnCopyEmail = new Button
        {
            Text = "📋 Copy",
            Location = new Point(360, 8),
            Size = new Size(68, 28),
            BackColor = Color.FromArgb(63, 63, 70),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        btnCopyEmail.FlatAppearance.BorderSize = 0;
        btnCopyEmail.Click += (_, _) =>
        {
            if (selectedAccount != null)
            {
                Clipboard.SetText(selectedAccount.EmailAddress);
                btnCopyEmail.Text = "Copied!";
                var t = new System.Windows.Forms.Timer { Interval = 1500 };
                t.Tick += (s, e) => { btnCopyEmail.Text = "📋 Copy"; t.Stop(); t.Dispose(); };
                t.Start();
            }
        };

        btnRefreshAccount = new Button
        {
            Text = "🔄 Refresh",
            Location = new Point(436, 8),
            Size = new Size(80, 28),
            BackColor = Color.FromArgb(63, 63, 70),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        btnRefreshAccount.FlatAppearance.BorderSize = 0;
        btnRefreshAccount.Click += async (_, _) => await RefreshCurrentAccountSafeAsync();

        lblLastCheck = new Label
        {
            Text = "",
            ForeColor = Color.FromArgb(161, 161, 170),
            AutoSize = true,
            Location = new Point(530, 14)
        };

        pnlAccountTop.Controls.Add(lblCurrentAccount);
        pnlAccountTop.Controls.Add(btnCopyEmail);
        pnlAccountTop.Controls.Add(btnRefreshAccount);
        pnlAccountTop.Controls.Add(lblLastCheck);

        // Split view for Message List (top) and Message Detail (bottom)
        var splitRight = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 240,
            SplitterWidth = 4,
            BackColor = Color.FromArgb(39, 39, 42)
        };

        // Upper: Message List
        lstMessages = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            BackColor = Color.FromArgb(24, 24, 27),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.None,
            HeaderStyle = ColumnHeaderStyle.Nonclickable
        };
        lstMessages.Columns.Add("Status", 60);
        lstMessages.Columns.Add("From", 220);
        lstMessages.Columns.Add("Subject", 340);
        lstMessages.Columns.Add("Date", 150);
        lstMessages.SelectedIndexChanged += async (_, _) => await OnSelectedMessageChangedAsync();

        splitRight.Panel1.Controls.Add(lstMessages);

        // Lower: Message Detail
        pnlMessageDetail = splitRight.Panel2;
        pnlMessageDetail.BackColor = Color.FromArgb(22, 22, 25);
        pnlMessageDetail.Padding = new Padding(16, 12, 16, 12);

        lblDetailSubject = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            ForeColor = Color.White,
            Text = "Select a message to view"
        };

        lblDetailMeta = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = Color.FromArgb(161, 161, 170),
            Text = ""
        };

        txtDetailBody = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(28, 28, 32),
            ForeColor = Color.FromArgb(240, 240, 245),
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 10F, FontStyle.Regular)
        };

        pnlMessageDetail.Controls.Add(txtDetailBody);
        pnlMessageDetail.Controls.Add(lblDetailMeta);
        pnlMessageDetail.Controls.Add(lblDetailSubject);

        pnlRight.Controls.Add(splitRight);
        pnlRight.Controls.Add(pnlAccountTop);

        Controls.Add(splitMain);
        Controls.Add(pnlHeader);

        // Events
        Load += async (_, _) => await OnFormLoadAsync();
        FormClosing += OnFormClosing;
        Resize += OnFormResize;

        BackgroundMailMonitor.NewMailReceived += (account, newMsgs) =>
        {
            Invoke(() =>
            {
                var first = newMsgs.FirstOrDefault();
                if (first != null)
                {
                    notifyIcon.ShowBalloonTip(5000,
                        $"New Email on {account.EmailAddress}",
                        $"{first.From}: {first.Subject}",
                        ToolTipIcon.Info);
                }
                ReloadAccountsList();
                if (selectedAccount?.Id == account.Id)
                    ReloadMessagesList();
            });
        };

        BackgroundMailMonitor.AccountsUpdated += () =>
        {
            Invoke(() =>
            {
                ReloadAccountsList();
                if (selectedAccount != null)
                    ReloadMessagesList();
            });
        };
    }

    Button CreateHeaderButton(string text, int x, EventHandler onClick)
    {
        var btn = new Button
        {
            Text = text,
            Location = new Point(x, 12),
            Size = new Size(110, 32),
            BackColor = Color.FromArgb(45, 45, 50),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.Click += onClick;
        return btn;
    }

    async Task OnFormLoadAsync()
    {
        ReloadAccountsList();

        // Start Tor in background
        _ = Task.Run(async () =>
        {
            try
            {
                var endpoint = await TorService.RequireEndpointAsync(CancellationToken.None);
                Invoke(() =>
                {
                    lblTorStatus.Text = $"● Tor: Ready ({endpoint.Host}:{endpoint.Port})";
                    lblTorStatus.ForeColor = Color.FromArgb(52, 211, 153);
                    notifyIcon.Text = $"AnonymMailClient - Tor Ready ({endpoint.Port})";
                });
            }
            catch (Exception ex)
            {
                Invoke(() =>
                {
                    lblTorStatus.Text = $"● Tor: Error ({ex.Message})";
                    lblTorStatus.ForeColor = Color.FromArgb(248, 113, 113);
                });
            }
        });

        BackgroundMailMonitor.Start();
    }

    void ReloadAccountsList()
    {
        var accounts = AccountStore.LoadAccounts();
        string? previouslySelectedId = selectedAccount?.Id;

        lstAccounts.BeginUpdate();
        lstAccounts.Items.Clear();

        foreach (var acc in accounts)
        {
            string itemText = acc.UnreadCount > 0
                ? $"{acc.EmailAddress}  ({acc.UnreadCount})"
                : acc.EmailAddress;
            lstAccounts.Items.Add(new AccountListItem(acc, itemText));
        }

        lstAccounts.EndUpdate();

        if (previouslySelectedId != null)
        {
            for (int i = 0; i < lstAccounts.Items.Count; i++)
            {
                if (((AccountListItem)lstAccounts.Items[i]).Account.Id == previouslySelectedId)
                {
                    lstAccounts.SelectedIndex = i;
                    return;
                }
            }
        }

        if (lstAccounts.SelectedIndex < 0 && lstAccounts.Items.Count > 0)
            lstAccounts.SelectedIndex = 0;
    }

    void OnSelectedAccountChanged()
    {
        if (lstAccounts.SelectedItem is AccountListItem item)
        {
            selectedAccount = item.Account;
            lblCurrentAccount.Text = selectedAccount.EmailAddress;
            lblLastCheck.Text = selectedAccount.LastCheckedUtc.HasValue
                ? $"Checked: {selectedAccount.LastCheckedUtc.Value.ToLocalTime():HH:mm:ss}"
                : "Not checked yet";
            ReloadMessagesList();
        }
        else
        {
            selectedAccount = null;
            lblCurrentAccount.Text = "No account selected";
            lblLastCheck.Text = "";
            lstMessages.Items.Clear();
            ClearMessageDetail();
        }
    }

    void ReloadMessagesList()
    {
        if (selectedAccount == null) return;
        var messages = MessageCacheStore.LoadMessages(selectedAccount.Id);

        lstMessages.BeginUpdate();
        lstMessages.Items.Clear();

        foreach (var msg in messages)
        {
            var lvi = new ListViewItem(msg.IsRead ? "✓ Read" : "● New")
            {
                ForeColor = msg.IsRead ? Color.FromArgb(161, 161, 170) : Color.FromArgb(52, 211, 153),
                Tag = msg
            };
            lvi.SubItems.Add(msg.From);
            lvi.SubItems.Add(msg.Subject);
            lvi.SubItems.Add(msg.DateDisplay);
            lstMessages.Items.Add(lvi);
        }

        lstMessages.EndUpdate();
    }

    async Task OnSelectedMessageChangedAsync()
    {
        if (lstMessages.SelectedItems.Count == 0 || lstMessages.SelectedItems[0].Tag is not MailMessageSummary msg)
        {
            ClearMessageDetail();
            return;
        }

        selectedMessage = msg;
        lblDetailSubject.Text = msg.Subject;
        lblDetailMeta.Text = $"From: {msg.From}   |   Date: {msg.DateDisplay}";

        if (!string.IsNullOrEmpty(msg.BodyText))
        {
            txtDetailBody.Text = msg.BodyText;
        }
        else
        {
            txtDetailBody.Text = "Fetching full message body over Tor...";
            if (selectedAccount != null)
            {
                try
                {
                    var detailed = await GuerrillaMailService.FetchMessageDetailAsync(selectedAccount, msg.Id);
                    MessageCacheStore.UpsertMessage(selectedAccount.Id, detailed);
                    if (selectedMessage?.Id == msg.Id)
                        txtDetailBody.Text = detailed.BodyText;
                }
                catch (Exception ex)
                {
                    if (selectedMessage?.Id == msg.Id)
                        txtDetailBody.Text = $"Could not download message body: {ex.Message}";
                }
            }
        }

        if (!msg.IsRead && selectedAccount != null)
        {
            MessageCacheStore.MarkAsRead(selectedAccount.Id, msg.Id);
            msg.IsRead = true;
            lstMessages.SelectedItems[0].Text = "✓ Read";
            lstMessages.SelectedItems[0].ForeColor = Color.FromArgb(161, 161, 170);
            selectedAccount.UnreadCount = Math.Max(0, selectedAccount.UnreadCount - 1);
            AccountStore.SaveOrUpdateAccount(selectedAccount);
            ReloadAccountsList();
        }
    }

    void ClearMessageDetail()
    {
        lblDetailSubject.Text = "Select a message to view";
        lblDetailMeta.Text = "";
        txtDetailBody.Text = "";
    }

    async Task RefreshCurrentAccountSafeAsync()
    {
        if (selectedAccount == null) return;
        btnRefreshAccount.Enabled = false;
        btnRefreshAccount.Text = "Refreshing...";
        try
        {
            var remoteMsgs = await GuerrillaMailService.CheckMessagesAsync(selectedAccount);
            foreach (var m in remoteMsgs)
                MessageCacheStore.UpsertMessage(selectedAccount.Id, m);

            selectedAccount.LastCheckedUtc = DateTime.UtcNow;
            selectedAccount.UnreadCount = MessageCacheStore.LoadMessages(selectedAccount.Id).Count(m => !m.IsRead);
            AccountStore.SaveOrUpdateAccount(selectedAccount);

            ReloadAccountsList();
            ReloadMessagesList();
            lblLastCheck.Text = $"Checked: {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Failed to refresh account: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            btnRefreshAccount.Enabled = true;
            btnRefreshAccount.Text = "🔄 Refresh";
        }
    }

    async Task CheckAllSafeAsync()
    {
        btnCheckAll.Enabled = false;
        btnCheckAll.Text = "Checking...";
        try
        {
            await BackgroundMailMonitor.CheckAllNowAsync();
        }
        finally
        {
            btnCheckAll.Enabled = true;
            btnCheckAll.Text = "🔄 Check Now";
        }
    }

    void OpenNewAccountDialog()
    {
        using var dlg = new NewAccountDialog();
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.CreatedAccount != null)
        {
            AccountStore.SaveOrUpdateAccount(dlg.CreatedAccount);
            ReloadAccountsList();
            for (int i = 0; i < lstAccounts.Items.Count; i++)
            {
                if (((AccountListItem)lstAccounts.Items[i]).Account.Id == dlg.CreatedAccount.Id)
                {
                    lstAccounts.SelectedIndex = i;
                    break;
                }
            }
        }
    }

    void OpenComposeDialog()
    {
        using var dlg = new ComposeDialog(selectedAccount);
        dlg.ShowDialog(this);
    }

    void OnDeleteAccountClicked()
    {
        if (selectedAccount == null) return;
        var res = MessageBox.Show(this,
            $"Remove account '{selectedAccount.EmailAddress}' from local client?\n(Guerrilla Mail inboxes are permanent; you can re-claim it anytime with the same username).",
            "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (res == DialogResult.Yes)
        {
            AccountStore.RemoveAccount(selectedAccount.Id);
            ReloadAccountsList();
        }
    }

    void OpenDataFolder()
    {
        Directory.CreateDirectory(AppPaths.UserData);
        Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.Root) { UseShellExecute = true });
    }

    void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    void OnFormResize(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized && Settings.MinimizeToTray.Value)
        {
            Hide();
        }
    }

    void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!isExitingFromTray && Settings.CloseToTray.Value && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        BackgroundMailMonitor.Stop();
        TorProcess.Stop();
    }

    sealed record AccountListItem(MailAccount Account, string Display)
    {
        public override string ToString() => Display;
    }
}
