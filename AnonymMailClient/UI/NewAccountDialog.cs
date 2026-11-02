using AnonymMailClient.Models;
using AnonymMailClient.Services;
using AnonymMailClient.Tor;

namespace AnonymMailClient.UI;

public sealed class NewAccountDialog : Form
{
    readonly RadioButton rbRandom;
    readonly RadioButton rbCustom;
    readonly TextBox txtUsername;
    readonly ComboBox cmbDomain;
    readonly Button btnCreate;
    readonly Button btnCancel;
    readonly Label lblStatus;
    readonly ProgressBar progressBar;

    public MailAccount? CreatedAccount { get; private set; }

    public NewAccountDialog()
    {
        Text = "Add Guerrilla Mail Account";
        Size = new Size(460, 320);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(24, 24, 27);
        ForeColor = Color.FromArgb(240, 240, 245);
        Font = new Font("Segoe UI", 10F, FontStyle.Regular);

        var lblTitle = new Label
        {
            Text = "Create / Claim Guerrilla Mail Account",
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(20, 16),
            AutoSize = true
        };

        rbRandom = new RadioButton
        {
            Text = "Generate Random Email Address",
            Location = new Point(24, 52),
            AutoSize = true,
            Checked = true
        };

        rbCustom = new RadioButton
        {
            Text = "Custom Username (Permanent Claimable Inbox)",
            Location = new Point(24, 82),
            AutoSize = true
        };

        txtUsername = new TextBox
        {
            Location = new Point(48, 114),
            Size = new Size(200, 26),
            BackColor = Color.FromArgb(39, 39, 42),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "username",
            Enabled = false
        };

        var lblAt = new Label
        {
            Text = "@",
            Location = new Point(252, 117),
            AutoSize = true,
            ForeColor = Color.FromArgb(161, 161, 170)
        };

        cmbDomain = new ComboBox
        {
            Location = new Point(272, 114),
            Size = new Size(150, 26),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(39, 39, 42),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        cmbDomain.Items.AddRange(GuerrillaMailService.AvailableDomains);
        cmbDomain.SelectedIndex = 0;

        rbRandom.CheckedChanged += (_, _) =>
        {
            txtUsername.Enabled = rbCustom.Checked;
            if (rbCustom.Checked) txtUsername.Focus();
        };

        lblStatus = new Label
        {
            Location = new Point(24, 160),
            Size = new Size(400, 40),
            ForeColor = Color.FromArgb(161, 161, 170),
            Text = "The account will be provisioned strictly over Tor."
        };

        progressBar = new ProgressBar
        {
            Location = new Point(24, 206),
            Size = new Size(398, 10),
            Style = ProgressBarStyle.Marquee,
            Visible = false
        };

        btnCreate = new Button
        {
            Text = "Create Account",
            Location = new Point(200, 230),
            Size = new Size(130, 36),
            BackColor = Color.FromArgb(16, 185, 129),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        btnCreate.FlatAppearance.BorderSize = 0;
        btnCreate.Click += async (_, _) => await OnCreateClickedAsync();

        btnCancel = new Button
        {
            Text = "Cancel",
            Location = new Point(340, 230),
            Size = new Size(82, 36),
            BackColor = Color.FromArgb(63, 63, 70),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            DialogResult = DialogResult.Cancel
        };
        btnCancel.FlatAppearance.BorderSize = 0;

        Controls.Add(lblTitle);
        Controls.Add(rbRandom);
        Controls.Add(rbCustom);
        Controls.Add(txtUsername);
        Controls.Add(lblAt);
        Controls.Add(cmbDomain);
        Controls.Add(lblStatus);
        Controls.Add(progressBar);
        Controls.Add(btnCreate);
        Controls.Add(btnCancel);

        AcceptButton = btnCreate;
        CancelButton = btnCancel;
    }

    async Task OnCreateClickedAsync()
    {
        string domain = (string)cmbDomain.SelectedItem!;
        string? username = rbCustom.Checked ? txtUsername.Text.Trim() : null;

        if (rbCustom.Checked && string.IsNullOrWhiteSpace(username))
        {
            MessageBox.Show(this, "Please enter a username or select Random address.", "Input Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        btnCreate.Enabled = false;
        btnCancel.Enabled = false;
        progressBar.Visible = true;
        lblStatus.Text = "Connecting over Tor and claiming inbox...";
        lblStatus.ForeColor = Color.FromArgb(56, 189, 248);

        try
        {
            MailAccount account;
            if (rbCustom.Checked)
            {
                account = await GuerrillaMailService.CreateCustomAccountAsync(username!, domain);
            }
            else
            {
                account = await GuerrillaMailService.CreateRandomAccountAsync(domain);
            }

            CreatedAccount = account;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            lblStatus.Text = "Error: " + ex.Message;
            lblStatus.ForeColor = Color.FromArgb(248, 113, 113);
            btnCreate.Enabled = true;
            btnCancel.Enabled = true;
            progressBar.Visible = false;
        }
    }
}
