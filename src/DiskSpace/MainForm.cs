using System.Diagnostics;

namespace DiskSpace;

/// <summary>Main window: treemap of the scanned folder with live progress in the title bar.</summary>
public sealed class MainForm : Form
{
    private const string AppName = "DiskSpace";

    private readonly TreemapView map = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    private readonly ToolStripStatusLabel statusLabel = new() { Text = "Ready" };
    private readonly ToolStripStatusLabel hoverLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    // AutoSize off: otherwise the strip's layout resets the width to the item's unscaled default (100px).
    private readonly ToolStripProgressBar progressBar = new() { Maximum = 100, AutoSize = false };
    private readonly ToolStripButton backButton = new("◀ 뒤로") { ToolTipText = "뒤로 (Backspace)" };
    private readonly ToolStripButton forwardButton = new("앞으로 ▶") { ToolTipText = "앞으로" };
    private readonly ToolStripButton upButton = new("▲ 상위 폴더") { ToolTipText = "상위 폴더" };
    private readonly ToolStripButton homeButton = new("⌂ 루트") { ToolTipText = "스캔 루트로 이동" };
    private readonly ToolStripButton rescanButton = new("⟳ 다시 스캔") { ToolTipText = "다시 스캔 (F5)" };
    private readonly ToolStripButton emptyBinButton = new("🗑 휴지통 비우기") { ToolTipText = "휴지통을 비운 후 다시 스캔" };
    private readonly ToolStripButton newButton = new();
    private readonly ToolStripButton accessDeniedButton = new()
    {
        Alignment = ToolStripItemAlignment.Right,
        ForeColor = Color.Firebrick,
        ToolTipText = "보안(권한) 문제로 접근하지 못한 폴더 목록을 메모장으로 엽니다",
        Visible = false,
    };
    private readonly ToolStripMenuItem rescanMenuItem = new("다시 스캔(&R)", null, null, Keys.F5);
    private readonly ToolStripMenuItem stopMenuItem = new("스캔 중지(&S)") { ShortcutKeyDisplayString = "Esc" };
    private readonly ToolStripMenuItem emptyBinMenuItem = new("휴지통 비우기(&E)...");
    private readonly ContextMenuStrip contextMenu = new();
    private readonly Stack<DirNode> backStack = new();
    private readonly Stack<DirNode> forwardStack = new();

    private Scanner? scanner;
    private Node? contextNode;

