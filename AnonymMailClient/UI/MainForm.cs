using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using AnonymMailClient.Models;
using AnonymMailClient.Services;
using AnonymMailClient.Storage;
using AnonymMailClient.Tor;

namespace AnonymMailClient.UI;

public sealed class MainForm : Form
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    readonly NotifyIcon notifyIcon;
    readonly ContextMenuStrip trayMenu;

    // Header Controls
    readonly Label lblTorDot;
    readonly Label lblTorStatus;
    readonly Button btnCheckAll;
    readonly Button btnCompose;
    readonly Button btnNewAccount;
    readonly Button btnOpenData;

    // Split & Sidebar
    readonly SplitContainer splitMain;
    readonly ListBox lstAccounts;
    readonly Button btnDeleteAccount;
    readonly Button btnSidebarAdd;

    // Right Content
    readonly Label lblCurrentAccount;
    readonly Button btnCopyEmail;
    readonly Button btnRefreshAccount;
    readonly Label lblLastCheck;

    readonly SplitContainer splitRight;
    readonly ListView lstMessages;
    readonly Label lblDetailSubject;
    readonly Label lblDetailMeta;
    readonly TextBox txtDetailBody;

    MailAccount? selectedAccount;
    MailMessageSummary? selectedMessage;
    bool isExitingFromTray;

    // Theme Colors
    static readonly Color BgDark = Color.FromArgb(18, 18, 22);
    static readonly Color BgHeader = Color.FromArgb(26, 26, 32);
    static readonly Color BgCard = Color.FromArgb(32, 32, 40);
    static readonly Color BgItemHover = Color.FromArgb(42, 42, 54);
    static readonly Color BgItemSelected = Color.FromArgb(50, 48, 80);
    static readonly Color BorderColor = Color.FromArgb(46, 46, 58);
    static readonly Color TextPrimary = Color.FromArgb(245, 245, 250);
    static readonly Color TextMuted = Color.FromArgb(160, 160, 175);
    static readonly Color AccentGreen = Color.FromArgb(16, 185, 129);
    static readonly Color AccentCyan = Color.FromArgb(56, 189, 248);
    static readonly Color AccentRed = Color.FromArgb(239, 68, 68);
    static readonly Color AccentYellow = Color.FromArgb(245, 158, 11);

    public MainForm()
    {
        Text = "AnonymMailClient - Anonymous Tor Email";
        Size = new Size(1100, 720);
        MinimumSize = new Size(900, 580);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BgDark;
        ForeColor = TextPrimary;
        Font = new Font("Segoe UI", 9.5F, FontStyle.Regular);

        // Enable Windows 10/11 native immersive dark title bar
        HandleCreated += (_, _) => EnableImmersiveDarkMode(Handle);

        // System Tray & Context Menu
        trayMenu = new ContextMenuStrip { BackColor = BgCard, ForeColor = TextPrimary };
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

        // ══════════════════════════════════════════════════════════════
        // 1. TOP HEADER BAR (Fully Responsive Layout)
        // ══════════════════════════════════════════════════════════════
        var pnlHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = 60,
            BackColor = BgHeader,
            Padding = new Padding(16, 10, 16, 10)
        };

        // Left Header Box (Logo + Tor Badge)
        var flpHeaderLeft = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        var lblAppTitle = new Label
        {
            Text = "AnonymMailClient",
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            ForeColor = TextPrimary,
            AutoSize = true,
            Margin = new Padding(0, 8, 16, 0)
        };

        // Tor Pill Badge
        var pnlTorBadge = new Panel
        {
            AutoSize = true,
            BackColor = Color.FromArgb(36, 36, 46),
            Padding = new Padding(10, 5, 10, 5),
            Margin = new Padding(0, 6, 0, 0)
        };
        lblTorDot = new Label
        {
            Text = "●",
            ForeColor = AccentYellow,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(6, 4)
        };
        lblTorStatus = new Label
        {
            Text = "Tor: Connecting...",
            ForeColor = Color.FromArgb(220, 220, 230),
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            AutoSize = true,
            Location = new Point(24, 6)
        };
        pnlTorBadge.Controls.Add(lblTorDot);
        pnlTorBadge.Controls.Add(lblTorStatus);

        flpHeaderLeft.Controls.Add(lblAppTitle);
        flpHeaderLeft.Controls.Add(pnlTorBadge);

        // Right Header Box (Action Buttons - Right Aligned)
        var flpHeaderRight = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        btnOpenData = CreateStyledButton("📁 Data", 85, Color.FromArgb(45, 45, 56), (_, _) => OpenDataFolder());
        btnCheckAll = CreateStyledButton("🔄 Check Now", 110, Color.FromArgb(45, 45, 56), (_, _) => _ = CheckAllSafeAsync());
        btnCompose = CreateStyledButton("✉️ Compose", 105, Color.FromArgb(55, 48, 163), (_, _) => OpenComposeDialog());
        btnNewAccount = CreateStyledButton("➕ New Account", 125, AccentGreen, (_, _) => OpenNewAccountDialog());

        flpHeaderRight.Controls.Add(btnOpenData);
        flpHeaderRight.Controls.Add(btnCheckAll);
        flpHeaderRight.Controls.Add(btnCompose);
        flpHeaderRight.Controls.Add(btnNewAccount);

        pnlHeader.Controls.Add(flpHeaderLeft);
        pnlHeader.Controls.Add(flpHeaderRight);

        // ══════════════════════════════════════════════════════════════
        // 2. MAIN SPLIT CONTAINER (Sidebar 280px Fixed + Content Auto)
        // ══════════════════════════════════════════════════════════════
        splitMain = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            SplitterWidth = 3,
            BackColor = BorderColor
        };

        // ── LEFT SIDEBAR (Accounts) ──
        var pnlLeft = splitMain.Panel1;
        pnlLeft.BackColor = BgHeader;
        pnlLeft.Padding = new Padding(12, 10, 12, 10);

        var pnlAccountsTitleBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 32,
            Padding = Padding.Empty
        };

        var lblAccountsHeader = new Label
        {
            Text = "MAIL ACCOUNTS",
            Font = new Font("Segoe UI", 8F, FontStyle.Bold),
            ForeColor = TextMuted,
            Dock = DockStyle.Left,
            AutoSize = true,
            Padding = new Padding(0, 6, 0, 0)
        };

        btnSidebarAdd = new Button
        {
            Text = "+ Add",
            Dock = DockStyle.Right,
            Size = new Size(54, 24),
            BackColor = Color.FromArgb(39, 39, 48),
            ForeColor = AccentGreen,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnSidebarAdd.FlatAppearance.BorderSize = 0;
        btnSidebarAdd.Click += (_, _) => OpenNewAccountDialog();

        pnlAccountsTitleBar.Controls.Add(lblAccountsHeader);
        pnlAccountsTitleBar.Controls.Add(btnSidebarAdd);

        lstAccounts = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = BgDark,
            ForeColor = TextPrimary,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 42
        };
        lstAccounts.DrawItem += OnDrawAccountItem;
        lstAccounts.SelectedIndexChanged += (_, _) => OnSelectedAccountChanged();

        var pnlAccountBottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            Padding = new Padding(0, 8, 0, 0)
        };

        btnDeleteAccount = new Button
        {
            Text = "🗑️ Delete Account",
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(38, 30, 36),
            ForeColor = AccentRed,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        btnDeleteAccount.FlatAppearance.BorderColor = Color.FromArgb(70, 35, 45);
        btnDeleteAccount.FlatAppearance.BorderSize = 1;
        btnDeleteAccount.Click += (_, _) => OnDeleteAccountClicked();
        pnlAccountBottom.Controls.Add(btnDeleteAccount);

        pnlLeft.Controls.Add(lstAccounts);
        pnlLeft.Controls.Add(pnlAccountsTitleBar);
        pnlLeft.Controls.Add(pnlAccountBottom);
        lstAccounts.BringToFront();

        // ══════════════════════════════════════════════════════════════
        // 3. RIGHT CONTENT (Account Header, Messages, Reader)
        // ══════════════════════════════════════════════════════════════
        var pnlRight = splitMain.Panel2;
        pnlRight.BackColor = BgDark;

        // Current Account Top Bar
        var pnlAccountTop = new Panel
        {
            Dock = DockStyle.Top,
            Height = 48,
            BackColor = Color.FromArgb(24, 24, 30),
            Padding = new Padding(16, 8, 16, 8)
        };

        lblCurrentAccount = new Label
        {
            Text = "No account selected",
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            ForeColor = TextPrimary,
            AutoSize = true,
            Dock = DockStyle.Left,
            Padding = new Padding(0, 4, 0, 0)
        };

        btnCopyEmail = new Button
        {
            Text = "📋 Copy",
            Size = new Size(68, 28),
            BackColor = Color.FromArgb(45, 45, 56),
            ForeColor = TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
            Cursor = Cursors.Hand,
            Margin = new Padding(12, 0, 0, 0)
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

        var flpAccountTopRight = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Color.Transparent
        };

        btnRefreshAccount = new Button
        {
            Text = "🔄 Refresh",
            Size = new Size(86, 28),
            BackColor = Color.FromArgb(45, 45, 56),
            ForeColor = TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        btnRefreshAccount.FlatAppearance.BorderSize = 0;
        btnRefreshAccount.Click += async (_, _) => await RefreshCurrentAccountSafeAsync();

        lblLastCheck = new Label
        {
            Text = "",
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
            AutoSize = true,
            Margin = new Padding(0, 6, 12, 0)
        };

        flpAccountTopRight.Controls.Add(btnRefreshAccount);
        flpAccountTopRight.Controls.Add(lblLastCheck);

        var pnlAccountTopLeft = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        pnlAccountTopLeft.Controls.Add(lblCurrentAccount);
        pnlAccountTopLeft.Controls.Add(btnCopyEmail);

        pnlAccountTop.Controls.Add(pnlAccountTopLeft);
        pnlAccountTop.Controls.Add(flpAccountTopRight);

        // Split view for Message List (top) and Message Detail (bottom)
        splitRight = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            FixedPanel = FixedPanel.Panel1,
            SplitterWidth = 3,
            BackColor = BorderColor
        };

        // Upper: Message List (OwnerDraw for Dark Columns & Rows)
        lstMessages = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            BackColor = BgDark,
            ForeColor = TextPrimary,
            BorderStyle = BorderStyle.None,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            OwnerDraw = true
        };
        lstMessages.Columns.Add("Status", 75);
        lstMessages.Columns.Add("From", 220);
        lstMessages.Columns.Add("Subject", 360);
        lstMessages.Columns.Add("Date", 140);
        lstMessages.DrawColumnHeader += OnDrawMessageColumnHeader;
        lstMessages.DrawItem += (_, e) => { /* Default handling via DrawSubItem */ };
        lstMessages.DrawSubItem += OnDrawMessageSubItem;
        lstMessages.SelectedIndexChanged += async (_, _) => await OnSelectedMessageChangedAsync();
        lstMessages.Resize += (_, _) => AdjustMessageColumns();

        splitRight.Panel1.Controls.Add(lstMessages);

        // Lower: Message Detail
        var pnlDetailContainer = splitRight.Panel2;
        pnlDetailContainer.BackColor = BgDark;
        pnlDetailContainer.Padding = new Padding(16, 12, 16, 12);

        var pnlDetailHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = 64,
            BackColor = Color.FromArgb(24, 24, 30),
            Padding = new Padding(12, 8, 12, 8)
        };

        lblDetailSubject = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            ForeColor = TextPrimary,
            Text = "Select a message to view",
            AutoEllipsis = true
        };

        lblDetailMeta = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            ForeColor = TextMuted,
            Text = "",
            AutoEllipsis = true
        };

        pnlDetailHeader.Controls.Add(lblDetailMeta);
        pnlDetailHeader.Controls.Add(lblDetailSubject);

        var pnlBodyWrapper = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(22, 22, 28),
            Padding = new Padding(12, 10, 12, 10)
        };

        txtDetailBody = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(22, 22, 28),
            ForeColor = TextPrimary,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 10F, FontStyle.Regular)
        };
        pnlBodyWrapper.Controls.Add(txtDetailBody);

        pnlDetailContainer.Controls.Add(pnlBodyWrapper);
        pnlDetailContainer.Controls.Add(pnlDetailHeader);
        pnlBodyWrapper.BringToFront();

        pnlRight.Controls.Add(splitRight);
        pnlRight.Controls.Add(pnlAccountTop);
        splitRight.BringToFront();

        // Add to main form in correct dock order
        Controls.Add(splitMain);
        Controls.Add(pnlHeader);
        splitMain.BringToFront();

        // Lifecycle Events
        Load += async (_, _) => await OnFormLoadAsync();
        Shown += (_, _) =>
        {
            try
            {
                if (splitMain.Width > 550)
                {
                    splitMain.Panel1MinSize = 200;
                    splitMain.Panel2MinSize = 250;
                    splitMain.SplitterDistance = 280;
                }

                if (splitRight.Height > 300)
                {
                    splitRight.Panel1MinSize = 120;
                    splitRight.Panel2MinSize = 120;
                    splitRight.SplitterDistance = 240;
                }

                AdjustMessageColumns();
            }
            catch { }
        };
        FormClosing += OnFormClosing;
        Resize += OnFormResize;

        // Background Monitor hooks
        BackgroundMailMonitor.NewMailReceived += (account, newMsgs) =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(() =>
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
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(() =>
            {
                ReloadAccountsList();
                if (selectedAccount != null)
                    ReloadMessagesList();
            });
        };
    }

    Button CreateStyledButton(string text, int width, Color bgColor, EventHandler onClick)
    {
        var btn = new Button
        {
            Text = text,
            Size = new Size(width, 34),
            BackColor = bgColor,
            ForeColor = TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            Cursor = Cursors.Hand,
            Margin = new Padding(4, 3, 4, 3)
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.Click += onClick;
        return btn;
    }

    void AdjustMessageColumns()
    {
        if (lstMessages.Columns.Count < 4) return;
        int totalWidth = lstMessages.ClientSize.Width;
        int fixedWidth = lstMessages.Columns[0].Width + lstMessages.Columns[1].Width + lstMessages.Columns[3].Width;
        int remaining = Math.Max(180, totalWidth - fixedWidth - 2);
        lstMessages.Columns[2].Width = remaining;
    }

    void OnDrawAccountItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= lstAccounts.Items.Count) return;
        var item = (AccountListItem)lstAccounts.Items[e.Index];
        bool isSelected = (e.State & DrawItemState.Selected) != 0;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Background
        var bg = isSelected ? BgItemSelected : ((e.Index % 2 == 0) ? BgDark : Color.FromArgb(22, 22, 27));
        using (var brush = new SolidBrush(bg))
            g.FillRectangle(brush, e.Bounds);

        // Selected left accent bar
        if (isSelected)
        {
            using var barBrush = new SolidBrush(AccentCyan);
            g.FillRectangle(barBrush, new Rectangle(e.Bounds.X, e.Bounds.Y, 3, e.Bounds.Height));
        }

        // Provider Badge
        bool isMailTm = string.Equals(item.Account.Provider, "mailtm", StringComparison.OrdinalIgnoreCase);
        string providerTag = isMailTm ? "TM" : "GM";
        Color tagColor = isMailTm ? AccentCyan : AccentGreen;

        using (var tagFont = new Font("Segoe UI", 7F, FontStyle.Bold))
        using (var tagBrush = new SolidBrush(tagColor))
        using (var tagBg = new SolidBrush(Color.FromArgb(35, tagColor.R / 4, tagColor.G / 4, tagColor.B / 4)))
        {
            var tagRect = new Rectangle(e.Bounds.X + 10, e.Bounds.Y + 13, 24, 16);
            g.FillRoundedRectangle(tagBg, tagRect, 4);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(providerTag, tagFont, tagBrush, tagRect, sf);
        }

        // Email Text
        using (var font = new Font(Font, isSelected ? FontStyle.Bold : FontStyle.Regular))
        using (var textBrush = new SolidBrush(isSelected ? Color.White : TextPrimary))
        {
            var textRect = new Rectangle(e.Bounds.X + 40, e.Bounds.Y + 11, e.Bounds.Width - 95, e.Bounds.Height - 11);
            g.DrawString(item.Account.EmailAddress, font, textBrush, textRect, new StringFormat { Trimming = StringTrimming.EllipsisCharacter });
        }

        // Unread Badge
        if (item.Account.UnreadCount > 0)
        {
            string badgeText = item.Account.UnreadCount.ToString();
            using var badgeFont = new Font("Segoe UI", 7.5F, FontStyle.Bold);
            var badgeSize = g.MeasureString(badgeText, badgeFont);
            int badgeW = Math.Max(20, (int)badgeSize.Width + 8);
            int badgeH = 18;
            int badgeX = e.Bounds.Right - badgeW - 10;
            int badgeY = e.Bounds.Y + (e.Bounds.Height - badgeH) / 2;

            var badgeRect = new Rectangle(badgeX, badgeY, badgeW, badgeH);
            using (var badgeBrush = new SolidBrush(AccentGreen))
                g.FillRoundedRectangle(badgeBrush, badgeRect, 6);

            using (var whiteBrush = new SolidBrush(Color.White))
            {
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(badgeText, badgeFont, whiteBrush, badgeRect, sf);
            }
        }
    }

    void OnDrawMessageColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var bgBrush = new SolidBrush(Color.FromArgb(30, 30, 38));
        e.Graphics.FillRectangle(bgBrush, e.Bounds);

        using var borderPen = new Pen(BorderColor);
        e.Graphics.DrawLine(borderPen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

        using var textBrush = new SolidBrush(TextMuted);
        using var font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
        var sf = new StringFormat { LineAlignment = StringAlignment.Center };
        var textRect = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 16, e.Bounds.Height);
        e.Graphics.DrawString(e.Header?.Text ?? "", font, textBrush, textRect, sf);
    }

    void OnDrawMessageSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        bool isSelected = e.Item != null && e.Item.Selected;
        var msg = e.Item?.Tag as MailMessageSummary;
        bool isUnread = msg != null && !msg.IsRead;

        var g = e.Graphics;
        var bg = isSelected ? Color.FromArgb(45, 45, 60) : ((e.ItemIndex % 2 == 0) ? BgDark : Color.FromArgb(22, 22, 27));

        using (var brush = new SolidBrush(bg))
            g.FillRectangle(brush, e.Bounds);

        // Status Column: Dot + Label
        if (e.ColumnIndex == 0)
        {
            var dotColor = isUnread ? AccentGreen : Color.FromArgb(100, 100, 115);
            using (var dotBrush = new SolidBrush(dotColor))
                g.FillEllipse(dotBrush, e.Bounds.X + 12, e.Bounds.Y + (e.Bounds.Height - 8) / 2, 8, 8);

            string statusText = isUnread ? "New" : "Read";
            using var font = new Font(Font, isUnread ? FontStyle.Bold : FontStyle.Regular);
            using var textBrush = new SolidBrush(dotColor);
            g.DrawString(statusText, font, textBrush, e.Bounds.X + 26, e.Bounds.Y + (e.Bounds.Height - font.Height) / 2);
            return;
        }

        // Other Columns: Text
        Color textColor = isUnread ? Color.White : TextMuted;
        FontStyle style = isUnread ? FontStyle.Bold : FontStyle.Regular;
        if (isSelected) textColor = Color.White;

        using (var font = new Font(Font, style))
        using (var textBrush = new SolidBrush(textColor))
        {
            var sf = new StringFormat
            {
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter
            };
            var textRect = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
            g.DrawString(e.SubItem?.Text ?? "", font, textBrush, textRect, sf);
        }
    }

    async Task OnFormLoadAsync()
    {
        ReloadAccountsList();

        _ = Task.Run(async () =>
        {
            try
            {
                var endpoint = await TorService.RequireEndpointAsync(CancellationToken.None);
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(() =>
                {
                    lblTorDot.ForeColor = AccentGreen;
                    lblTorStatus.Text = $"Tor Ready: {endpoint.Host}:{endpoint.Port}";
                    notifyIcon.Text = $"AnonymMailClient - Tor Ready ({endpoint.Port})";
                });
            }
            catch (Exception ex)
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(() =>
                {
                    lblTorDot.ForeColor = AccentRed;
                    lblTorStatus.Text = $"Tor Error: {ex.Message}";
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
            lstAccounts.Items.Add(new AccountListItem(acc, acc.EmailAddress));

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
            string providerName = string.Equals(selectedAccount.Provider, "mailtm", StringComparison.OrdinalIgnoreCase) ? "Mail.tm" : "Guerrilla Mail";
            lblCurrentAccount.Text = $"{selectedAccount.EmailAddress} ({providerName})";
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
            var lvi = new ListViewItem(msg.IsRead ? "Read" : "New")
            {
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
        lblDetailMeta.Text = $"From: {msg.From}   •   Date: {msg.DateDisplay}";

        if (!string.IsNullOrEmpty(msg.BodyText))
        {
            txtDetailBody.Text = msg.BodyText;
        }
        else
        {
            txtDetailBody.Text = "Downloading message body over Tor...";
            if (selectedAccount != null)
            {
                try
                {
                    var detailed = await MailAccountService.FetchMessageDetailAsync(selectedAccount, msg.Id);
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
            selectedAccount.UnreadCount = Math.Max(0, selectedAccount.UnreadCount - 1);
            AccountStore.SaveOrUpdateAccount(selectedAccount);
            lstAccounts.Invalidate();
            lstMessages.Invalidate();
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
            var remoteMsgs = await MailAccountService.CheckMessagesAsync(selectedAccount);
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
        AdjustMessageColumns();
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

    static void EnableImmersiveDarkMode(IntPtr handle)
    {
        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            {
                int useDarkMode = 1;
                DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
            }
        }
        catch { }
    }

    sealed record AccountListItem(MailAccount Account, string Display)
    {
        public override string ToString() => Display;
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics g, Brush brush, Rectangle bounds, int cornerRadius)
    {
        using var path = CreateRoundedRectanglePath(bounds, cornerRadius);
        g.FillPath(brush, path);
    }

    static GraphicsPath CreateRoundedRectanglePath(Rectangle bounds, int radius)
    {
        int diameter = radius * 2;
        var path = new GraphicsPath();
        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));

        // Top-left
        path.AddArc(arc, 180, 90);
        // Top-right
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        // Bottom-right
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        // Bottom-left
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);

        path.CloseFigure();
        return path;
    }
}
