namespace NeoAdmin;

internal sealed class SteamApiSettingsForm : NeoForm
{
    private readonly TextBox _apiKey = new();

    public string ApiKey { get; private set; } = string.Empty;

    public SteamApiSettingsForm(string currentApiKey)
    {
        Text = "Steam Profile Integration";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(620, 240);
        MinimumSize = Size;
        MaximumSize = Size;
        ShowIcon = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(16),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        root.Controls.Add(new Label
        {
            Text = "STEAM WEB API KEY",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font.FontFamily, 10F, FontStyle.Bold),
        }, 0, 0);

        _apiKey.Text = currentApiKey;
        _apiKey.Dock = DockStyle.Fill;
        _apiKey.UseSystemPasswordChar = true;
        _apiKey.PlaceholderText = "Paste your 32-character Steam Web API key here";
        root.Controls.Add(_apiKey, 0, 1);

        var showKey = new CheckBox
        {
            Text = "Show API key",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
        };
        showKey.CheckedChanged += (_, _) =>
        {
            _apiKey.UseSystemPasswordChar = !showKey.Checked;
            BeginInvoke((Action)StyleApiKeyInput);
        };
        root.Controls.Add(showKey, 0, 2);

        root.Controls.Add(new Label
        {
            Text = "Optional. Leave blank to use SteamGPT's keyless public API. Enter a Valve key to query Steam directly.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            ForeColor = NeoTheme.MutedText,
        }, 0, 3);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        var save = new Button { Text = "SAVE", Width = 100, Height = 32 };
        var cancel = new Button
        {
            Text = "CANCEL",
            Width = 100,
            Height = 32,
            DialogResult = DialogResult.Cancel,
        };
        save.Click += (_, _) => SaveAndClose();
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons, 0, 4);

        Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;

        Shown += (_, _) => BeginInvoke((Action)StyleApiKeyInput);
    }

    private void StyleApiKeyInput()
    {
        _apiKey.BackColor = NeoTheme.ToolbarInput;
        _apiKey.ForeColor = NeoTheme.Text;
        _apiKey.Invalidate(true);
        _apiKey.Update();
    }

    private void SaveAndClose()
    {
        string value = _apiKey.Text.Trim();
        if (value.Length != 0 &&
            (value.Length != 32 || value.Any(character => !Uri.IsHexDigit(character))))
        {
            MessageBox.Show(
                this,
                "Enter a 32-character hexadecimal Steam Web API key, or leave it blank.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        ApiKey = value;
        DialogResult = DialogResult.OK;
        Close();
    }
}
