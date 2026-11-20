namespace CamoConnector;

internal sealed partial class ConnectorWindow
{
    private Font? setupHeadingFont;
    private Label? detectionSummary;

    private Control CreateSetupView()
    {
        setupHeadingFont = new Font(Font.FontFamily, Font.Size + 13, FontStyle.Bold);
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top, ColumnCount = 2, RowCount = 2, Padding = new Padding(24)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var capture = new RoundedCard { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1 };
        var heading = new Label { Text = "Auto tracking", AutoSize = true, Font = setupHeadingFont };
        AddSetupRow(capture, heading);
        AddSetupRow(capture, new Label
        {
            Text = "Choose a capture source and track camo progress as you play.",
            AutoSize = true, Margin = new Padding(3, 2, 3, 14), ForeColor = ShellMuted
        });
        AddSetupRow(capture, automaticTracking);
        var sourceLabel = new Label { Text = "Capture source", AutoSize = true, Margin = new Padding(3, 12, 3, 3) };
        AddSetupRow(capture, sourceLabel);
        var picker = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
        picker.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        picker.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        picker.Controls.Add(sources, 0, 0);
        picker.Controls.Add(refreshSources, 1, 0);
        AddSetupRow(capture, picker);
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = Padding.Empty };
        actions.WrapContents = true;
        actions.Controls.Add(enableCaptureDevices);
        AddSetupRow(capture, actions);
        AddSetupRow(capture, detectorStatus);
        AddSetupRow(capture, new Label { Text = "Capture preview", AutoSize = true, Margin = new Padding(3, 12, 3, 4) });
        var previewRow = capture.RowCount++;
        capture.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var previewFrame = new RoundedCard
        {
            ColumnCount = 1, RowCount = 1, Dock = DockStyle.Fill,
            Margin = new Padding(3), Padding = new Padding(10)
        };
        previewFrame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        previewFrame.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        previewFrame.Controls.Add(preview, 0, 0);
        capture.Controls.Add(previewFrame, 0, previewRow);
        AddSetupRow(capture, previewStatus);

        detectionSummary = new Label { AutoSize = true };
        var trackerShortcut = new Button { Text = "Open Tracker", AutoSize = true };
        trackerShortcut.Click += (_, _) => pages.SelectedTab = localTrackerPage;
        var syncHelp = new Label { Text = "Use automatic PC detection or select a window, monitor, or capture device.", AutoSize = true };
        var sidebar = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = new Padding(4, 0, 0, 0) };
        AddSetupRow(sidebar, SetupSection("Connection", status, detectionSummary));
        AddSetupRow(sidebar, SetupSection("Capture sources", syncHelp, trackerShortcut));
        grid.Controls.Add(capture, 0, 0);
        grid.Controls.Add(sidebar, 1, 0);
        viewport.Controls.Add(grid);

        void FitSetup()
        {
            // Leave room for a scrollbar without changing the column breakpoint when it appears.
            var width = viewport.ClientSize.Width - SystemInformation.VerticalScrollBarWidth;
            var wide = width >= LogicalToDeviceUnits(820);
            grid.SuspendLayout();
            grid.ColumnStyles[0].Width = wide ? 68 : 100;
            grid.ColumnStyles[1].Width = wide ? 32 : 0;
            grid.SetCellPosition(sidebar, new TableLayoutPanelCellPosition(wide ? 1 : 0, wide ? 0 : 1));
            var available = width - grid.Padding.Horizontal;
            var sidebarWidth = Math.Max(1, (wide ? available * 32 / 100 : available) - LogicalToDeviceUnits(55));
            foreach (var label in new[] { status, detectionSummary, syncHelp })
                label!.MaximumSize = new Size(sidebarWidth, 0);
            var captureWidth = Math.Max(1, (wide ? available * 68 / 100 : available) - LogicalToDeviceUnits(35));
            detectorStatus.MaximumSize = previewStatus.MaximumSize = new Size(captureWidth, 0);
            var minimumCaptureHeight = LogicalToDeviceUnits(445) + detectorStatus.Height + previewStatus.Height;
            var sidebarHeight = sidebar.GetPreferredSize(new Size(sidebarWidth + LogicalToDeviceUnits(55), 0)).Height;
            var minimum = (wide ? Math.Max(minimumCaptureHeight, sidebarHeight) : minimumCaptureHeight + sidebarHeight)
                + grid.Padding.Vertical + LogicalToDeviceUnits(12);
            grid.Height = Math.Max(viewport.ClientSize.Height, minimum);
            grid.ResumeLayout();
        }

        viewport.SizeChanged += (_, _) => FitSetup();
        foreach (var label in new[] { status, detectionSummary, detectorStatus, previewStatus })
            label!.TextChanged += (_, _) => FitSetup();
        BindCaptureControls();
        return viewport;
    }

    private static void AddSetupRow(TableLayoutPanel table, Control control)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(control, 0, row);
    }

    private RoundedCard SetupSection(string title, params Control[] controls)
    {
        var section = new RoundedCard
        {
            AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1,
            Margin = new Padding(3, 0, 3, 14), Padding = new Padding(20, 16, 20, 15)
        };
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddSetupRow(section, new Label
        {
            Text = title, AutoSize = true, Font = ShellFont(Font.FontFamily.Name, 12, FontStyle.Bold),
            Margin = new Padding(3, 0, 3, 8)
        });
        foreach (var control in controls) AddSetupRow(section, control);
        return section;
    }
}
