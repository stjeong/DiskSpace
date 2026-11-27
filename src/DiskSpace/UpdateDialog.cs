using System.Diagnostics;

namespace DiskSpace;

/// <summary>
/// "최신 버전 업데이트" flow in a native task dialog (check → confirm → download → install).
/// The task dialog is drawn by Windows, so it follows per-monitor DPI changes by itself.
/// </summary>
internal static class UpdateDialog
{
    private const string Caption = "최신 버전 업데이트";
    private const int MaxNotesLength = 3000;

    /// <summary>Runs the flow. Returns true when an update was started and the app should now exit.</summary>
    public static bool Run(IWin32Window owner, IReadOnlyList<string> restartArgs)
    {
        using var cts = new CancellationTokenSource();
        string? downloadedFile = null;

        var cancelButton = TaskDialogButton.Cancel;
        var checkPage = new TaskDialogPage
        {
            Caption = Caption,
            Heading = "업데이트 확인 중...",
            Text = $"GitHub({Updater.Repository})에서 최신 릴리스를 확인하고 있습니다.",
            ProgressBar = new TaskDialogProgressBar(TaskDialogProgressBarState.Marquee),
            Buttons = { cancelButton },
        };

        checkPage.Created += async (_, _) =>
        {
            ReleaseInfo release;
            try
            {
                release = await Updater.GetLatestReleaseAsync(cts.Token);
            }
            catch (Exception ex)
            {
                if (!cts.IsCancellationRequested)
                    checkPage.Navigate(ErrorPage("업데이트를 확인하지 못했습니다.", ex));
                return;
            }
            if (cts.IsCancellationRequested) return;

            if (release.Version <= Updater.CurrentVersion)
            {
                checkPage.Navigate(new TaskDialogPage
                {
                    Caption = Caption,
                    Icon = TaskDialogIcon.Information,
                    Heading = "최신 버전을 사용하고 있습니다.",
                    Text = $"현재 버전: {Updater.CurrentVersion}\nGitHub 최신 릴리스: {release.Version}",
                    Buttons = { TaskDialogButton.OK },
                });
                return;
            }

            checkPage.Navigate(AvailablePage(release, file => downloadedFile = file, cts.Token));
        };

        TaskDialog.ShowDialog(owner, checkPage, TaskDialogStartupLocation.CenterOwner);
        cts.Cancel(); // stops any request still running after the dialog was closed

        if (downloadedFile is null) return false;

        try
        {
            Updater.Install(downloadedFile, restartArgs);
            return true;
        }
        catch (Exception ex)
        {
            TaskDialog.ShowDialog(owner, ErrorPage("업데이트를 설치하지 못했습니다.", ex), TaskDialogStartupLocation.CenterOwner);
            return false;
        }
    }

