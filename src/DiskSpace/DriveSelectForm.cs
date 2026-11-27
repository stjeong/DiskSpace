namespace DiskSpace;

/// <summary>Start-up dialog: pick a drive (icon view) or type a path.</summary>
public sealed class DriveSelectForm : Form
{
    private readonly ListView driveList = new();
    private readonly ImageList images = new();
    private readonly TextBox pathBox = new();
    private readonly Button startButton = new();
    private readonly Button browseButton = new();
    private readonly Label pathLabel = new() { Text = "Path:", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly TableLayoutPanel bottom = new()
    {
        Dock = DockStyle.Bottom,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 4,
        RowCount = 1,
    };
    private int layoutDpi;

    public DriveSelectForm()
    {
        // PerMonitorV2: pixel sizes are applied by hand from 96-DPI values (see ApplyDpi) so that
        // repeated DPI changes never drift; WinForms only resizes the window and rescales fonts.
        AutoScaleMode = AutoScaleMode.None;
        Text = "드라이브 선택 또는 경로 입력";
        Icon = AppIcon.Value;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = Dpi.Scale(new Size(460, 300), DeviceDpi); // no handle yet: DeviceDpi is the form's initial DPI
        Font = new Font("Segoe UI", 9f);

        images.ColorDepth = ColorDepth.Depth32Bit;

        driveList.View = View.LargeIcon;
        driveList.LargeImageList = images;
        driveList.MultiSelect = false;
        driveList.HideSelection = false;
        driveList.Dock = DockStyle.Fill;
        driveList.BorderStyle = BorderStyle.None;
        driveList.ShowItemToolTips = true;
        driveList.SelectedIndexChanged += (_, _) =>
        {
            if (driveList.SelectedItems.Count > 0)
                pathBox.Text = (string)driveList.SelectedItems[0].Tag!;
        };
        driveList.ItemActivate += (_, _) => Accept();

        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        pathBox.Dock = DockStyle.Fill;
        pathBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Accept(); }
        };
        browseButton.Text = "...";
        browseButton.AutoSize = true;
        browseButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        browseButton.Click += (_, _) => Browse();
        startButton.Text = "Start";
        startButton.AutoSize = true;
        startButton.AutoSizeMode = AutoSizeMode.GrowAndShrink; // GrowOnly would never shrink back after a DPI decrease
        startButton.Click += (_, _) => Accept();

        bottom.Controls.Add(pathLabel, 0, 0);
        bottom.Controls.Add(pathBox, 1, 0);
        bottom.Controls.Add(browseButton, 2, 0);
        bottom.Controls.Add(startButton, 3, 0);

        Controls.Add(driveList);
        Controls.Add(bottom);
        AcceptButton = startButton;

        LoadDrives();
        ApplyDpi(DeviceDpi);
    }

    // ---- DPI ------------------------------------------------------------------------------

    /// <summary>Sets every pixel size from its 96-DPI value. Safe to call repeatedly.</summary>
    private void ApplyDpi(int dpi)
    {
        layoutDpi = dpi;
        MinimumSize = Dpi.Scale(new Size(360, 260), dpi);
        bottom.Padding = Dpi.Scale(new Padding(6, 6, 6, 4), dpi);
        pathLabel.Margin = Dpi.Scale(new Padding(0, 6, 4, 0), dpi);
        pathBox.Margin = browseButton.Margin = startButton.Margin = Dpi.Scale(new Padding(3), dpi);
        browseButton.MinimumSize = new Size(Dpi.Scale(32, dpi), 0);
        startButton.MinimumSize = new Size(Dpi.Scale(90, dpi), 0);

        int icon = Dpi.Scale(32, dpi);
        if (images.ImageSize.Width != icon)
        {
            images.ImageSize = new Size(icon, icon); // clears the list, so reload the drive icons
            LoadImages();
        }

        if (IsHandleCreated) UpdateIconSpacing();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // The window may have been created on a monitor whose DPI differs from the one used in the constructor.
        if (DeviceDpi != layoutDpi) ApplyDpi(DeviceDpi);
        else UpdateIconSpacing();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        // Drop the old minimum first so it can't stop the window from shrinking to the suggested bounds.
        MinimumSize = Size.Empty;
        base.OnDpiChanged(e); // resizes the window and rescales fonts
        ApplyDpi(e.DeviceDpiNew);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (IsHandleCreated) UpdateIconSpacing(); // spacing depends on the (DPI-scaled) font
    }

    /// <summary>Wider icon cells so labels like "New Volume (D:)" wrap onto two lines instead of being cut.</summary>
    private void UpdateIconSpacing()
    {
        Font f = driveList.Font;
        int cx = TextRenderer.MeasureText("New Volume", f).Width + Dpi.Scale(12, DeviceDpi);
        int cy = images.ImageSize.Height + f.Height * 2 + Dpi.Scale(16, DeviceDpi);
        NativeMethods.SetIconSpacing(driveList.Handle, cx, cy);
        driveList.ArrangeIcons();
    }

    /// <summary>The path the user chose, e.g. "C:\".</summary>
    public string? SelectedPath { get; private set; }

    private void LoadDrives()
    {
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            string root = drive.Name; // "C:\"
            string letter = root.TrimEnd('\\');
            string label;
            string tip;
            try
            {
                if (!drive.IsReady) continue;
                label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? $"({letter})" : $"{drive.VolumeLabel} ({letter})";
                tip = $"{label}\n{drive.DriveFormat}, {drive.DriveType}\n" +
                      $"사용: {SizeFormat.Format(drive.TotalSize - drive.TotalFreeSpace)} / 전체: {SizeFormat.Format(drive.TotalSize)}";
            }
            catch
            {
                continue;
            }

            driveList.Items.Add(new ListViewItem(label, root) { Tag = root, ToolTipText = tip }); // image key = root
        }

        if (driveList.Items.Count > 0)
            driveList.Items[0].Selected = true;
    }

    /// <summary>(Re)loads the drive icons into the image list at its current size, keyed by drive root.</summary>
    private void LoadImages()
    {
        images.Images.Clear();
        foreach (ListViewItem item in driveList.Items)
        {
            string root = (string)item.Tag!;
            var (icon, _) = NativeMethods.GetShellInfo(root);
            if (icon is null) continue;
            images.Images.Add(root, icon.ToBitmap()); // ImageList reads lazily, so copy before disposing
            icon.Dispose();
        }
    }

    private void Browse()
    {
        using var dlg = new FolderBrowserDialog { Description = "분석할 폴더 선택", UseDescriptionForTitle = true };
        if (Directory.Exists(pathBox.Text)) dlg.InitialDirectory = pathBox.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK)
            pathBox.Text = dlg.SelectedPath;
    }

    private void Accept()
    {
        string text = pathBox.Text.Trim().Trim('"');
        if (text.Length == 2 && text[1] == ':') text += "\\"; // "C:" means current dir of C, we want the root

        string full;
        try
        {
            full = Path.GetFullPath(text);
        }
        catch
        {
            full = "";
        }

        if (full.Length == 0 || !Directory.Exists(full))
        {
            MessageBox.Show(this, $"폴더를 찾을 수 없습니다:\n{text}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Keep "C:\" but drop a trailing separator on ordinary folders.
        if (full.Length > 3) full = full.TrimEnd('\\');

        SelectedPath = full;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) images.Dispose();
        base.Dispose(disposing);
    }
}
