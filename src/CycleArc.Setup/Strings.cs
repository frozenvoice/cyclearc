namespace CycleArc.Setup;

/// <summary>
/// The installer's text, in the two languages the application itself ships. It follows the
/// system UI language and carries no resource files, so the wrapper stays a single binary.
/// </summary>
internal static class Strings
{
    private static readonly bool Korean =
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("ko", StringComparison.OrdinalIgnoreCase);

    private static string T(string english, string korean) => Korean ? korean : english;

    public static string WindowTitle => T("CycleArc Setup", "CycleArc 설치");

    public static string ConfirmHeadingInstall => T("Install CycleArc", "CycleArc 설치");
    public static string ConfirmHeadingUpdate => T("Update CycleArc", "CycleArc 업데이트");

    public static string ConfirmBodyInstall => T(
        "CycleArc will be installed for your Windows account only. No administrator rights are needed.",
        "CycleArc를 현재 Windows 계정에만 설치합니다. 관리자 권한은 필요하지 않습니다.");

    public static string ConfirmBodyUpdate => T(
        "An existing CycleArc installation was found and will be updated in place. Your accounts, settings and usage history are kept.",
        "기존 CycleArc 설치를 찾았습니다. 같은 위치에 업데이트하며 계정·설정·사용량 기록은 그대로 유지됩니다.");

    public static string LocationLabel => T("Install location", "설치 위치");

    public static string InstallButton => T("Install", "설치");
    public static string CancelButton => T("Cancel", "취소");
    public static string FinishButton => T("Finish", "마침");
    public static string CloseButton => T("Close", "닫기");

    public static string ProgressHeading => T("Installing CycleArc", "CycleArc 설치 중");
    public static string ProgressBody => T(
        "This takes a moment. Please leave this window open.",
        "잠시 걸립니다. 이 창을 닫지 말고 기다려 주세요.");
    public static string ProgressClosing => T(
        "Closing the running CycleArc...", "실행 중인 CycleArc를 종료하는 중...");
    public static string ProgressCopying => T("Installing files...", "파일을 설치하는 중...");

    public static string DoneHeading => T("CycleArc is installed", "CycleArc 설치 완료");
    public static string DoneBody => T(
        "You can start CycleArc now, or from the Start menu later.",
        "지금 CycleArc를 실행하거나, 나중에 시작 메뉴에서 열 수 있습니다.");
    public static string RunCheckbox => T("Run CycleArc", "CycleArc 실행");

    public static string FailedHeading => T("Installation failed", "설치하지 못했습니다");
    public static string FailedBodyPrefix => T(
        "CycleArc was not installed.", "CycleArc를 설치하지 못했습니다.");
    public static string LogLabel => T("Log", "로그");

    public static string NoEngineHeading => T("This installer is incomplete", "설치 파일이 불완전합니다");
    public static string NoEngineBody => T(
        "This build of CycleArc-Setup.exe does not contain the application and cannot install it. Download the installer again from the CycleArc releases page.",
        "이 CycleArc-Setup.exe에는 프로그램이 들어 있지 않아 설치할 수 없습니다. CycleArc 릴리스 페이지에서 설치 파일을 다시 받아 주세요.");

    public static string LaunchFailedPrefix => T(
        "CycleArc is installed, but it could not be started:",
        "CycleArc는 설치했지만 실행하지 못했습니다:");
}
