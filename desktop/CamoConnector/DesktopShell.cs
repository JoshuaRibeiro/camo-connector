using System.Drawing.Drawing2D;

namespace CamoConnector;

internal sealed partial class ConnectorWindow
{
    private static readonly Color ShellBackground = Color.FromArgb(17, 20, 25);
    private static readonly Color ShellPanel = Color.FromArgb(26, 32, 39);
    private static readonly Color ShellBorder = Color.FromArgb(64, 88, 101);
    private static readonly Color ShellButton = Color.FromArgb(33, 45, 56);
    private static readonly Color ShellAccent = Color.FromArgb(141, 215, 206);
    private static readonly Color ShellText = Color.FromArgb(215, 226, 232);
    private static readonly Color ShellMuted = Color.FromArgb(158, 179, 193);
    private readonly TabPage capturePage = new("Auto tracking"),
        helpPage = new("How it works"), settingsPage = new("Settings");
    private readonly List<Font> shellFonts = [];
    private readonly Dictionary<TabPage, Button> railButtons = [];
    private readonly Panel rail = new() { Dock = DockStyle.Left };
    private readonly TableLayoutPanel captureBanner = new() { Dock = DockStyle.Top, ColumnCount = 3, Padding = new Padding(12, 5, 12, 5) };
    private Button? versionBadge;
    private Button? navigationToggle;
    private bool railExpanded, diagnosticsUnlocked;

