using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NeoAdmin;

internal sealed class PlayerIdentityForm : NeoForm
{
    private const ulong SteamId64Base = 76561197960265728UL;

    public PlayerIdentityForm(
        string playerName,
        int playerSlot,
        ulong steamId64,
        string steamWebApiKey)
    {
        Text = "Player Profile";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 500);
        MinimumSize = new Size(620, 500);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        bool hasSteamIdentity =
            steamId64 >= SteamId64Base &&
            steamId64 - SteamId64Base <= uint.MaxValue;

        string steamId64Text = hasSteamIdentity
            ? steamId64.ToString()
            : "Not available";
        string steam2Text = hasSteamIdentity
            ? ToSteam2Id(steamId64)
            : "Not available";
        string steam3Text = hasSteamIdentity
            ? ToSteam3Id(steamId64)
            : "Not available";
        string accountIdText = hasSteamIdentity
            ? (steamId64 - SteamId64Base).ToString()
            : "Not available";

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = new Padding(16, 6, 16, 6),
            BackColor = Color.FromArgb(31, 36, 41),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 52));

        var title = new Label
        {
            Text = "PLAYER IDENTITY",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            ForeColor = Color.WhiteSmoke,
            Font = new Font(Font.FontFamily, 11F, FontStyle.Bold),
        };

        var player = new Label
        {
            Text = $"{playerName}  |  Slot {playerSlot}",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            ForeColor = Color.FromArgb(181, 190, 199),
            AutoEllipsis = true,
        };

        header.Controls.Add(title, 0, 0);
        header.Controls.Add(player, 0, 1);

        var identityList = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = new Padding(16, 12, 16, 8),
            BackColor = NeoTheme.Surface,
        };
        identityList.ColumnStyles.Add(
            new ColumnStyle(SizeType.Absolute, 110));
        identityList.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100));

        for (int row = 0; row < identityList.RowCount; ++row)
        {
            identityList.RowStyles.Add(
                new RowStyle(SizeType.Percent, 25));
        }

        AddIdentityRow(identityList, 0, "SteamID64", steamId64Text);
        AddIdentityRow(identityList, 1, "Steam2", steam2Text);
        AddIdentityRow(identityList, 2, "Steam3", steam3Text);
        AddIdentityRow(identityList, 3, "Account ID", accountIdText);

        var avatar = new PictureBox
        {
            Width = 96,
            Height = 96,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(31, 36, 41),
            Margin = new Padding(0, 4, 14, 4),
        };
        var profileName = new Label
        {
            Text = "STEAM PROFILE",
            Dock = DockStyle.Top,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font.FontFamily, 10F, FontStyle.Bold),
            ForeColor = NeoTheme.Text,
        };
        var profileDetails = new Label
        {
            Text = hasSteamIdentity
                ? "Loading Steam profile..."
                : "No Steam profile is available for this client.",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = NeoTheme.MutedText,
        };
        var profileLink = new LinkLabel
        {
            Text = "OPEN STEAM PROFILE",
            Dock = DockStyle.Bottom,
            Height = 28,
            Visible = false,
            LinkColor = NeoTheme.Accent,
        };
        string profileUrl = hasSteamIdentity
            ? $"https://steamcommunity.com/profiles/{steamId64}"
            : string.Empty;
        profileLink.Visible = hasSteamIdentity;
        profileLink.LinkClicked += (_, _) =>
        {
            if (Uri.TryCreate(profileUrl, UriKind.Absolute, out Uri? uri))
            {
                Process.Start(new ProcessStartInfo(uri.ToString())
                {
                    UseShellExecute = true,
                });
            }
        };

        var profileText = new Panel { Dock = DockStyle.Fill };
        profileText.Controls.Add(profileDetails);
        profileText.Controls.Add(profileLink);
        profileText.Controls.Add(profileName);

        var profilePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(16, 8, 16, 8),
            BackColor = NeoTheme.SurfaceRaised,
        };
        profilePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        profilePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        profilePanel.Controls.Add(avatar, 0, 0);
        profilePanel.Controls.Add(profileText, 1, 0);

        var loadCancellation = new CancellationTokenSource();
        Shown += async (_, _) =>
        {
            if (!hasSteamIdentity)
                return;

            try
            {
                SteamProfileInfo profile = await SteamProfileClient.GetAsync(
                    steamWebApiKey,
                    steamId64,
                    loadCancellation.Token);
                if (IsDisposed || Disposing)
                    return;

                profileName.Text = string.IsNullOrWhiteSpace(profile.PersonaName)
                    ? "STEAM PROFILE"
                    : profile.PersonaName;
                string banStatus = profile.CommunityBanned ||
                    profile.VacBanned || profile.GameBanCount > 0
                        ? $"Community ban: {(profile.CommunityBanned ? "Yes" : "No")}  |  " +
                          $"VAC bans: {profile.VacBanCount}  |  Game bans: {profile.GameBanCount}"
                        : "No community, VAC, or game bans reported.";
                string economyStatus = profile.EconomyBan.Equals(
                    "none",
                    StringComparison.OrdinalIgnoreCase)
                        ? "No economy restriction reported."
                        : $"Economy restriction: {profile.EconomyBan}";
                profileDetails.Text =
                    $"{banStatus}{Environment.NewLine}{economyStatus}" +
                    $"{Environment.NewLine}Source: {profile.DataSource}";
                profileDetails.ForeColor = profile.CommunityBanned ||
                    profile.VacBanned || profile.GameBanCount > 0
                        ? NeoTheme.Danger
                        : NeoTheme.Success;
                profileUrl = profile.ProfileUrl;
                profileLink.Visible = Uri.TryCreate(
                    profileUrl,
                    UriKind.Absolute,
                    out _);

                if (profile.AvatarBytes.Length > 0)
                {
                    using var stream = new MemoryStream(profile.AvatarBytes);
                    using Image loaded = Image.FromStream(stream);
                    avatar.Image = new Bitmap(loaded);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                profileDetails.Text = $"Steam lookup failed: {exception.Message}";
                profileDetails.ForeColor = NeoTheme.Danger;
            }
        };
        FormClosed += (_, _) =>
        {
            loadCancellation.Cancel();
            loadCancellation.Dispose();
            avatar.Image?.Dispose();
        };

        var statusLabel = new Label
        {
            Text = hasSteamIdentity
                ? string.Empty
                : "No Steam identity is reported for this client.",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = hasSteamIdentity
                ? NeoTheme.Text
                : NeoTheme.Danger,
            Margin = new Padding(16, 0, 0, 0),
        };

        var copyAllButton = new Button
        {
            Text = "COPY ALL",
            AutoSize = true,
            Enabled = hasSteamIdentity,
            Margin = new Padding(6, 6, 0, 6),
        };

        var closeButton = new Button
        {
            Text = "CLOSE",
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
            Margin = new Padding(6, 6, 8, 6),
        };

        copyAllButton.Click += (_, _) =>
        {
            string details =
                $"Player: {playerName}{Environment.NewLine}" +
                $"Slot: {playerSlot}{Environment.NewLine}" +
                $"SteamID64: {steamId64Text}{Environment.NewLine}" +
                $"Steam2: {steam2Text}{Environment.NewLine}" +
                $"Steam3: {steam3Text}{Environment.NewLine}" +
                $"Account ID: {accountIdText}";

            try
            {
                Clipboard.SetText(details);
                statusLabel.Text = "Copied to clipboard.";
                statusLabel.ForeColor = NeoTheme.Success;
            }
            catch (ExternalException exception)
            {
                statusLabel.Text =
                    $"Clipboard unavailable: {exception.Message}";
                statusLabel.ForeColor = NeoTheme.Danger;
            }
        };

        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = NeoTheme.Surface,
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        buttons.Controls.Add(copyAllButton);
        buttons.Controls.Add(closeButton);

        actions.Controls.Add(statusLabel, 0, 0);
        actions.Controls.Add(buttons, 1, 0);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(identityList, 0, 1);
        root.Controls.Add(profilePanel, 0, 2);
        root.Controls.Add(actions, 0, 3);

        CancelButton = closeButton;
        Controls.Add(root);
    }

    private static void AddIdentityRow(
        TableLayoutPanel list,
        int row,
        string label,
        string value)
    {
        var fieldLabel = new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(
                SystemFonts.MessageBoxFont?.FontFamily
                    ?? FontFamily.GenericSansSerif,
                9F,
                FontStyle.Bold),
            ForeColor = NeoTheme.Text,
        };

        var valueBox = new TextBox
        {
            Text = value,
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = NeoTheme.Input,
            ForeColor = NeoTheme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font(FontFamily.GenericMonospace, 10F),
            Margin = new Padding(0, 4, 0, 4),
        };

        valueBox.Enter += (_, _) => valueBox.SelectAll();

        list.Controls.Add(fieldLabel, 0, row);
        list.Controls.Add(valueBox, 1, row);
    }

    private static string ToSteam2Id(ulong steamId64)
    {
        ulong accountId = steamId64 - SteamId64Base;
        ulong authenticationServer = accountId & 1UL;
        ulong accountNumber = accountId >> 1;
        return $"STEAM_0:{authenticationServer}:{accountNumber}";
    }

    private static string ToSteam3Id(ulong steamId64)
    {
        ulong accountId = steamId64 - SteamId64Base;
        return $"[U:1:{accountId}]";
    }
}
