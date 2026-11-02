using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using AnonymMailClient.Models;
using AnonymMailClient.Services;
using AnonymMailClient.Storage;
using MimeKit;

namespace AnonymMailClient.UI;

public sealed class ComposeDialog : Form
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    readonly ComboBox cmbSender;
    readonly TextBox txtRecipient;
    readonly TextBox txtSubject;
    readonly TextBox txtBody;
    readonly Label lblAttachment;
    readonly Button btnAttach;
    readonly Button btnRemoveAttachment;
    readonly Button btnSend;
    readonly Button btnClose;
    readonly Label lblStatus;
    readonly ProgressBar progressBar;

    string? selectedAttachmentPath;

    // Theme Colors
    static readonly Color BgDark = Color.FromArgb(20, 20, 26);
    static readonly Color BgCard = Color.FromArgb(26, 26, 32);
    static readonly Color BgInput = Color.FromArgb(32, 32, 40);
    static readonly Color BorderColor = Color.FromArgb(50, 50, 64);
    static readonly Color TextPrimary = Color.FromArgb(245, 245, 250);
    static readonly Color TextMuted = Color.FromArgb(160, 160, 175);
    static readonly Color AccentGreen = Color.FromArgb(16, 185, 129);
    static readonly Color AccentCyan = Color.FromArgb(56, 189, 248);
    static readonly Color AccentRed = Color.FromArgb(239, 68, 68);

    public ComposeDialog(MailAccount? defaultSender = null)
    {
        Text = "Compose Anonymous Email (Direct-to-MX over Tor)";
        Size = new Size(720, 600);
        MinimumSize = new Size(580, 480);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = BgDark;
        ForeColor = TextPrimary;
        Font = new Font("Segoe UI", 9.5F, FontStyle.Regular);

        HandleCreated += (_, _) => EnableImmersiveDarkMode(Handle);

        var accounts = AccountStore.LoadAccounts();

        // ── TOP FORM FIELDS ──
        var lblFrom = new Label
        {
            Text = "From:",
            Location = new Point(16, 20),
            Size = new Size(70, 24),
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        };

        cmbSender = new ComboBox
        {
            Location = new Point(94, 18),
            Size = new Size(ClientSize.Width - 114, 28),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            DropDownStyle = ComboBoxStyle.DropDown,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems,
            BackColor = BgInput,
            ForeColor = TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular)
        };

        foreach (var acc in accounts)
            cmbSender.Items.Add(acc.EmailAddress);

        if (defaultSender != null && !string.IsNullOrWhiteSpace(defaultSender.EmailAddress))
            cmbSender.Text = defaultSender.EmailAddress;
        else if (accounts.Count > 0)
            cmbSender.Text = accounts[0].EmailAddress;
        else
            cmbSender.Text = "anonymous@sharklasers.com";

        var lblTo = new Label
        {
            Text = "To:",
            Location = new Point(16, 58),
            Size = new Size(70, 24),
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        };

        txtRecipient = new TextBox
        {
            Location = new Point(94, 56),
            Size = new Size(ClientSize.Width - 114, 28),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = BgInput,
            ForeColor = TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "recipient@example.com",
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular)
        };

        var lblSubj = new Label
        {
            Text = "Subject:",
            Location = new Point(16, 96),
            Size = new Size(70, 24),
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        };

        txtSubject = new TextBox
        {
            Location = new Point(94, 94),
            Size = new Size(ClientSize.Width - 114, 28),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = BgInput,
            ForeColor = TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "Subject",
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular)
        };

        // ── BOTTOM DOCKED PANEL ──
        var pnlBottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 118,
            BackColor = BgCard,
            Padding = new Padding(20, 10, 20, 10)
        };
        pnlBottom.Paint += (_, e) =>
        {
            using var pen = new Pen(BorderColor, 1);
            e.Graphics.DrawLine(pen, 0, 0, pnlBottom.Width, 0);
        };

        // Attach Button & Label
        btnAttach = new Button
        {
            Text = "Attach File",
            Location = new Point(20, 12),
            Size = new Size(110, 30),
            BackColor = Color.FromArgb(45, 45, 58),
            ForeColor = TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        btnAttach.FlatAppearance.BorderSize = 0;
        btnAttach.Click += (_, _) => OnAttachClicked();

        lblAttachment = new Label
        {
            Text = "No attachment",
            Location = new Point(140, 17),
            Size = new Size(480, 20),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = TextMuted,
            AutoEllipsis = true,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular)
        };

        btnRemoveAttachment = new Button
        {
            Text = "✕",
            Location = new Point(626, 15),
            Size = new Size(24, 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            BackColor = Color.Transparent,
            ForeColor = AccentRed,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Visible = false
        };
        btnRemoveAttachment.FlatAppearance.BorderSize = 0;
        btnRemoveAttachment.Click += (_, _) => RemoveAttachment();

        // Status & Progress
        lblStatus = new Label
        {
            Text = "Ready to send over Tor Direct-to-MX.",
            Location = new Point(20, 48),
            Size = new Size(ClientSize.Width - 40, 18),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular)
        };

        progressBar = new ProgressBar
        {
            Location = new Point(20, 68),
            Size = new Size(ClientSize.Width - 40, 4),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Style = ProgressBarStyle.Marquee,
            Visible = false
        };

        // Action Buttons (Explicit layout to avoid WinForms anchor shift)
        btnClose = new Button
        {
            Text = "Close",
            Size = new Size(76, 34),
            BackColor = Color.FromArgb(45, 45, 56),
            ForeColor = TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            Cursor = Cursors.Hand,
            DialogResult = DialogResult.Cancel
        };
        btnClose.FlatAppearance.BorderSize = 0;

        btnSend = new Button
        {
            Text = "Send over Tor (MX:25)",
            Size = new Size(190, 34),
            BackColor = AccentGreen,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnSend.FlatAppearance.BorderSize = 0;
        btnSend.Click += async (_, _) => await OnSendClickedAsync();

        void PositionBottomButtons()
        {
            btnClose.Location = new Point(Math.Max(20, pnlBottom.ClientSize.Width - btnClose.Width - 20), 70);
            btnSend.Location = new Point(Math.Max(20, btnClose.Left - btnSend.Width - 12), 70);
        }
        pnlBottom.Resize += (_, _) => PositionBottomButtons();

        pnlBottom.Controls.Add(btnAttach);
        pnlBottom.Controls.Add(lblAttachment);
        pnlBottom.Controls.Add(btnRemoveAttachment);
        pnlBottom.Controls.Add(lblStatus);
        pnlBottom.Controls.Add(progressBar);
        pnlBottom.Controls.Add(btnClose);
        pnlBottom.Controls.Add(btnSend);

        PositionBottomButtons();

        // ── MESSAGE BODY (Centered, Auto Expanding) ──
        txtBody = new TextBox
        {
            Location = new Point(20, 134),
            Size = new Size(ClientSize.Width - 40, ClientSize.Height - 134 - pnlBottom.Height - 12),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(26, 26, 32),
            ForeColor = TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 10F, FontStyle.Regular),
            PlaceholderText = "Write your message here..."
        };

        // Add Controls
        Controls.Add(txtBody);
        Controls.Add(pnlBottom);
        Controls.Add(lblFrom);
        Controls.Add(cmbSender);
        Controls.Add(lblTo);
        Controls.Add(txtRecipient);
        Controls.Add(lblSubj);
        Controls.Add(txtSubject);
    }

    void OnAttachClicked()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Select File to Attach",
            Multiselect = false
        };
        if (ofd.ShowDialog(this) == DialogResult.OK)
        {
            selectedAttachmentPath = ofd.FileName;
            lblAttachment.Text = "Attachment: " + Path.GetFileName(selectedAttachmentPath);
            lblAttachment.ForeColor = AccentCyan;
            btnRemoveAttachment.Visible = true;
        }
    }

    void RemoveAttachment()
    {
        selectedAttachmentPath = null;
        lblAttachment.Text = "No attachment";
        lblAttachment.ForeColor = TextMuted;
        btnRemoveAttachment.Visible = false;
    }

    async Task OnSendClickedAsync()
    {
        string fromRaw = cmbSender.Text.Trim();
        if (string.IsNullOrWhiteSpace(fromRaw))
        {
            MessageBox.Show(this, "Please enter or select a sender email address (From).", "Sender Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            cmbSender.Focus();
            return;
        }

        MailboxAddress fromMailbox;
        if (MailboxAddress.TryParse(fromRaw, out var parsedMailbox))
        {
            fromMailbox = parsedMailbox;
        }
        else if (fromRaw.Contains('@'))
        {
            string user = fromRaw.Split('@')[0];
            fromMailbox = new MailboxAddress(user, fromRaw);
        }
        else
        {
            MessageBox.Show(this, "The sender address is invalid. Example: user@sharklasers.com", "Invalid Sender", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            cmbSender.Focus();
            return;
        }

        string recipient = txtRecipient.Text.Trim();
        if (string.IsNullOrWhiteSpace(recipient) || !recipient.Contains('@'))
        {
            MessageBox.Show(this, "Please enter a valid recipient email address (To).", "Invalid Recipient", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            txtRecipient.Focus();
            return;
        }

        btnSend.Enabled = false;
        btnAttach.Enabled = false;
        btnRemoveAttachment.Enabled = false;
        cmbSender.Enabled = false;
        progressBar.Visible = true;
        lblStatus.ForeColor = AccentCyan;

        void UpdateProgress(string text)
        {
            if (InvokeRequired)
            {
                Invoke(() => UpdateProgress(text));
                return;
            }
            lblStatus.Text = text;
        }

        try
        {
            // Circuit isolation: match account Id if found, else sender email
            var accounts = AccountStore.LoadAccounts();
            var matchedAccount = accounts.Find(a => string.Equals(a.EmailAddress, fromMailbox.Address, StringComparison.OrdinalIgnoreCase));
            string isolationKey = matchedAccount?.Id ?? fromMailbox.Address;

            await DirectMxSmtpService.SendCustomAsync(
                fromMailbox,
                recipient,
                txtSubject.Text.Trim(),
                txtBody.Text,
                isHtml: false,
                attachmentPath: selectedAttachmentPath,
                isolationKey: isolationKey,
                onProgress: UpdateProgress);

            progressBar.Visible = false;
            lblStatus.Text = "Mail successfully delivered to recipient MX!";
            lblStatus.ForeColor = AccentGreen;
            MessageBox.Show(this, "Email delivered directly to recipient MX server over Tor with full anonymity scrubbing!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            progressBar.Visible = false;
            lblStatus.Text = "Error: " + ex.Message;
            lblStatus.ForeColor = AccentRed;
            btnSend.Enabled = true;
            btnAttach.Enabled = true;
            btnRemoveAttachment.Enabled = true;
            cmbSender.Enabled = true;
        }
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
}