    private static TaskDialogPage AvailablePage(ReleaseInfo release, Action<string> downloaded, CancellationToken ct)
    {
        bool canInstall = Updater.Kind != InstallKind.Development && release.AssetUrl is not null;

        string how = Updater.Kind switch
        {
            _ when !canInstall && Updater.Kind == InstallKind.Development =>
                "개발 빌드에서는 자동 업데이트를 지원하지 않습니다. 릴리스 페이지에서 받아 주세요.",
            _ when !canInstall =>
                "이 릴리스에는 현재 설치 방식에 맞는 파일이 없습니다. 릴리스 페이지에서 받아 주세요.",
            InstallKind.Msix =>
                $"{release.AssetName} ({SizeFormat.Format(release.AssetSize)})을 받아 앱 설치 관리자로 엽니다.\n" +
                "DiskSpace는 종료되며, 앱 설치 관리자에서 [업데이트]를 누르면 설치됩니다.",
            _ =>
                $"{release.AssetName} ({SizeFormat.Format(release.AssetSize)})을 받아 실행 파일을 교체하고 다시 시작합니다.\n" +
                "현재 스캔 결과는 사라지고, 같은 경로를 다시 스캔합니다.",
        };

        var updateButton = new TaskDialogButton("지금 업데이트") { AllowCloseDialog = false };
        var pageButton = new TaskDialogButton("릴리스 페이지 열기");
        pageButton.Click += (_, _) => OpenUrl(release.PageUrl);

        var page = new TaskDialogPage
        {
            Caption = Caption,
            Icon = TaskDialogIcon.Information,
            Heading = $"새 버전 {release.Version}이(가) 있습니다.",
            Text = $"현재 버전: {Updater.CurrentVersion}\n새 버전: {release.Version}\n\n{how}",
            DefaultButton = canInstall ? updateButton : pageButton,
        };
        if (canInstall) page.Buttons.Add(updateButton);
        page.Buttons.Add(pageButton);
        page.Buttons.Add(TaskDialogButton.Cancel);

        if (release.Notes.Length > 0)
        {
            string notes = release.Notes.Length > MaxNotesLength ? release.Notes[..MaxNotesLength] + "\n..." : release.Notes;
            page.Expander = new TaskDialogExpander(notes)
            {
                CollapsedButtonText = "릴리스 노트 보기",
                ExpandedButtonText = "릴리스 노트 숨기기",
                Position = TaskDialogExpanderPosition.AfterFootnote,
            };
        }

        updateButton.Click += (_, _) => page.Navigate(DownloadPage(release, downloaded, ct));
        return page;
    }

    private static TaskDialogPage DownloadPage(ReleaseInfo release, Action<string> downloaded, CancellationToken ct)
    {
        var cancelButton = TaskDialogButton.Cancel;
        var page = new TaskDialogPage
        {
            Caption = Caption,
            Heading = $"DiskSpace {release.Version} 다운로드 중...",
            Text = release.AssetName,
            ProgressBar = new TaskDialogProgressBar { Minimum = 0, Maximum = 100 },
            Buttons = { cancelButton },
        };

        page.Created += async (_, _) =>
        {
            var progress = new Progress<(long Done, long Total)>(p =>
            {
                if (page.BoundDialog is null) return;
                if (p.Total > 0) page.ProgressBar!.Value = (int)Math.Clamp(p.Done * 100 / p.Total, 0, 100);
                page.Text = $"{release.AssetName}\n{SizeFormat.Format(p.Done)} / {SizeFormat.Format(p.Total)}";
            });

            string file;
            try
            {
                file = await Updater.DownloadAsync(release, progress, ct);
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested && page.BoundDialog is not null)
                    page.Navigate(ErrorPage("다운로드하지 못했습니다.", ex, release.PageUrl));
                return;
            }

            if (ct.IsCancellationRequested || page.BoundDialog is null) return;
            downloaded(file);
            cancelButton.PerformClick(); // closes the dialog; Run() installs once it has returned
        };

        return page;
    }

    private static TaskDialogPage ErrorPage(string heading, Exception ex, string? pageUrl = null)
    {
        var pageButton = new TaskDialogButton("릴리스 페이지 열기");
        pageButton.Click += (_, _) => OpenUrl(pageUrl ?? Updater.ReleasesPage);

        string text = ex switch
        {
            UnauthorizedAccessException => $"{ex.Message}\n\n프로그램 폴더에 쓸 권한이 없습니다. 릴리스 페이지에서 직접 받아 주세요.",
            HttpRequestException => $"{ex.Message}\n\n인터넷 연결을 확인해 주세요.",
            TaskCanceledException => "서버 응답 시간이 초과되었습니다.",
            _ => ex.Message,
        };

        return new TaskDialogPage
        {
            Caption = Caption,
            Icon = TaskDialogIcon.Error,
            Heading = heading,
            Text = text,
            Buttons = { pageButton, TaskDialogButton.Close },
        };
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // No browser registered: nothing useful to do.
        }
    }
}