    private sealed class RoundedCard : TableLayoutPanel
    {
        public RoundedCard() => DoubleBuffered = true;

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            if (SystemInformation.HighContrast || Width < 4 || Height < 4) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var outline = RoundedOutline(ClientSize, LogicalToDeviceUnits(19), 1);
            using var fill = new SolidBrush(ShellPanel);
            using var stroke = new Pen(ShellBorder);
            e.Graphics.FillPath(fill, outline);
            e.Graphics.DrawPath(stroke, outline);
        }
    }

    private static GraphicsPath RoundedOutline(Size size, int radius, int inset)
    {
        var bounds = new Rectangle(inset, inset, size.Width - inset * 2, size.Height - inset * 2);
        radius = Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2);
        var path = new GraphicsPath();
        if (radius == 0) path.AddRectangle(bounds);
        else if (bounds.Width == bounds.Height && radius == bounds.Height / 2) path.AddEllipse(bounds);
        else path.AddRoundedRectangle(bounds, new Size(radius, radius));
        return path;
    }

    private sealed class PageDeck : TabControl
    {
        public PageDeck()
        {
            Appearance = TabAppearance.FlatButtons;
            SizeMode = TabSizeMode.Fixed;
            ItemSize = new Size(1, 1);
            TabStop = false;
        }

        protected override void WndProc(ref Message message)
        {
            const int adjustRect = 0x1328;
            if (message.Msg == adjustRect) { message.Result = 0; return; }
            base.WndProc(ref message);
        }
    }

    private Font ShellFont(string family, float size, FontStyle style = FontStyle.Regular)
    {
        var font = new Font(family, size, style);
        shellFonts.Add(font);
        return font;
    }

    private void BuildShell(string build)
    {
        var menu = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(LogicalToDeviceUnits(6), 10, LogicalToDeviceUnits(6), 0),
            AutoScroll = true
        };
        var footer = new Panel { Dock = DockStyle.Bottom, Height = LogicalToDeviceUnits(23) };
        var version = versionBadge = new Button { Text = "v" + build, Dock = DockStyle.Fill, Padding = Padding.Empty, Margin = Padding.Empty, Font = ShellFont("Bahnschrift", 8), AccessibleName = "Version " + build };
        footer.Controls.Add(version);
        var toggle = navigationToggle = new Button { Height = 38, AccessibleName = "Expand navigation", Margin = new Padding(0, 0, 0, 8) };
        menu.Controls.Add(toggle);
        var entries = new[] { (localTrackerPage, ""), (capturePage, "\uE7F8"),
            (helpPage, "\uE897"), (settingsPage, "\uE713"), (logsPage, "\uE9F9") };
        var icons = ShellFont("Segoe MDL2 Assets", 14);
        var hints = new ToolTip();
        Disposed += (_, _) => { hints.Dispose(); logsPage.Dispose(); };
        hints.SetToolTip(version, "Camo Connector version");
        using var appBitmap = Icon!.ToBitmap();
        var trackerIcon = new Bitmap(appBitmap, new Size(LogicalToDeviceUnits(20), LogicalToDeviceUnits(20)));
        Disposed += (_, _) => trackerIcon.Dispose();
        foreach (var (page, glyph) in entries)
        {
            var button = new Button { Height = 40, Margin = new Padding(0, 0, 0, 4), AccessibleName = page.Text };
            button.Paint += (_, args) =>
            {
                var iconArea = new Rectangle(0, 0, railExpanded ? LogicalToDeviceUnits(42) : button.Width, button.Height);
                if (page == localTrackerPage)
                    args.Graphics.DrawImageUnscaled(trackerIcon,
                        (iconArea.Width - trackerIcon.Width) / 2, (iconArea.Height - trackerIcon.Height) / 2);
                else
                    TextRenderer.DrawText(args.Graphics, glyph, icons, iconArea, button.ForeColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            };
            button.Click += (_, _) => pages.SelectedTab = page;
            hints.SetToolTip(button, page.Text);
            menu.Controls.Add(button);
            railButtons.Add(page, button);
        }
        void UpdateRail()
        {
            rail.Width = LogicalToDeviceUnits(railExpanded ? 162 : 52);
            var buttonWidth = LogicalToDeviceUnits(railExpanded ? 150 : 40);
            toggle.Height = LogicalToDeviceUnits(40);
            toggle.Width = buttonWidth;
            toggle.Font = railExpanded ? Font : icons;
            toggle.Text = railExpanded ? "Collapse" : "\uE700";
            toggle.AccessibleName = railExpanded ? "Collapse navigation" : "Expand navigation";
            hints.SetToolTip(toggle, toggle.AccessibleName);
            foreach (var (page, _) in entries)
            {
                var button = railButtons[page];
                button.Visible = page != logsPage || diagnosticsUnlocked;
                button.Size = new Size(buttonWidth, LogicalToDeviceUnits(40));
                button.Padding = railExpanded ? new Padding(LogicalToDeviceUnits(42), 0, 0, 0) : Padding.Empty;
                button.Text = railExpanded ? page.Text : "";
                button.TextAlign = railExpanded ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleCenter;
            }
            StyleShell();
        }
        version.Click += (_, _) =>
        {
            diagnosticsUnlocked = !diagnosticsUnlocked;
            if (diagnosticsUnlocked)
            {
                pages.TabPages.Add(logsPage);
                ApplyTheme(logsPage, dark == true);
                pages.SelectedTab = logsPage;
            }
            else
            {
                pages.SelectedTab = settingsPage;
                pages.TabPages.Remove(logsPage);
            }
            UpdateRail();
        };
        toggle.Click += (_, _) => { railExpanded = !railExpanded; UpdateRail(); };
        rail.Controls.Add(menu);
        rail.Controls.Add(footer);

        var captureShortcut = PageButton("Open Auto tracking", capturePage);
        captureBanner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        captureBanner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        captureBanner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        captureBanner.Controls.Add(captureBannerText, 0, 0);
        captureBanner.Controls.Add(reloadTracker, 2, 0);
        reloadTracker.Visible = pages.SelectedTab == localTrackerPage;
        captureBanner.Controls.Add(captureShortcut, 1, 0);
        captureBanner.Height = LogicalToDeviceUnits(54);
        captureBannerText.AutoSize = false;
        captureBannerText.Dock = DockStyle.Fill;
        captureBannerText.TextAlign = ContentAlignment.MiddleLeft;
        captureBannerText.AutoEllipsis = true;
        var content = new Panel { Dock = DockStyle.Fill };
        content.Controls.Add(pages);
        content.Controls.Add(captureBanner);
        Controls.Add(content);
        Controls.Add(rail);

        var help = PageBody(helpPage, "How it works", "Camo Connector reads your game capture and saves progress on this PC.");
        AddSetupRow(help, SetupSection("PC capture", new Label { AutoSize = true, Text = "Select the monitor showing Call of Duty in Auto tracking." }));
        AddSetupRow(help, SetupSection("Console or OBS", new Label { AutoSize = true, Text = "Select your capture card or OBS Virtual Camera in Auto tracking and turn on Auto tracking." }));
        AddSetupRow(help, SetupSection("OBS overlay", new Label { AutoSize = true, Text = "Copy the URL from Camo tracker and paste it into an OBS Browser Source." }));
        var settings = PageBody(settingsPage, "Settings", "Manage desktop updates and tray controls.");
        update.AutoSize = true;
        updateStatus.Text = $"Camo Connector v{build}";
        AddSetupRow(settings, SetupSection("App updates", updateStatus, update));
        var tray = new Button { Text = "Hide to tray", AutoSize = true };
        tray.Click += (_, _) => HideTrackerToTray();
        AddSetupRow(settings, SetupSection("Desktop",
            new Label { Text = "Keep Camo Connector running in the notification area.", AutoSize = true }, tray));
        pages.SelectedIndexChanged += (_, _) =>
        {
            reloadTracker.Visible = pages.SelectedTab == localTrackerPage;
            StyleShell();
            if (pages.SelectedTab == capturePage) StartPreview();
            else if (detector == null) StopPreview();
        };
        pages.SelectedTab = localTrackerPage;
        UpdateRail();
        ShapeButtons(this);
        ShapeButtons(logsPage);
    }

    private void ShapeButtons(Control parent)
    {
        if (parent is Button button && button != versionBadge && button != navigationToggle && !railButtons.ContainsValue(button))
        {
            if (button.AutoSize && button != versionBadge)
            {
                button.MinimumSize = new Size(0, LogicalToDeviceUnits(34));
                var padding = LogicalToDeviceUnits(12);
                button.Padding = new Padding(Math.Max(button.Padding.Left, padding), button.Padding.Top,
                    Math.Max(button.Padding.Right, padding), button.Padding.Bottom);
            }
            void ResizeShape(object? sender, EventArgs args)
            {
                if (button.Width < 2 || button.Height < 2) return;
                using var path = RoundedOutline(button.ClientSize, Math.Min(button.Width, button.Height) / 2, 0);
                var previous = button.Region;
                button.Region = new Region(path);
                previous?.Dispose();
            }
            button.SizeChanged += ResizeShape;
            button.Paint += (_, args) =>
            {
                if (SystemInformation.HighContrast || button.FlatAppearance.BorderSize != 0 ||
                    button.AccessibleName?.StartsWith("Version ", StringComparison.Ordinal) == true ||
                    button.Width < 4 || button.Height < 4) return;
                args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var outline = RoundedOutline(button.ClientSize, button.Height / 2, 1);
                using var stroke = new Pen(button.Focused ? ShellAccent : ShellBorder);
                args.Graphics.DrawPath(stroke, outline);
            };
            ResizeShape(null, EventArgs.Empty);
        }
        foreach (Control child in parent.Controls) ShapeButtons(child);
    }

    private TableLayoutPanel PageBody(TabPage page, string title, string description)
    {
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var body = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(24) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddSetupRow(body, new Label { Text = title, AutoSize = true, Font = ShellFont(Font.FontFamily.Name, 25, FontStyle.Bold) });
        AddSetupRow(body, new Label { Text = description, AutoSize = true, Margin = new Padding(3, 2, 3, 14) });
        viewport.Controls.Add(body);
        page.Controls.Add(viewport);
        viewport.SizeChanged += (_, _) =>
        {
            var width = Math.Max(120, viewport.ClientSize.Width - body.Padding.Horizontal - 42);
            foreach (Control child in body.Controls)
            {
                if (child is Label label) label.MaximumSize = new Size(width, 0);
                if (child is RoundedCard card)
                    foreach (var text in card.Controls.OfType<Label>())
                        text.MaximumSize = new Size(Math.Max(80, width - card.Padding.Horizontal), 0);
            }
        };
        return body;
    }

    private Button PageButton(string label, TabPage page)
    {
        var button = new Button { Text = label, AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        button.Click += (_, _) => pages.SelectedTab = page;
        return button;
    }

    private void StyleShell()
    {
        if (versionBadge is { } version)
        {
            version.FlatStyle = FlatStyle.Flat;
            version.FlatAppearance.BorderSize = 0;
            version.BackColor = dark == true ? ShellPanel : SystemColors.Control;
            version.FlatAppearance.MouseOverBackColor = version.BackColor;
            version.FlatAppearance.MouseDownBackColor = version.BackColor;
            version.ForeColor = dark == true ? ShellMuted : SystemColors.GrayText;
        }
        if (dark != true) return;
        rail.BackColor = ShellPanel;
        captureBanner.BackColor = captureBannerText.BackColor = ShellBackground;
        foreach (Control child in rail.Controls) child.BackColor = ShellPanel;
        foreach (var (page, button) in railButtons)
        {
            var selected = page == pages.SelectedTab;
            button.BackColor = selected ? Color.FromArgb(34, 68, 75) : ShellPanel;
            button.ForeColor = selected ? ShellAccent : ShellMuted;
            button.FlatAppearance.BorderSize = 0;
        }
        RefreshCaptureBanner();
    }
}
