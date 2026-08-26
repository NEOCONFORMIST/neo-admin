namespace NeoAdmin;

internal sealed class FirstOwnerSetupForm : NeoForm
{
    private readonly TextBox _serverAddress = new();
    private readonly NumericUpDown _serverPort = new();
    private readonly TextBox _setupCode = new();
    private readonly TextBox _displayName = new();
    private readonly TextBox _accountId = new();
    private readonly Label _status = new();
    private readonly Button _create = new();
    private readonly Button _cancel = new();

    public FirstOwnerSetupResult? Result { get; private set; }

    public FirstOwnerSetupForm(string serverAddress, int serverPort)
    {
        Text = "Initial Server Setup";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(680, 520);
        MinimumSize = Size;
        MaximumSize = Size;
        ShowIcon = false;

        _serverAddress.Text = serverAddress;
        _serverPort.Minimum = 1;
        _serverPort.Maximum = 65535;
        _serverPort.Value = serverPort is >= 1 and <= 65535
            ? serverPort
            : 27122;
        _setupCode.CharacterCasing = CharacterCasing.Upper;
        _setupCode.Font = new Font("Consolas", 10F);
        _displayName.Text = Environment.UserName;
        _accountId.Text = MakeDefaultAccountId(Environment.UserName);

        BuildUi();
    }

    private void BuildUi()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 9,
            Padding = new Padding(18),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        for (int index = 2; index <= 6; ++index)
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        var heading = new Label
        {
            Text = "Create the first Owner account",
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, 13F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        layout.Controls.Add(heading, 0, 0);
        layout.SetColumnSpan(heading, 2);

        var instructions = new Label
        {
            Text =
                "Use this only on a fresh server with no administrator accounts. " +
                "The one-time code appears in the CS2 server console when the plugin starts.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
        };
        layout.Controls.Add(instructions, 0, 1);
        layout.SetColumnSpan(instructions, 2);

        AddRow(layout, 2, "Server address", _serverAddress);
        AddRow(layout, 3, "UDP port", _serverPort);
        AddRow(layout, 4, "Setup code", _setupCode);
        AddRow(layout, 5, "Your name", _displayName);
        AddRow(layout, 6, "Account ID", _accountId);

        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.TopLeft;
        _status.ForeColor = NeoTheme.MutedText;
        layout.Controls.Add(_status, 0, 7);
        layout.SetColumnSpan(_status, 2);

        _create.Text = "CREATE OWNER";
        _create.Width = 145;
        _create.Height = 32;
        _create.Click += async (_, _) => await CreateOwnerAsync();
        _cancel.Text = "CANCEL";
        _cancel.Width = 110;
        _cancel.Height = 32;
        _cancel.DialogResult = DialogResult.Cancel;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_create);
        layout.Controls.Add(buttons, 0, 8);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);
        AcceptButton = _create;
        CancelButton = _cancel;
    }

    private async Task CreateOwnerAsync()
    {
        SetBusy(true);
        _status.ForeColor = NeoTheme.MutedText;
        _status.Text = "Contacting the CS2 server and creating the Owner account...";
        try
        {
            Result = await FirstOwnerSetupClient.ClaimAsync(
                _serverAddress.Text,
                decimal.ToInt32(_serverPort.Value),
                _displayName.Text,
                _accountId.Text,
                _setupCode.Text);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception exception)
        {
            _status.ForeColor = NeoTheme.Danger;
            _status.Text = exception.Message;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _create.Enabled = !busy;
        _cancel.Enabled = !busy;
        _serverAddress.Enabled = !busy;
        _serverPort.Enabled = !busy;
        _setupCode.Enabled = !busy;
        _displayName.Enabled = !busy;
        _accountId.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private static void AddRow(
        TableLayoutPanel layout,
        int row,
        string label,
        Control input)
    {
        input.Dock = DockStyle.Fill;
        layout.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, row);
        layout.Controls.Add(input, 1, row);
    }

    private static string MakeDefaultAccountId(string value)
    {
        string id = new(value
            .Where(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-')
            .Take(32)
            .ToArray());
        return id.Length >= 3 ? id : "owner";
    }
}
