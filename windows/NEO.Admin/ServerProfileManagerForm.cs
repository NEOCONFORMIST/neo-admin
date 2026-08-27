namespace NeoAdmin;

internal sealed class ServerProfileManagerForm : NeoForm
{
    private readonly DataGridView _grid = new();
    private readonly BindingSource _source = new();
    private readonly List<ServerProfile> _profiles;
    private string _activeId;

    public IReadOnlyList<ServerProfile> Profiles => _profiles;
    public string ActiveServerId => _activeId;

    public ServerProfileManagerForm(
        IEnumerable<ServerProfile> profiles,
        string activeServerId)
    {
        _profiles = profiles.Select(profile => profile.Clone()).ToList();
        _activeId = activeServerId;

        Text = "CS2 Servers";
        Width = 900;
        Height = 480;
        MinimumSize = new Size(720, 380);
        StartPosition = FormStartPosition.CenterParent;

        var heading = new Label
        {
            Text = "SERVER PROFILES",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(12, 12, 12, 6),
        };

        _grid.Dock = DockStyle.Fill;
        _grid.AutoGenerateColumns = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Name", DataPropertyName = nameof(ServerProfile.Name), FillWeight = 30,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Address", DataPropertyName = nameof(ServerProfile.ServerAddress), FillWeight = 30,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Port", DataPropertyName = nameof(ServerProfile.ServerPttPort), FillWeight = 15,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Account", DataPropertyName = nameof(ServerProfile.AdminId), FillWeight = 25,
        });
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.DoubleClick += (_, _) => EditSelected();

        var add = new Button { Text = "ADD", AutoSize = true };
        var edit = new Button { Text = "EDIT", AutoSize = true };
        var remove = new Button { Text = "REMOVE", AutoSize = true };
        var active = new Button { Text = "MAKE ACTIVE", AutoSize = true };
        var save = new Button { Text = "SAVE", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "CANCEL", AutoSize = true, DialogResult = DialogResult.Cancel };
        add.Click += (_, _) => AddProfile();
        edit.Click += (_, _) => EditSelected();
        remove.Click += (_, _) => RemoveSelected();
        active.Click += (_, _) => MakeSelectedActive();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(8),
        };
        buttons.Controls.AddRange(new Control[] { add, edit, remove, active, save, cancel });

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(_grid, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);

        AcceptButton = save;
        CancelButton = cancel;
        RefreshRows();
    }

    private ServerProfile? Selected =>
        _grid.CurrentRow?.DataBoundItem as ServerProfile;

    private void RefreshRows()
    {
        _source.DataSource = null;
        _source.DataSource = _profiles;
        _grid.DataSource = _source;
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.DataBoundItem is ServerProfile profile && profile.Id == _activeId)
                row.DefaultCellStyle.Font = new Font(_grid.Font, FontStyle.Bold);
        }
    }

    private void AddProfile()
    {
        using var editor = new ServerProfileEditorForm(new ServerProfile());
        if (editor.ShowDialog(this) != DialogResult.OK)
            return;
        _profiles.Add(editor.Profile);
        if (_profiles.Count == 1)
            _activeId = editor.Profile.Id;
        RefreshRows();
    }

    private void EditSelected()
    {
        if (Selected is not ServerProfile selected)
            return;
        using var editor = new ServerProfileEditorForm(selected);
        if (editor.ShowDialog(this) != DialogResult.OK)
            return;
        int index = _profiles.FindIndex(profile => profile.Id == selected.Id);
        if (index >= 0)
            _profiles[index] = editor.Profile;
        RefreshRows();
    }

    private void RemoveSelected()
    {
        if (Selected is not ServerProfile selected)
            return;
        if (_profiles.Count == 1)
        {
            MessageBox.Show(this, "At least one server profile is required.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this, $"Remove '{selected.Name}'?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _profiles.Remove(selected);
        if (_activeId == selected.Id)
            _activeId = _profiles[0].Id;
        RefreshRows();
    }

    private void MakeSelectedActive()
    {
        if (Selected is not ServerProfile selected)
            return;
        _activeId = selected.Id;
        RefreshRows();
    }
}

internal sealed class ServerProfileEditorForm : NeoForm
{
    private readonly TextBox _name = new();
    private readonly TextBox _address = new();
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 27122 };
    private readonly TextBox _adminId = new();
    private readonly TextBox _accessKey = new() { UseSystemPasswordChar = true };
    private readonly string _id;
    public ServerProfile Profile { get; private set; }

    public ServerProfileEditorForm(ServerProfile profile)
    {
        Profile = profile.Clone();
        _id = Profile.Id;
        Text = string.IsNullOrWhiteSpace(Profile.ServerAddress) ? "Add CS2 Server" : "Edit CS2 Server";
        Width = 560;
        Height = 340;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        _name.Text = Profile.Name;
        _address.Text = Profile.ServerAddress;
        _port.Value = Math.Clamp(Profile.ServerPttPort, 1, 65535);
        _adminId.Text = Profile.AdminId;
        _accessKey.Text = Profile.AccessKey;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6,
            Padding = new Padding(12), AutoSize = true,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(table, 0, "Name", _name);
        AddRow(table, 1, "Public IPv4 / DNS", _address);
        AddRow(table, 2, "PTT port", _port);
        AddRow(table, 3, "Administrator ID", _adminId);
        AddRow(table, 4, "Access key", _accessKey);

        var save = new Button { Text = "SAVE", AutoSize = true };
        var cancel = new Button { Text = "CANCEL", AutoSize = true, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => SaveProfile();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.AddRange(new Control[] { save, cancel });
        table.Controls.Add(buttons, 1, 5);
        Controls.Add(table);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private static void AddRow(TableLayoutPanel table, int row, string text, Control control)
    {
        control.Dock = DockStyle.Fill;
        table.Controls.Add(new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        table.Controls.Add(control, 1, row);
    }

    private void SaveProfile()
    {
        if (string.IsNullOrWhiteSpace(_name.Text) || string.IsNullOrWhiteSpace(_address.Text) ||
            string.IsNullOrWhiteSpace(_adminId.Text) || _accessKey.Text.Trim().Length < 16)
        {
            MessageBox.Show(this,
                "Enter a name, address, administrator ID, and a valid access key.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Profile = new ServerProfile
        {
            Id = _id,
            Name = _name.Text,
            ServerAddress = _address.Text,
            ServerPttPort = (int)_port.Value,
            AdminId = _adminId.Text,
            AccessKey = _accessKey.Text,
        };
        Profile.Normalize();
        DialogResult = DialogResult.OK;
    }
}
