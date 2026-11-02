using System.Runtime.InteropServices;
using AnonymMailClient.Models;
using AnonymMailClient.Services;
using AnonymMailClient.Tor;

namespace AnonymMailClient.UI;

public sealed class NewAccountDialog : Form
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    readonly ComboBox cmbProvider;
    readonly RadioButton rbRandom;
    readonly RadioButton rbCustom;
    readonly TextBox txtUsername;
    readonly ComboBox cmbDomain;
    readonly Label lblPassword;
    readonly TextBox txtPassword;
    readonly Button btnCreate;
    readonly Button btnCancel;
    readonly Label lblStatus;
    readonly ProgressBar progressBar;

    public MailAccount? CreatedAccount { get; private set; }

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

    public NewAccountDialog()
    {
        Text = "Add Anonymous Mail Account";
        Size = new Size(500, 420);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = BgDark;
        ForeColor = TextPrimary;
        Font = new Font("Segoe UI", 9.5F, FontStyle.Regular);

        HandleCreated += (_, _) => EnableImmersiveDarkMode(Handle);

        var lblProvider = new Label
        {
            Text = "Service Provider:",
            Location = new Point(24, 18),
            AutoSize = true,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = TextMuted
        };

        cmbProvider = new ComboBox
        {
            Location = new Point(24, 40),
            Size = new Size(436, 28),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = BgInput,
            ForeColor = TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
        };
        cmbProvider.Items.Add("Guerrilla Mail (sharklasers.com, guerrillamail.com)");
        cmbProvider.Items.Add("Mail.tm (Password-protected permanent inboxes)");
        cmbProvider.SelectedIndex = 0;

        rbRandom = new RadioButton
        {
            Text = "Generate Random Email Address",
            Location = new Point(24, 82),
            AutoSize = true,
            Checked = true,
            ForeColor = TextPrimary
        };

        rbCustom = new RadioButton
        {
            Text = "Custom Username (Permanent claimable address)",
            Location = new Point(24, 110),
            AutoSize = true,
            ForeColor = TextPrimary
        };

        txtUsername = new TextBox
        {
            Location = new Point(24, 140),
            Size = new Size(210, 26),
            BackColor = BgInput,
            ForeColor = TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "username",
            Enabled = false,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular)
        };

        var lblAt = new Label
        {
            Text = "@",
            Location = new Point(240, 143),
            AutoSize = true,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold)
        };

        cmbDomain = new ComboBox
        {
            Location = new Point(262, 140),
            Size = new Size(198, 26),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = BgInput,
            ForeColor = TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular)
        };
        cmbDomain.Items.AddRange(GuerrillaMailService.AvailableDomains);
        cmbDomain.SelectedIndex = 0;

        // Password Row (Mail.tm only)
        lblPassword = new Label
        {
            Text = "Password (Optional - leave empty for auto-generated):",
            Location = new Point(24, 178),
            AutoSize = true,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
            Visible = false
        };

        txtPassword = new TextBox
        {
            Location = new Point(24, 200),
            Size = new Size(436, 26),
            BackColor = BgInput,
            ForeColor = TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "Auto-generated secure password",
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular),
            Visible = false
        };

        lblStatus = new Label
        {
            Location = new Point(24, 240),
            Size = new Size(436, 36),
            ForeColor = TextMuted,
            Text = "The account will be provisioned strictly over Tor SOCKS5.",
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular)
        };

        progressBar = new ProgressBar
        {
            Location = new Point(24, 282),
            Size = new Size(436, 6),
            Style = ProgressBarStyle.Marquee,
            Visible = false
        };

        btnCreate = new Button
        {
            Text = "Create Account",
            Location = new Point(220, 310),
            Size = new Size(140, 34),
            BackColor = AccentGreen,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnCreate.FlatAppearance.BorderSize = 0;
        btnCreate.Click += async (_, _) => await OnCreateClickedAsync();

        btnCancel = new Button
        {
            Text = "Cancel",
            Location = new Point(370, 310),
            Size = new Size(90, 34),
            BackColor = Color.FromArgb(45, 45, 56),
            ForeColor = TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            Cursor = Cursors.Hand,
            DialogResult = DialogResult.Cancel
        };
        btnCancel.FlatAppearance.BorderSize = 0;

        // Event hooks
        rbRandom.CheckedChanged += (_, _) =>
        {
            txtUsername.Enabled = rbCustom.Checked;
            if (rbCustom.Checked) txtUsername.Focus();
        };

        cmbProvider.SelectedIndexChanged += async (_, _) => await OnProviderChangedAsync();

        Controls.Add(lblProvider);
        Controls.Add(cmbProvider);
        Controls.Add(rbRandom);
        Controls.Add(rbCustom);
        Controls.Add(txtUsername);
        Controls.Add(lblAt);
        Controls.Add(cmbDomain);
        Controls.Add(lblPassword);
        Controls.Add(txtPassword);
        Controls.Add(lblStatus);
        Controls.Add(progressBar);
        Controls.Add(btnCreate);
        Controls.Add(btnCancel);

        AcceptButton = btnCreate;
        CancelButton = btnCancel;
    }

    async Task OnProviderChangedAsync()
    {
        bool isMailTm = cmbProvider.SelectedIndex == 1;
        lblPassword.Visible = isMailTm;
        txtPassword.Visible = isMailTm;

        if (isMailTm)
        {
            lblStatus.Text = "Fetching active Mail.tm domains over Tor...";
            lblStatus.ForeColor = AccentCyan;
            progressBar.Visible = true;
            btnCreate.Enabled = false;

            try
            {
                var domains = await MailTmService.GetDomainsAsync();
                cmbDomain.Items.Clear();
                foreach (var d in domains)
                    cmbDomain.Items.Add(d);

                if (cmbDomain.Items.Count > 0)
                    cmbDomain.SelectedIndex = 0;

                lblStatus.Text = "Mail.tm ready. Enter custom username/password or use auto-generated.";
                lblStatus.ForeColor = TextMuted;
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Could not fetch Mail.tm domains: " + ex.Message;
                lblStatus.ForeColor = AccentRed;
            }
            finally
            {
                progressBar.Visible = false;
                btnCreate.Enabled = true;
            }
        }
        else
        {
            cmbDomain.Items.Clear();
            cmbDomain.Items.AddRange(GuerrillaMailService.AvailableDomains);
            if (cmbDomain.Items.Count > 0)
                cmbDomain.SelectedIndex = 0;

            lblStatus.Text = "Guerrilla Mail inboxes are claimable instantly without a password.";
            lblStatus.ForeColor = TextMuted;
        }
    }

    async Task OnCreateClickedAsync()
    {
        bool isMailTm = cmbProvider.SelectedIndex == 1;
        string? domain = cmbDomain.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(domain))
        {
            MessageBox.Show(this, "Please select an active domain.", "Domain Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string? username = rbCustom.Checked ? txtUsername.Text.Trim() : null;
        if (rbCustom.Checked && string.IsNullOrWhiteSpace(username))
        {
            MessageBox.Show(this, "Please enter a username or select Random address.", "Input Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            txtUsername.Focus();
            return;
        }

        btnCreate.Enabled = false;
        btnCancel.Enabled = false;
        cmbProvider.Enabled = false;
        progressBar.Visible = true;
        lblStatus.Text = isMailTm
            ? "Creating Mail.tm account over Tor..."
            : "Connecting over Tor and claiming Guerrilla Mail inbox...";
        lblStatus.ForeColor = AccentCyan;

        try
        {
            MailAccount account;
            if (isMailTm)
            {
                string? pwd = string.IsNullOrWhiteSpace(txtPassword.Text) ? null : txtPassword.Text.Trim();
                account = await MailTmService.CreateAccountAsync(username, domain, pwd);
            }
            else
            {
                if (rbCustom.Checked)
                    account = await GuerrillaMailService.CreateCustomAccountAsync(username!, domain);
                else
                    account = await GuerrillaMailService.CreateRandomAccountAsync(domain);
            }

            CreatedAccount = account;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            lblStatus.Text = "Error: " + ex.Message;
            lblStatus.ForeColor = AccentRed;
            btnCreate.Enabled = true;
            btnCancel.Enabled = true;
            cmbProvider.Enabled = true;
            progressBar.Visible = false;
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