    public MainForm(string path)
    {
        // PerMonitorV2: sizes are applied by hand from 96-DPI values (see ApplyDpi); WinForms rescales fonts.
        AutoScaleMode = AutoScaleMode.None;
        Text = AppName;
        Icon = AppIcon.Value;
        StartPosition = FormStartPosition.CenterScreen;
        Size = Dpi.Scale(new Size(1280, 800), DeviceDpi); // no handle yet: DeviceDpi is the form's initial DPI
        Font = new Font("Segoe UI", 9f);
        KeyPreview = true;

        // Menu
        var menu = new MenuStrip();
        var fileMenu = new ToolStripMenuItem("파일(&F)");
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("새 스캔(&N)...", null, (_, _) => NewScan(), Keys.Control | Keys.N));
        rescanMenuItem.Click += (_, _) => Rescan();
        stopMenuItem.Click += (_, _) => StopScan();
        emptyBinMenuItem.Click += (_, _) => EmptyRecycleBinAndRescan();
        fileMenu.DropDownItems.Add(rescanMenuItem);
        fileMenu.DropDownItems.Add(stopMenuItem);
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(emptyBinMenuItem);
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("끝내기(&X)", null, (_, _) => Close()));
        var viewMenu = new ToolStripMenuItem("보기(&V)");
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("뒤로", null, (_, _) => GoBack(), Keys.Alt | Keys.Left));
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("앞으로", null, (_, _) => GoForward(), Keys.Alt | Keys.Right));
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("상위 폴더", null, (_, _) => GoUp(), Keys.Alt | Keys.Up));
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("루트", null, (_, _) => GoHome(), Keys.Alt | Keys.Home));
        menu.Items.Add(fileMenu);
        menu.Items.Add(viewMenu);
        MainMenuStrip = menu;

        // Toolbar
        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, ImageScalingSize = new Size(20, 20) };
        // Acts as "새 스캔" when idle and as "취소" while a scan is running.
        newButton.Click += (_, _) =>
        {
            if (IsScanning) StopScan();
            else NewScan();
        };
        backButton.Click += (_, _) => GoBack();
        forwardButton.Click += (_, _) => GoForward();
        upButton.Click += (_, _) => GoUp();
        homeButton.Click += (_, _) => GoHome();
        rescanButton.Click += (_, _) => Rescan();
        emptyBinButton.Click += (_, _) => EmptyRecycleBinAndRescan();
        accessDeniedButton.Click += (_, _) => ShowAccessDeniedPaths();
        toolbar.Items.AddRange([newButton, new ToolStripSeparator(), backButton, forwardButton, upButton, homeButton,
            new ToolStripSeparator(), rescanButton, new ToolStripSeparator(), emptyBinButton, accessDeniedButton]);

        // Status bar
        var status = new StatusStrip();
        status.Items.AddRange([statusLabel, new ToolStripSeparator(), hoverLabel, progressBar]);

        // Context menu
        contextMenu.Items.Add("탐색기에서 열기", null, (_, _) => OpenInExplorer(contextNode));
        contextMenu.Items.Add("이 폴더로 확대", null, (_, _) => ZoomInto(contextNode));
        contextMenu.Items.Add("경로 복사", null, (_, _) =>
        {
            if (contextNode is not null) Clipboard.SetText(contextNode.FullPath);
        });
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("상위 폴더", null, (_, _) => GoUp());

        // Treemap
        map.Dock = DockStyle.Fill;
        map.HoverChanged += OnHoverChanged;
        map.NodeClicked += OnNodeClicked;
        map.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.XButton1) GoBack();
            else if (e.Button == MouseButtons.XButton2) GoForward();
        };

        // Fill control first so it docks last (after the bars).
        Controls.Add(map);
        Controls.Add(status);
        Controls.Add(toolbar);
        Controls.Add(menu);

        ApplyDpi(DeviceDpi);

        timer.Tick += (_, _) => UpdateProgress();
        Shown += (_, _) => StartScan(path);
        UpdateNavButtons();
        UpdateScanButtons();
    }

    // ---- scanning -------------------------------------------------------------------------

    private void StartScan(string path)
    {
        StopScan();
        scanner?.Dispose();

        scanner = new Scanner(path);
        backStack.Clear();
        forwardStack.Clear();
        map.ViewRoot = scanner.Root;
        scanner.Start();
        timer.Start();
        UpdateProgress();
        UpdateNavButtons();
        UpdateScanButtons();
        map.Focus();
    }

    private void StopScan()
    {
        if (scanner is { IsRunning: true })
        {
            scanner.Cancel();
            timer.Stop();
            UpdateProgress();
        }
    }

    private bool IsScanning => scanner is { IsRunning: true };

    /// <summary>Rescan / Empty Recycle Bin are disabled during a scan; the "새 스캔" button becomes "취소".</summary>
    private void UpdateScanButtons() => UpdateScanButtons(IsScanning);

    private void UpdateScanButtons(bool scanning)
    {
        bool hasScan = scanner is not null;

        rescanButton.Enabled = rescanMenuItem.Enabled = hasScan && !scanning;
        emptyBinButton.Enabled = emptyBinMenuItem.Enabled = !scanning;
        stopMenuItem.Enabled = scanning;

        if (scanning)
        {
            newButton.Text = "✖ 취소";
            newButton.ToolTipText = "스캔 취소 (Esc)";
        }
        else
        {
            newButton.Text = "📂 새 스캔";
            newButton.ToolTipText = "드라이브/폴더 선택 (Ctrl+N)";
        }
    }

    private void Rescan()
    {
        if (scanner is not null && !IsScanning) StartScan(scanner.RootPath);
    }

    private void EmptyRecycleBinAndRescan()
    {
        if (IsScanning) return;

        long size, count;
        try
        {
            (size, count) = NativeMethods.QueryRecycleBin();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"휴지통 정보를 읽을 수 없습니다.\n{ex.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (count == 0)
        {
            MessageBox.Show(this, "휴지통이 이미 비어 있습니다.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var answer = MessageBox.Show(this,
            $"모든 드라이브의 휴지통에 있는 항목 {count:N0}개({SizeFormat.Format(size)})를 영구적으로 삭제합니다.\n" +
            "이 작업은 되돌릴 수 없습니다. 계속하시겠습니까?",
            "휴지통 비우기", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        StopScan();
        UseWaitCursor = true;
        try
        {
            NativeMethods.EmptyRecycleBin(Handle);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"휴지통을 비우지 못했습니다.\n{ex.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
        }

        Rescan();
    }

    private void NewScan()
    {
        using var dlg = new DriveSelectForm();
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.SelectedPath is not null)
            StartScan(dlg.SelectedPath);
    }

    private void UpdateProgress()
    {
        if (scanner is null) return;

        // Snapshot the state first: if the scan finishes while we're reading counters below, the timer keeps
        // running and the next tick shows the final numbers (instead of stopping with stale ones).
        bool running = scanner.IsRunning;

        int percent = (int)(scanner.Progress * 100);
        Text = $"{AppName} - [{scanner.RootPath}] - full scan {percent}%";
        progressBar.Value = Math.Clamp(percent, 0, 100);

        string state = running ? "스캔 중..." : scanner.IsCompleted ? "스캔 완료" : "스캔 취소됨";
        statusLabel.Text =
            $"{state}  폴더 {scanner.DirectoriesScanned:N0}/{scanner.DirectoriesFound:N0} · 파일 {scanner.FilesFound:N0} · " +
            $"{SizeFormat.Format(scanner.Root.Size)} · 스레드 {scanner.ThreadCount} · {scanner.Elapsed:mm\\:ss}";

        int denied = scanner.AccessDeniedCount;
        accessDeniedButton.Visible = denied > 0;
        accessDeniedButton.Text = $"⚠ 오류: {denied:N0}개";

        map.RefreshMap();

        progressBar.Visible = running;
        if (!running) timer.Stop();
        UpdateScanButtons(running);
    }

    /// <summary>Writes the access-denied directories to a text file and opens it in Notepad.</summary>
    private void ShowAccessDeniedPaths()
    {
        if (scanner is null) return;
        string[] paths = scanner.GetAccessDeniedPaths();
        if (paths.Length == 0) return;
        Array.Sort(paths, StringComparer.OrdinalIgnoreCase);

        try
        {
            string file = Path.Combine(Path.GetTempPath(), $"DiskSpace_접근거부_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            var lines = new List<string>(paths.Length + 4)
            {
                $"스캔 경로: {scanner.RootPath}",
                $"보안(권한) 문제로 접근하지 못한 폴더: {paths.Length:N0}개" + (IsScanning ? " (스캔 진행 중 - 지금까지의 목록)" : ""),
                "",
            };
            lines.AddRange(paths);
            File.WriteAllLines(file, lines, System.Text.Encoding.UTF8);
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{file}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"목록을 열지 못했습니다.\n{ex.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---- navigation -----------------------------------------------------------------------

    private void Navigate(DirNode target)
    {
        if (map.ViewRoot is null || ReferenceEquals(target, map.ViewRoot)) return;
        backStack.Push(map.ViewRoot);
        forwardStack.Clear();
        map.ViewRoot = target;
        UpdateNavButtons();
    }

    private void GoBack()
    {
        if (backStack.Count == 0 || map.ViewRoot is null) return;
        forwardStack.Push(map.ViewRoot);
        map.ViewRoot = backStack.Pop();
        UpdateNavButtons();
    }

    private void GoForward()
    {
        if (forwardStack.Count == 0 || map.ViewRoot is null) return;
        backStack.Push(map.ViewRoot);
        map.ViewRoot = forwardStack.Pop();
        UpdateNavButtons();
    }

    private void GoUp()
    {
        if (map.ViewRoot?.Parent is DirNode parent) Navigate(parent);
    }

    private void GoHome()
    {
        if (scanner is not null) Navigate(scanner.Root);
    }

    private void ZoomInto(Node? node)
    {
        DirNode? dir = node as DirNode ?? node?.Parent;
        if (dir is not null) Navigate(dir);
    }

    private void UpdateNavButtons()
    {
        backButton.Enabled = backStack.Count > 0;
        forwardButton.Enabled = forwardStack.Count > 0;
        upButton.Enabled = map.ViewRoot?.Parent is not null;
        homeButton.Enabled = scanner is not null && !ReferenceEquals(map.ViewRoot, scanner.Root);
    }

    // ---- treemap events -------------------------------------------------------------------

    private void OnHoverChanged(Node? node)
    {
        if (node is null)
        {
            hoverLabel.Text = map.ViewRoot is null ? "" : $"{map.ViewRoot.FullPath} - {SizeFormat.Format(map.ViewRoot.Size)}";
            return;
        }

        string extra = node switch
        {
            DirNode { IsScanned: false } => "  (스캔 대기 중)",
            FileNode { IsAggregate: true } f => $"  ({f.AggregateCount:N0}개의 작은 파일)",
            _ => "",
        };
        double pct = map.ViewRoot is { Size: > 0 } v ? node.Size * 100.0 / v.Size : 0;
        hoverLabel.Text = $"{node.FullPath} - {SizeFormat.Format(node.Size)} ({pct:0.0}%){extra}";
    }

    private void OnNodeClicked(Node node, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            // Clicking a folder zooms into it; clicking the current root goes up.
            if (ReferenceEquals(node, map.ViewRoot)) GoUp();
            else if (node is DirNode dir) Navigate(dir);
            else if (node.Parent is DirNode parent && !ReferenceEquals(parent, map.ViewRoot)) Navigate(parent);
        }
        else if (e.Button == MouseButtons.Right)
        {
            contextNode = node;
            contextMenu.Show(map, e.Location);
        }
    }

    private static void OpenInExplorer(Node? node)
    {
        if (node is null) return;
        try
        {
            if (node is FileNode { IsAggregate: false })
                Process.Start("explorer.exe", $"/select,\"{node.FullPath}\"");
            else
                Process.Start("explorer.exe", $"\"{node.FullPath}\"");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---- DPI ------------------------------------------------------------------------------

    private int layoutDpi;

    /// <summary>Sets every pixel size that WinForms doesn't rescale by itself (fonts and docked bars it handles).</summary>
    private void ApplyDpi(int dpi)
    {
        layoutDpi = dpi;
        progressBar.Width = Dpi.Scale(140, dpi);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // The window may have been created on a monitor whose DPI differs from the one used in the constructor.
        if (DeviceDpi != layoutDpi) ApplyDpi(DeviceDpi);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e); // resizes the window to the suggested bounds and rescales fonts
        ApplyDpi(e.DeviceDpiNew);
    }

    // ---- misc -----------------------------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Back && ActiveControl == map)
        {
            GoBack();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Escape)
        {
            StopScan();
            e.Handled = true;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        timer.Stop();
        scanner?.Cancel();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            scanner?.Dispose();
            contextMenu.Dispose();
        }
        base.Dispose(disposing);
    }
}
