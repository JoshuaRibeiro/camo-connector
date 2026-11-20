namespace CamoConnector;

internal sealed partial class ConnectorWindow
{
    private static readonly Color ShellBackground = Color.FromArgb(17, 24, 33);
    private static readonly Color ShellPanel = Color.FromArgb(27, 40, 53);
    private static readonly Color ShellAccent = Color.FromArgb(123, 224, 205);
    private static readonly Color ShellMuted = Color.FromArgb(176, 198, 216);
    private readonly TabPage homePage = new("Home"), capturePage = new("Auto tracking"),
        helpPage = new("How it works"), settingsPage = new("Settings");
    private readonly List<Font> shellFonts = [];
    private readonly Dictionary<TabPage, Button> railButtons = [];
    private readonly Panel rail = new() { Dock = DockStyle.Left };
    private readonly TableLayoutPanel captureBanner = new() { Dock = DockStyle.Top, ColumnCount = 2, Padding = new Padding(12, 5, 12, 5) };
    private bool railExpanded, diagnosticsUnlocked;

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
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(6, 10, 6, 0),
            AutoScroll = true
        };
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, WrapContents = false, Padding = new Padding(6, 4, 0, 0) };
        var version = new Button { Text = "v" + build, Height = 30, AutoSize = true, Margin = Padding.Empty, AccessibleName = "Version " + build };
        footer.Controls.Add(version);
        var toggle = new Button { Height = 38, AccessibleName = "Expand navigation", Margin = new Padding(0, 0, 0, 8) };
        menu.Controls.Add(toggle);
        var entries = new[] { (homePage, "\uE80F"), (localTrackerPage, "\uE8FD"), (capturePage, "\uE7F8"),
            (helpPage, "\uE897"), (settingsPage, "\uE713"), (logsPage, "\uE9F9") };
        var icons = ShellFont("Segoe MDL2 Assets", 14);
        var hints = new ToolTip();
        Disposed += (_, _) => { hints.Dispose(); logsPage.Dispose(); };
        hints.SetToolTip(version, "Camo Connector version");
        foreach (var (page, _) in entries)
        {
            var button = new Button { Height = 40, Margin = new Padding(0, 0, 0, 4), AccessibleName = page.Text };
            button.Click += (_, _) => pages.SelectedTab = page;
            hints.SetToolTip(button, page.Text);
            menu.Controls.Add(button);
            railButtons.Add(page, button);
        }
        void UpdateRail()
        {
            rail.Width = LogicalToDeviceUnits(railExpanded ? 174 : 72);
            var buttonWidth = LogicalToDeviceUnits(railExpanded ? 150 : 54);
            toggle.Width = buttonWidth;
            toggle.Font = railExpanded ? Font : icons;
            toggle.Text = railExpanded ? "Collapse" : "\uE700";
            toggle.AccessibleName = railExpanded ? "Collapse navigation" : "Expand navigation";
            hints.SetToolTip(toggle, toggle.AccessibleName);
            foreach (var (page, glyph) in entries)
            {
                var button = railButtons[page];
                button.Visible = page != logsPage || diagnosticsUnlocked;
                button.Width = buttonWidth;
                button.Font = railExpanded ? Font : icons;
                button.Text = railExpanded ? page.Text : glyph;
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
        captureBanner.Controls.Add(captureBannerText, 0, 0);
        captureBanner.Controls.Add(captureShortcut, 1, 0);
        captureBanner.Height = 46;
        captureBannerText.AutoSize = false;
        captureBannerText.Dock = DockStyle.Fill;
        captureBannerText.TextAlign = ContentAlignment.MiddleLeft;
        captureBannerText.AutoEllipsis = true;
        var content = new Panel { Dock = DockStyle.Fill };
        content.Controls.Add(pages);
        content.Controls.Add(captureBanner);
        Controls.Add(content);
        Controls.Add(rail);

        var home = PageBody(homePage, "Camo Connector", "Track your multiplayer camo grind and keep your OBS overlay in sync.");
        AddSetupRow(home, SetupSection("Camo tracker", new Label { AutoSize = true, Text = "See your next unlocks, search the weapon roster, and adjust progress." },
            PageButton("Open Camo tracker", localTrackerPage)));
        AddSetupRow(home, SetupSection("Tracking setup", new Label { AutoSize = true, Text = "Use automatic PC detection or select a window, monitor, or capture device." },
            PageButton("Open Auto tracking", capturePage)));
        var help = PageBody(helpPage, "How it works", "Camo Connector reads your game capture and saves progress on this PC.");
        AddSetupRow(help, SetupSection("PC capture", new Label { AutoSize = true, Text = "Open Call of Duty and bring the game to the foreground. If needed, select its window in Auto tracking." }));
        AddSetupRow(help, SetupSection("Console or OBS", new Label { AutoSize = true, Text = "Select your capture card or OBS Virtual Camera in Auto tracking, then start detection." }));
        AddSetupRow(help, SetupSection("OBS overlay", new Label { AutoSize = true, Text = "Copy the URL from Camo tracker and paste it into an OBS Browser Source." }));
        var settings = PageBody(settingsPage, "Settings", "Manage the desktop app and website sync.");
        update.AutoSize = true;
        updateStatus.Text = "Check for a new desktop release.";
        AddSetupRow(settings, SetupSection("App updates", update, updateStatus, extensionStatus));
        AddSetupRow(settings, SetupSection("Website sync", PageButton("Open website sync", trackerPage)));
        var tray = new Button { Text = "Hide to tray", AutoSize = true };
        tray.Click += (_, _) => HideTrackerToTray();
        AddSetupRow(settings, SetupSection("Desktop", tray));
        pages.SelectedIndexChanged += (_, _) =>
        {
            StyleShell();
            if (pages.SelectedTab == capturePage) StartPreview();
            else if (detector == null) StopPreview();
        };
        pages.SelectedTab = homePage;
        UpdateRail();
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
                if (child is GroupBox group)
                    foreach (var text in group.Controls[0].Controls.OfType<Label>()) text.MaximumSize = new Size(width, 0);
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
        if (dark != true) return;
        rail.BackColor = captureBanner.BackColor = ShellPanel;
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
