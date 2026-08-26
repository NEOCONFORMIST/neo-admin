using System.Runtime.InteropServices;

namespace NeoAdmin;

internal class NeoForm : Form
{
    public NeoForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5F, FontStyle.Regular);
        BackColor = NeoTheme.Canvas;
        ForeColor = NeoTheme.Text;
        SetStyle(
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.AllPaintingInWmPaint,
            true);
    }

    protected override void OnLoad(EventArgs e)
    {
        NeoTheme.Apply(this);
        base.OnLoad(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NeoTheme.UseDarkTitleBar(Handle);
    }
}

internal sealed class NeoTabControl : TabControl
{
    private int _hoverIndex = -1;
    public int TabWidth { get; set; } = 112;

    public NeoTabControl()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);
        SizeMode = TabSizeMode.Fixed;
        ItemSize = new Size(112, 40);
        Padding = new Point(14, 5);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ItemSize = new Size(TabWidth, 40);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(NeoTheme.Canvas);

        Rectangle pageBounds = DisplayRectangle;
        using (var borderPen = new Pen(NeoTheme.Border))
        {
            e.Graphics.DrawRectangle(
                borderPen,
                pageBounds.Left - 1,
                pageBounds.Top - 1,
                Math.Max(0, pageBounds.Width + 1),
                Math.Max(0, pageBounds.Height + 1));
        }

        using var tabFont = new Font("Segoe UI", 8F, FontStyle.Bold);
        for (int index = 0; index < TabCount; ++index)
        {
            Rectangle bounds = GetTabRect(index);
            bool selected = index == SelectedIndex;
            bool hovered = index == _hoverIndex;
            Color background = selected || hovered
                ? NeoTheme.SurfaceRaised
                : NeoTheme.Surface;

            using var backgroundBrush = new SolidBrush(background);
            e.Graphics.FillRectangle(backgroundBrush, bounds);

            using var borderPen = new Pen(NeoTheme.Border);
            e.Graphics.DrawRectangle(
                borderPen,
                bounds.Left,
                bounds.Top,
                Math.Max(0, bounds.Width - 1),
                Math.Max(0, bounds.Height - 1));

            if (selected)
            {
                using var accentBrush = new SolidBrush(NeoTheme.Accent);
                e.Graphics.FillRectangle(
                    accentBrush,
                    new Rectangle(
                        bounds.Left + 1,
                        bounds.Bottom - 3,
                        Math.Max(0, bounds.Width - 2),
                        3));
            }

            TextRenderer.DrawText(
                e.Graphics,
                TabPages[index].Text.ToUpperInvariant(),
                tabFont,
                bounds,
                selected ? NeoTheme.Text : NeoTheme.MutedText,
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hoverIndex = -1;
        for (int index = 0; index < TabCount; ++index)
        {
            if (GetTabRect(index).Contains(e.Location))
            {
                hoverIndex = index;
                break;
            }
        }

        if (_hoverIndex != hoverIndex)
        {
            _hoverIndex = hoverIndex;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverIndex = -1;
        Invalidate();
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        base.OnSelectedIndexChanged(e);
        Invalidate();
    }
}

internal static class NeoTheme
{
    public static readonly Color Canvas = Color.FromArgb(18, 20, 24);
    public static readonly Color Surface = Color.FromArgb(27, 30, 35);
    public static readonly Color SurfaceRaised = Color.FromArgb(36, 40, 46);
    public static readonly Color Input = Color.FromArgb(21, 24, 28);
    public static readonly Color ToolbarInput = Color.FromArgb(49, 55, 64);
    public static readonly Color Border = Color.FromArgb(58, 64, 73);
    public static readonly Color Text = Color.FromArgb(241, 244, 247);
    public static readonly Color MutedText = Color.FromArgb(163, 171, 181);
    public static readonly Color Accent = Color.FromArgb(32, 184, 166);
    public static readonly Color AccentHover = Color.FromArgb(45, 203, 184);
    public static readonly Color AccentPressed = Color.FromArgb(24, 145, 133);
    public static readonly Color Selection = Color.FromArgb(23, 108, 101);
    public static readonly Color Success = Color.FromArgb(90, 210, 140);
    public static readonly Color Warning = Color.FromArgb(244, 190, 79);
    public static readonly Color Danger = Color.FromArgb(242, 105, 112);

    private static readonly NeoColorTable ColorTable = new();

    public static void UseDarkTitleBar(IntPtr windowHandle)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == IntPtr.Zero)
            return;

        try
        {
            int enabled = 1;
            _ = DwmSetWindowAttribute(
                windowHandle,
                20,
                ref enabled,
                sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    public static void Apply(Form form)
    {
        form.SuspendLayout();
        form.BackColor = Canvas;
        form.ForeColor = Text;
        form.Font = new Font("Segoe UI", 9.5F, FontStyle.Regular);
        ApplyControl(form);
        form.ResumeLayout(true);
    }

    public static void StyleToolStrip(ToolStrip strip)
    {
        strip.BackColor = Surface;
        strip.ForeColor = Text;
        strip.GripStyle = ToolStripGripStyle.Hidden;
        strip.Padding = new Padding(6, 3, 6, 3);
        strip.RenderMode = ToolStripRenderMode.Professional;
        strip.Renderer = new ToolStripProfessionalRenderer(ColorTable);

        StyleToolStripItems(strip.Items, strip.Renderer);
    }

    private static void StyleToolStripItems(
        ToolStripItemCollection items,
        ToolStripRenderer renderer)
    {
        foreach (ToolStripItem item in items)
        {
            item.ForeColor = Text;
            item.BackColor = Surface;

            if (item is ToolStripTextBox textBox)
            {
                textBox.BackColor = ToolbarInput;
                textBox.ForeColor = Text;
                textBox.BorderStyle = BorderStyle.FixedSingle;
            }
            else if (item is ToolStripComboBox comboBox)
            {
                StyleToolStripComboBox(comboBox);
            }

            if (item is ToolStripDropDownItem dropDown)
            {
                dropDown.DropDown.BackColor = SurfaceRaised;
                dropDown.DropDown.ForeColor = Text;
                dropDown.DropDown.Renderer = renderer;
                StyleToolStripItems(dropDown.DropDownItems, renderer);
            }
        }
    }

    public static void RefreshToolStripComboBox(ToolStripComboBox comboBox)
    {
        comboBox.BackColor = ToolbarInput;
        comboBox.ForeColor = Text;
        comboBox.FlatStyle = FlatStyle.Flat;
        comboBox.ComboBox.BackColor = ToolbarInput;
        comboBox.ComboBox.ForeColor = Text;
        comboBox.ComboBox.Invalidate(true);
        comboBox.ComboBox.Update();
        comboBox.Invalidate();
        comboBox.Owner?.Invalidate(comboBox.Bounds);
    }

    private static void StyleToolStripComboBox(ToolStripComboBox comboBox)
    {
        RefreshToolStripComboBox(comboBox);

        ComboBox nativeComboBox = comboBox.ComboBox;
        if (nativeComboBox.DrawMode == DrawMode.OwnerDrawFixed)
            return;

        nativeComboBox.DrawMode = DrawMode.OwnerDrawFixed;
        nativeComboBox.ItemHeight = Math.Max(nativeComboBox.ItemHeight, 24);
        nativeComboBox.DrawItem += (_, e) =>
        {
            bool highlighted = nativeComboBox.DroppedDown &&
                (e.State & DrawItemState.Selected) != 0;
            using var background = new SolidBrush(
                highlighted ? Selection : ToolbarInput);
            e.Graphics.FillRectangle(background, e.Bounds);

            string itemText = e.Index >= 0 &&
                e.Index < nativeComboBox.Items.Count
                    ? nativeComboBox.Items[e.Index]?.ToString() ?? string.Empty
                    : nativeComboBox.Text;
            TextRenderer.DrawText(
                e.Graphics,
                itemText,
                nativeComboBox.Font,
                new Rectangle(
                    e.Bounds.Left + 6,
                    e.Bounds.Top,
                    Math.Max(0, e.Bounds.Width - 8),
                    e.Bounds.Height),
                Text,
                TextFormatFlags.Left |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis);
        };
    }

    public static void StyleTabs(TabControl tabs)
    {
        if (tabs is NeoTabControl neoTabs)
        {
            neoTabs.ItemSize = new Size(neoTabs.TabWidth, 40);
            neoTabs.BackColor = Canvas;
            return;
        }

        tabs.Appearance = TabAppearance.Normal;
        tabs.SizeMode = TabSizeMode.Fixed;
        tabs.ItemSize = new Size(112, 40);
        tabs.Padding = new Point(14, 5);
        tabs.BackColor = Canvas;

        tabs.DrawMode = TabDrawMode.OwnerDrawFixed;

        tabs.DrawItem += (_, e) =>
        {
            bool selected = e.Index == tabs.SelectedIndex;
            Rectangle bounds = e.Bounds;
            Color background = selected ? SurfaceRaised : Surface;

            using var backgroundBrush = new SolidBrush(background);
            e.Graphics.FillRectangle(backgroundBrush, bounds);

            if (selected)
            {
                using var accentBrush = new SolidBrush(Accent);
                e.Graphics.FillRectangle(
                    accentBrush,
                    new Rectangle(bounds.Left, bounds.Bottom - 3, bounds.Width, 3));
            }

            using var tabFont = new Font("Segoe UI", 9F, FontStyle.Bold);
            TextRenderer.DrawText(
                e.Graphics,
                tabs.TabPages[e.Index].Text.ToUpperInvariant(),
                tabFont,
                bounds,
                selected ? Text : MutedText,
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis);
        };
    }

    private static void ApplyControl(Control control)
    {
        switch (control)
        {
            case MapOverviewControl:
                return;

            case MenuStrip menu:
                StyleToolStrip(menu);
                menu.AutoSize = false;
                menu.Height = 38;
                break;

            case StatusStrip statusStrip:
                StyleToolStrip(statusStrip);
                break;

            case ToolStrip toolStrip:
                StyleToolStrip(toolStrip);
                break;

            case TabControl tabs:
                StyleTabs(tabs);
                break;

            case TabPage page:
                page.BackColor = Canvas;
                page.ForeColor = Text;
                page.Padding = new Padding(12);
                break;

            case DataGridView grid:
                StyleGrid(grid);
                break;

            case Button button:
                StyleButton(button);
                break;

            case TextBoxBase textBox:
                textBox.BackColor = Input;
                textBox.ForeColor = Text;
                textBox.BorderStyle = BorderStyle.FixedSingle;
                break;

            case ComboBox comboBox:
                StyleComboBox(comboBox);
                break;

            case NumericUpDown numeric:
                numeric.BackColor = Input;
                numeric.ForeColor = Text;
                numeric.BorderStyle = BorderStyle.FixedSingle;
                break;

            case DateTimePicker picker:
                picker.CalendarMonthBackground = Input;
                picker.CalendarForeColor = Text;
                picker.CalendarTitleBackColor = SurfaceRaised;
                picker.CalendarTitleForeColor = Text;
                break;

            case CheckedListBox checkedList:
                checkedList.BackColor = Input;
                checkedList.ForeColor = Text;
                checkedList.BorderStyle = BorderStyle.FixedSingle;
                break;

            case ListBox list:
                list.BackColor = Input;
                list.ForeColor = Text;
                list.BorderStyle = BorderStyle.FixedSingle;
                break;

            case ListView listView:
                listView.BackColor = Input;
                listView.ForeColor = Text;
                listView.BorderStyle = BorderStyle.FixedSingle;
                break;

            case GroupBox group:
                group.BackColor = Surface;
                group.ForeColor = Text;
                break;

            case FlowLayoutPanel flow:
                flow.BackColor = ResolveContainerColor(flow);
                flow.ForeColor = Text;
                break;

            case TableLayoutPanel table:
                table.BackColor = ResolveContainerColor(table);
                table.ForeColor = Text;
                break;

            case Panel panel:
                panel.BackColor = ResolveContainerColor(panel);
                panel.ForeColor = Text;
                break;

            case LinkLabel link:
                link.LinkColor = AccentHover;
                link.ActiveLinkColor = Accent;
                link.VisitedLinkColor = AccentHover;
                break;

            case Label label:
                if (label.ForeColor == SystemColors.ControlText ||
                    label.ForeColor == Color.Black)
                {
                    label.ForeColor = Text;
                }
                break;

            case CheckBox checkBox:
                checkBox.ForeColor = Text;
                checkBox.BackColor = Color.Transparent;
                break;

            case RadioButton radioButton:
                radioButton.ForeColor = Text;
                radioButton.BackColor = Color.Transparent;
                break;
        }

        foreach (Control child in control.Controls)
            ApplyControl(child);
    }

    private static Color ResolveContainerColor(Control control)
    {
        Color color = control.BackColor;
        return color == SystemColors.Control || color == Color.Transparent
            ? control.Parent?.BackColor ?? Canvas
            : color;
    }

    private static void StyleButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = SurfaceRaised;
        button.FlatAppearance.MouseDownBackColor = Input;
        button.UseVisualStyleBackColor = false;
        button.MinimumSize = new Size(button.MinimumSize.Width, 34);

        ApplyButtonState(button);
        button.EnabledChanged += (_, _) => ApplyButtonState(button);
    }

    private static void ApplyButtonState(Button button)
    {
        if (!button.Enabled)
        {
            button.BackColor = Color.FromArgb(49, 54, 62);
            button.ForeColor = Color.FromArgb(156, 164, 174);
            button.FlatAppearance.BorderColor = Color.FromArgb(76, 83, 93);
            button.Cursor = Cursors.Default;
            return;
        }

        button.BackColor = SurfaceRaised;
        button.ForeColor = Text;
        button.FlatAppearance.BorderColor = Color.FromArgb(76, 83, 93);
        button.Cursor = Cursors.Hand;

        string command = button.Text.Trim().ToUpperInvariant();
        if (command is "SAVE" or "SEND" or "CONNECT" or "CREATE OWNER" or
            "HOLD TO TALK" or "MAP LIST...")
        {
            button.BackColor = Accent;
            button.ForeColor = Color.FromArgb(8, 27, 25);
            button.FlatAppearance.BorderColor = Accent;
            button.FlatAppearance.MouseOverBackColor = AccentHover;
            button.FlatAppearance.MouseDownBackColor = AccentPressed;
        }
        else if (command.Contains("DELETE", StringComparison.Ordinal) ||
                 command.Contains("REMOVE", StringComparison.Ordinal) ||
                 command.Contains("UNBAN", StringComparison.Ordinal) ||
                 command.Contains("REVOKE", StringComparison.Ordinal) ||
                 command == "KICK BOTS")
        {
            button.FlatAppearance.BorderColor = Danger;
            button.ForeColor = Danger;
        }
    }

    private static void StyleComboBox(ComboBox comboBox)
    {
        comboBox.BackColor = Input;
        comboBox.ForeColor = Text;
        comboBox.FlatStyle = FlatStyle.Flat;
        comboBox.DrawMode = DrawMode.OwnerDrawFixed;
        comboBox.ItemHeight = Math.Max(comboBox.ItemHeight, 24);
        comboBox.DrawItem += (_, e) =>
        {
            Color background = (e.State & DrawItemState.Selected) != 0
                ? Selection
                : Input;
            using var brush = new SolidBrush(background);
            e.Graphics.FillRectangle(brush, e.Bounds);

            string text = e.Index >= 0 && e.Index < comboBox.Items.Count
                ? comboBox.Items[e.Index]?.ToString() ?? string.Empty
                : comboBox.Text;
            TextRenderer.DrawText(
                e.Graphics,
                text,
                comboBox.Font,
                new Rectangle(
                    e.Bounds.Left + 6,
                    e.Bounds.Top,
                    Math.Max(0, e.Bounds.Width - 8),
                    e.Bounds.Height),
                Text,
                TextFormatFlags.Left |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis);
        };
    }

    private static void StyleGrid(DataGridView grid)
    {
        grid.EnableHeadersVisualStyles = false;
        grid.BackgroundColor = Canvas;
        grid.BorderStyle = BorderStyle.None;
        grid.GridColor = Border;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.RowHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersHeight = Math.Max(grid.ColumnHeadersHeight, 38);
        grid.ColumnHeadersHeightSizeMode =
            DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.RowTemplate.Height = Math.Max(grid.RowTemplate.Height, 34);

        grid.DefaultCellStyle.BackColor = Surface;
        grid.DefaultCellStyle.ForeColor = Text;
        grid.DefaultCellStyle.SelectionBackColor = Selection;
        grid.DefaultCellStyle.SelectionForeColor = Color.White;
        grid.DefaultCellStyle.Padding = new Padding(5, 2, 5, 2);
        grid.DefaultCellStyle.Font = new Font("Segoe UI", 9.5F);

        grid.AlternatingRowsDefaultCellStyle.BackColor = SurfaceRaised;
        grid.AlternatingRowsDefaultCellStyle.ForeColor = Text;
        grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = Selection;
        grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = Color.White;

        grid.ColumnHeadersDefaultCellStyle.BackColor = Input;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = MutedText;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Input;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Text;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(5, 2, 5, 2);
        grid.ColumnHeadersDefaultCellStyle.Font =
            new Font("Segoe UI", 8.5F, FontStyle.Bold);
    }

    private sealed class NeoColorTable : ProfessionalColorTable
    {
        public override Color ToolStripGradientBegin => Surface;
        public override Color ToolStripGradientMiddle => Surface;
        public override Color ToolStripGradientEnd => Surface;
        public override Color MenuStripGradientBegin => Surface;
        public override Color MenuStripGradientEnd => Surface;
        public override Color ToolStripDropDownBackground => SurfaceRaised;
        public override Color ImageMarginGradientBegin => SurfaceRaised;
        public override Color ImageMarginGradientMiddle => SurfaceRaised;
        public override Color ImageMarginGradientEnd => SurfaceRaised;
        public override Color MenuItemSelected => Selection;
        public override Color MenuItemBorder => AccentPressed;
        public override Color MenuItemSelectedGradientBegin => Selection;
        public override Color MenuItemSelectedGradientEnd => Selection;
        public override Color MenuItemPressedGradientBegin => SurfaceRaised;
        public override Color MenuItemPressedGradientMiddle => SurfaceRaised;
        public override Color MenuItemPressedGradientEnd => SurfaceRaised;
        public override Color ButtonSelectedGradientBegin => Selection;
        public override Color ButtonSelectedGradientMiddle => Selection;
        public override Color ButtonSelectedGradientEnd => Selection;
        public override Color ButtonPressedGradientBegin => AccentPressed;
        public override Color ButtonPressedGradientMiddle => AccentPressed;
        public override Color ButtonPressedGradientEnd => AccentPressed;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
        public override Color ToolStripBorder => Border;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int value,
        int valueSize);
}
