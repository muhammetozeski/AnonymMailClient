using System.Runtime.InteropServices;
using AnonymMailClient.Models;
using AnonymMailClient.Services;
using AnonymMailClient.Storage;

namespace AnonymMailClient.UI;

public sealed class ComposeDialog : Form
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    readonly ComboBox cmbSender;
    readonly TextBox txtRecipient;
    readonly TextBox txtSubject;
    readonly TextBox txtBody;
    readonly Label lblAttachment;
    readonly Button btnAttach;
    readonly Button btnSend;
    readonly Button btnClose;
    readonly Label lblStatus;
    readonly ProgressBar progressBar;

    string? selectedAttachmentPath;

    public ComposeDialog(MailAccount? defaultSender = null)
    {
        Text = "Compose Anonymous Email (Direct-to-MX over Tor)";
        Size = new Size(680, 560);
        MinimumSize = new Size(540, 440);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(24, 24, 30);
        ForeColor = Color.FromArgb(240, 240, 245);
        Font = new Font("Segoe UI", 9.5F, FontStyle.Regular);

        HandleCreated += (_, _) =>
        {
            try
            {
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
                {
                    int dm = 1;
                    DwmSetWindowAttribute(Handle, 20, ref dm, sizeof(int));
                }
            }
            catch { }
        };

        var accounts = AccountStore.LoadAccounts();

        var lblFrom = new Label { Text = "From:", Location = new Point(24, 20), AutoSize = true, ForeColor = Color.FromArgb(161, 161, 170) };
        cmbSender = new ComboBox
        {
            Location = new Point(80, 16),
            Size = new Size(560, 26),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(39, 39, 42),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        foreach (var acc in accounts)
            cmbSender.Items.Add(acc);

        if (defaultSender != null)
        {
            int idx = accounts.FindIndex(a => a.Id == defaultSender.Id);
            if (idx >= 0) cmbSender.SelectedIndex = idx;
        }
        if (cmbSender.SelectedIndex < 0 && cmbSender.Items.Count > 0)
            cmbSender.SelectedIndex = 0;

        var lblTo = new Label { Text = "To:", Location = new Point(24, 56), AutoSize = true, ForeColor = Color.FromArgb(161, 161, 170) };
        txtRecipient = new TextBox
        {
            Location = new Point(80, 52),
            Size = new Size(560, 26),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.FromArgb(39, 39, 42),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "recipient@example.com"
        };

        var lblSubj = new Label { Text = "Subject:", Location = new Point(24, 92), AutoSize = true, ForeColor = Color.FromArgb(161, 161, 170) };
        txtSubject = new TextBox
        {
            Location = new Point(80, 88),
            Size = new Size(560, 26),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.FromArgb(39, 39, 42),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "Subject"
        };

        txtBody = new TextBox
        {
            Location = new Point(24, 126),
            Size = new Size(616, 280),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(39, 39, 42),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 10F, FontStyle.Regular),
            PlaceholderText = "Write your message here..."
        };

        btnAttach = new Button
        {
            Text = "📎 Attach File",
            Location = new Point(24, 416),
            Size = new Size(110, 30),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            BackColor = Color.FromArgb(63, 63, 70),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        btnAttach.FlatAppearance.BorderSize = 0;
        btnAttach.Click += (_, _) => OnAttachClicked();

        lblAttachment = new Label
        {
            Text = "No attachment",
            Location = new Point(142, 422),
            Size = new Size(490, 20),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = Color.FromArgb(161, 161, 170),
            AutoEllipsis = true
        };

        lblStatus = new Label
        {
            Text = "Ready to send over Tor Direct-to-MX.",
            Location = new Point(24, 452),
            Size = new Size(616, 20),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = Color.FromArgb(161, 161, 170)
        };

        progressBar = new ProgressBar
        {
            Location = new Point(24, 474),
            Size = new Size(616, 6),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Style = ProgressBarStyle.Marquee,
            Visible = false
        };

        btnSend = new Button
        {
            Text = "🚀 Send over Tor (MX:25)",
            Location = new Point(410, 486),
            Size = new Size(160, 34),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            BackColor = Color.FromArgb(16, 185, 129),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnSend.FlatAppearance.BorderSize = 0;
        btnSend.Click += async (_, _) => await OnSendClickedAsync();

        btnClose = new Button
        {
            Text = "Close",
            Location = new Point(576, 486),
            Size = new Size(64, 34),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            BackColor = Color.FromArgb(63, 63, 70),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            DialogResult = DialogResult.Cancel
        };
        btnClose.FlatAppearance.BorderSize = 0;

        Controls.Add(lblFrom);
        Controls.Add(cmbSender);
        Controls.Add(lblTo);
        Controls.Add(txtRecipient);
        Controls.Add(lblSubj);
        Controls.Add(txtSubject);
        Controls.Add(txtBody);
        Controls.Add(btnAttach);
        Controls.Add(lblAttachment);
        Controls.Add(lblStatus);
        Controls.Add(progressBar);
        Controls.Add(btnSend);
        Controls.Add(btnClose);
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
            lblAttachment.ForeColor = Color.FromArgb(56, 189, 248);
        }
    }

    async Task OnSendClickedAsync()
    {
        if (cmbSender.SelectedItem is not MailAccount senderAccount)
        {
            MessageBox.Show(this, "Please select or create an account first.", "No Sender", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string recipient = txtRecipient.Text.Trim();
        if (string.IsNullOrWhiteSpace(recipient) || !recipient.Contains('@'))
        {
            MessageBox.Show(this, "Please enter a valid recipient email address.", "Invalid Recipient", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        btnSend.Enabled = false;
        btnAttach.Enabled = false;
        progressBar.Visible = true;
        lblStatus.ForeColor = Color.FromArgb(56, 189, 248);

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
            await DirectMxSmtpService.SendAsync(
                senderAccount,
                recipient,
                txtSubject.Text.Trim(),
                txtBody.Text,
                isHtml: false,
                attachmentPath: selectedAttachmentPath,
                onProgress: UpdateProgress);

            progressBar.Visible = false;
            lblStatus.Text = "Mail successfully sent!";
            lblStatus.ForeColor = Color.FromArgb(52, 211, 153);
            MessageBox.Show(this, "Email delivered directly to recipient MX server over Tor!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            progressBar.Visible = false;
            lblStatus.Text = "Error: " + ex.Message;
            lblStatus.ForeColor = Color.FromArgb(248, 113, 113);
            btnSend.Enabled = true;
            btnAttach.Enabled = true;
        }
    }
}
