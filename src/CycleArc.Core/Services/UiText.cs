namespace CycleArc.Services;

public static class UiText
{
    public static UiLanguage Language { get; private set; } = UiLanguage.English;

    public static event Action? Changed;

    public static bool IsKorean => Language == UiLanguage.Korean;

    public static void SetLanguage(UiLanguage language)
    {
        if (Language == language)
        {
            return;
        }

        Language = language;
        Changed?.Invoke();
    }

    public static string T(string english, string korean) => IsKorean ? korean : english;

    public static string ProductName => "CycleArc";
    public static string CodexProviderName => "Codex";
    public static string AboutTitle => T($"About {ProductName}", $"{ProductName} 정보");
    public static string WidgetTitle => T($"{ProductName} widget", $"{ProductName} 위젯");
    public static string GptPro => "GPT Pro";
    public static string Gpt6Pro => "GPT-6 Pro";
    public static string SolPro => "Sol Pro";

    public static string SyncNow => T("Sync now", "지금 동기화");
    public static string Syncing => T("Syncing", "동기화 중");
    public static string SyncingEllipsis => T("Syncing...", "동기화 중...");
    public static string LastSync => T("Last sync", "마지막 동기화");
    public static string Never => T("Never", "없음");
    public static string Settings => T("Settings", "설정");
    public static string OpenLogs => T("Open logs", "로그 폴더 열기");
    public static string StartWithWindows => T("Start with Windows", "Windows 시작 시 실행");
    public static string AutomaticSync => T("Automatic history synchronization", "자동 동기화");
    public static string Coverage => T("Coverage", "데이터 상태");
    public static string DataStatus => T("Data status", "데이터 상태");
    public static string Remaining => T("Remaining", "남은 횟수");
    public static string ExactRemaining => T("Exact remaining", "정확한 잔여 횟수");
    public static string RemainingCount => T("Remaining", "잔여 횟수");
    public static string ExactRemainingUnavailable => T("Unavailable", "확인 불가");
    public static string ConfirmedRequests => T("Confirmed requests", "확인된 요청");
    public static string ConfirmedProRequests => T("Confirmed Pro requests", "확인된 Pro 요청");
    public static string ConfirmedProUsage => T("Confirmed this cycle", "이번 주기 확인 사용");
    public static string CycleStart => T("Cycle start", "주기 시작");
    public static string NextReset => T("Next reset", "다음 리셋");
    public static string EstimatedStamp(string stamp) => T($"{stamp} · estimated", $"{stamp} · 추정");
    public static string HistoryBasedLowerBound => T("History-based minimum", "기록 기반 최소치");
    public static string ReconstructedObservedCaption => T(
        "Reconstructed observed requests · estimated",
        "기록 집계 · 추정");
    public static string ReconstructedCount(int count) => T($"{count} · estimated", $"{count}회 · 추정");
    public static string CurrentCycleReconstructed => T("This cycle reconstructed", "이번 주기 기록 집계");
    public static string EstimatedPeriodReconstructed => T("Last 7 days reconstructed", "최근 7일 기록 집계");
    public static string UnresolvedPending => T("Unresolved", "확인 보류");
    public static string MigrationBackupFailed => T(
        "CycleArc could not create a verified backup of the usage database, so it stopped before migrating it. Your data was not changed.",
        "사용량 데이터베이스의 검증된 백업을 만들 수 없어 이전을 중단했습니다. 기존 데이터는 변경되지 않았습니다.");
    public static string UnresolvedPendingCount(int count) => T($"{count} items", $"{count}건");
    public static string ObservedRequestsNotBilled => T(
        "Observed requests, not proven server-billed quota.",
        "관측된 요청이며 서버 차감 횟수는 아닙니다.");
    public static string HistoryStatistics => T("History statistics", "기록 통계");
    public static string ProRestricted => T("Restricted", "제한됨");
    public static string ProNoServerRestriction => T("No server restriction observed", "서버 제한 없음");
    public static string ProRestrictionMatchesReset => T(
        "Server restriction matches the Pro model reset window",
        "서버 제한과 Pro 모델 리셋 시각이 일치함");
    public static string ServerReset => T("Server reset", "서버 리셋");
    public static string ServerReason => T("Server reason", "서버 사유");
    public static string MultipleProResets => T("Multiple model resets", "모델별 리셋이 다름");
    public static string Stale => T("Stale", "오래됨");
    public static string ReconstructedHistory => T("Reconstructed from history", "기록 재구성");
    public static string Reset => T("Reset", "리셋");
    public static string ResetTime => T("Reset time", "리셋 시각");
    public static string Estimate => T("Estimate", "추정 기준");
    public static string EstimatedNextReset => T("Estimated next reset", "예상 다음 리셋");
    public static string EstimatedPeriodFooter => T(
        "Observed requests from the last 7 days. The quota reset is not confirmed; this is not an official remaining quota.",
        "최근 7일 대화 기록에서 재구성한 통계입니다. 실제 리셋 시각과 공식 잔여 한도는 확인되지 않았습니다.");
    public static string NotConfirmed => T("Not confirmed", "확인되지 않음");
    public static string NotAvailable => T("Not available", "확인되지 않음");
    public static string Limit => T("Limit", "한도");
    public static string LimitInfo => T("Limit", "한도 정보");
    public static string Today => T("Today", "오늘");
    public static string ThisWeek => T("This week", "이번 주");
    public static string Medium => T("Medium", "보통");
    public static string High => T("High", "높음");
    public static string ExtraHigh => T("Extra High", "매우 높음");
    public static string Complete => T("Complete", "완료");
    public static string Partial => T("Partial", "일부 누락");
    public static string Estimated => T("Estimated", "추정");
    public static string Unavailable => T("Unavailable", "확인 불가");
    public static string Failed => T("Failed", "실패");
    public static string PreviousData => T("Previous data", "이전 데이터");
    public static string Close => T("Close", "닫기");
    public static string Pin => T("Pin", "고정");
    public static string Unpin => T("Unpin", "고정 해제");
    public static string Save => T("Save", "저장");
    public static string About => T("About", "정보");
    public static string Exit => T("Exit", "종료");
    public static string OpenApp => T($"Open {ProductName}", $"{ProductName} 열기");
    public static string OpenLogin => T("Open Login", "로그인 열기");
    public static string ViewStatistics => T("View statistics", "통계 보기");
    public static string Finish => T("Finish", "마침");
    public static string LanguageCaption => T("Language / 언어", "언어 / Language");
    public static string Korean => "한국어";
    public static string English => "English";

    public static string Idle => T("Idle", "대기");
    public static string SignedOut => T("Signed out", "로그아웃됨");
    public static string AuthenticationRequired => T("Authentication required", "인증 필요");
    public static string DetectingAccount => T("Detecting account...", "계정 확인 중...");
    public static string LoadingCatalog => T("Loading model catalog...", "모델 목록 불러오는 중...");
    public static string UpToDate => T("Up to date", "최신 상태");
    public static string Updated => T("Updated", "업데이트됨");
    public static string DataUsable => T("Usable", "사용 가능");
    public static string ConnectionRequired => T("Connection required", "연결 필요");
    public static string SignInRequired => T("Sign-in required", "로그인 필요");
    public static string SyncFailedShort => T("Sync failed", "동기화 실패");
    public static string DataStale => T("Stale data", "오래된 데이터");
    public static string NeedsAttention => T("Needs attention", "확인 필요");
    public static string ChatGptServerStatus => T("ChatGPT server status", "ChatGPT 서버 상태");
    public static string CodexStatusLabel => T("Codex status", "Codex 상태");
    public static string HistoryBasedStats => T("History-based statistics", "기록 기반 통계");
    public static string Confirmed => T("Confirmed", "확인됨");
    public static string HistoryNotOfficialNote => T(
        "Some history-based statistics are not official OpenAI usage totals.",
        "일부 기록 기반 통계는 OpenAI 공식 사용량 집계가 아닙니다.");
    public static string AdvancedDiagnostics => T("Advanced diagnostics", "고급 진단");
    public static string NoDiagnosticIssues => T("No issues to report.", "특이사항 없음");
    public static string HistoryConfirmedMinPro(string count) => T(
        $"Minimum Pro requests confirmed from history: {count}",
        $"기록에서 확인된 최소 Pro 요청: {count}");
    public static string RateLimited => T("Rate limited", "요청이 제한됨");
    public static string ApiChanged => T("API changed", "API가 변경됨");
    public static string ProviderSchemaMismatch => T("Provider schema mismatch", "응답 형식이 맞지 않음");
    public static string PartialData => T("Partial data", "일부 데이터 누락");
    public static string Offline => T("Offline", "오프라인");
    public static string Error => T("Error", "오류");
    public static string ScanningPeriod => T("Scanning current quota period...", "현재 한도 기간을 읽는 중...");
    public static string ScanningConversations => T("Scanning conversations...", "대화를 읽는 중...");

    public static string ChatGptSignedOut => T("ChatGPT tab is signed out.", "ChatGPT 탭이 로그아웃되어 있습니다.");
    public static string ChatGptSessionExpired => T("ChatGPT session expired.", "ChatGPT 세션이 만료되었습니다.");
    public static string ChatGptUnreachable => T("ChatGPT is unreachable.", "ChatGPT에 연결할 수 없습니다.");
    public static string RateLimitedPaused => T("Rate limited. Automatic sync paused.", "요청이 제한되어 자동 동기화를 잠시 멈췄습니다.");
    public static string AutoSyncPaused => T("Automatic sync paused after repeated failures.", "오류가 반복되어 자동 동기화를 잠시 멈췄습니다.");
    public static string SchemaMismatchStatus => T("Provider schema mismatch", "응답 형식이 맞지 않음");

    public static string CompanionConnected => T("Connected", "연결됨");
    public static string CompanionDisconnected => T("Disconnected", "연결 끊김");
    public static string CompanionDisconnectedStatus => T(
        "Browser Companion disconnected.",
        "Browser Companion 연결이 끊어졌습니다.");
    public static string BridgeTimeoutStatus => T(
        "Browser Companion timed out.",
        "Browser Companion 응답 시간이 초과되었습니다.");
    public static string BridgeWriteFailedStatus => T(
        "Could not send the request to Browser Companion.",
        "Browser Companion으로 요청을 보내지 못했습니다.");
    public static string CompanionRegisteredWaiting => T("Registered. Waiting for extension", "등록됨. 확장 프로그램 연결을 기다리는 중");
    public static string CompanionWaiting => T("Waiting for extension", "확장 프로그램 연결을 기다리는 중");
    public static string CompanionNotInstalled => T("Not installed", "설치되지 않음");
    public static string Forbidden403 => T(
        CompanionDiagnostics.Forbidden403,
        "ChatGPT가 페이지 요청을 거부했습니다 (403)");
    public static string NoChatGptTab => T(
        CompanionDiagnostics.NoChatGptTab,
        "ChatGPT를 열거나 로그인한 다음 다시 시도하세요");
    public static string PageBridgeUnavailable => T(
        CompanionDiagnostics.PageBridgeUnavailable,
        "ChatGPT 페이지 브리지를 사용할 수 없습니다");

    public static string IncompleteReconstruction => T("Incomplete reconstruction", "대화 기록 재구성이 불완전합니다");
    public static string ReconstructedHigh => T("Reconstructed · high confidence", "대화 기록 기반 · 높은 신뢰도");
    public static string ReconstructedEstimated => T("Reconstructed · estimated", "대화 기록 기반 · 추정");
    public static string ServerCount(int reconstructed) => T(
        $"Server count · reconstructed {reconstructed}",
        $"서버 집계 · 재구성 {reconstructed}");

    public static string CountBasisServer => T("Server", "서버");
    public static string CountBasisReconstructed => T("Reconstructed", "대화 기록 기반");
    public static string CountConfidenceAuthoritative => T("Authoritative", "공식");
    public static string CountConfidenceHigh => T("High", "높음");
    public static string CountConfidenceEstimated => T("Estimated", "추정");
    public static string CountConfidenceIncomplete => T("Incomplete", "불완전");
    public static string ResetBasisServer => T("Server", "서버");
    public static string ResetBasisUser => T("User configured", "사용자 설정");
    public static string ResetBasisEstimated => T("Estimated", "추정");
    public static string BranchIncluded => T("Included", "포함됨");
    public static string BranchUnknown => T("Unknown", "확인되지 않음");
    public static string CannotReconstruct => T("Cannot reconstruct", "재구성할 수 없음");
    public static string NormalChats => T("Normal chats", "일반 대화");
    public static string ArchivedChats => T("Archived chats", "보관된 대화");
    public static string Projects => T("Projects", "프로젝트");
    public static string ConversationBodies => T("Conversation bodies", "대화 본문");
    public static string TemporaryChats => T("Temporary chats", "임시 대화");
    public static string DeletedChats => T("Deleted chats", "삭제된 대화");
    public static string CountBasis => T("Count basis", "횟수 기준");
    public static string CountConfidence => T("Count confidence", "횟수 신뢰도");
    public static string ResetBasis => T("Reset basis", "리셋 기준");
    public static string BranchCoverage => T("Branch coverage", "분기 포함");
    public static string Successful => T("successful", "성공");
    public static string UniqueFailed => T("unique failed", "고유 실패");
    public static string ConversationsNotRead(int count) => T(
        $"{count} conversations not read",
        $"대화 {count}개 읽기 실패");
    public static string ConversationsNotApplied(int count) => T(
        $"{count} conversations not applied",
        $"대화 {count}개 미반영");
    public static string PartialUnapplied => T("Partially not applied", "일부 미반영");
    public static string DataPartiallyNotApplied => T("Data partially not applied", "데이터 일부 미반영");
    public static string ReadTimeout => T("Read timeout", "읽기 시간 초과");
    public static string ResponseFormatMismatch => T("Response format mismatch", "응답 형식 불일치");
    public static string RepeatedConversationSchemaMismatch => T(
        "Response format mismatch repeated across multiple conversations.",
        "여러 대화에서 응답 형식 불일치가 반복됨");
    public static string SchemaMismatchDetail(string reason) => reason switch
    {
        "conversation collection too large" => T(
            "conversation collection too large",
            "대화 노드 수가 안전 검증 한도를 초과함"),
        "mapping too large" => T(
            "mapping too large",
            "mapping 노드 수가 안전 검증 한도를 초과함"),
        "children rejected" or "children must be an array" => T(
            "children rejected",
            "children 필드가 안전 검증을 통과하지 못함"),
        "metadata field rejected" or "metadata value rejected" => T(
            "metadata field rejected",
            "metadata 필드가 안전 검증을 통과하지 못함"),
        "timestamp rejected" => T(
            "timestamp rejected",
            "타임스탬프가 안전 검증을 통과하지 못함"),
        "unknown projected field" => T(
            "unknown projected field",
            "알 수 없는 투영 필드"),
        "too many fields" => T(
            "too many fields",
            "필드 수가 안전 검증 한도를 초과함"),
        "mapping must be an object" => T(
            "mapping must be an object",
            "mapping 형식이 안전 검증을 통과하지 못함"),
        _ => reason
    };
    public static string ResponseTooLarge => T("Response too large", "응답 크기 초과");
    public static string ConversationCompanionFailure => T("Companion disconnected", "도우미 연결 끊김");
    public static string FailureCategoryOther => T("Other", "기타");
    public static string FailedThisSync => T("Failed this sync", "이번 동기화 실패");
    public static string WaitingToRetry => T("Waiting to retry", "재시도 대기");
    public static string CoverageLowerBoundNote => T(
        "This statistic is a lower bound from conversations that could be read.",
        "이 통계는 읽을 수 있었던 대화만 반영한 최소값입니다.");
    public static string CoverageAutoRetryNote => T(
        "Failed conversations will be retried automatically later.",
        "실패한 대화는 이후 자동으로 다시 시도합니다.");
    public static string CoverageNoUserActionNote => T(
        "You do not need to take any action.",
        "사용자가 별도로 조치할 필요는 없습니다.");
    public static string CoverageDisclaimer => T(
        "History reconstruction is not an official OpenAI quota counter.",
        "대화 기록 재구성은 OpenAI의 공식 한도 집계가 아닙니다.");
    public static string TemporaryDeletedNote => T(
        "Temporary and deleted chats cannot be reconstructed from account history.",
        "임시 대화와 삭제된 대화는 계정 기록으로 재구성할 수 없습니다.");
    public static string HistoryLoadedWithoutUsage => T(
        SyncEngine.MissingAssistantUsageDiagnostic,
        "대화 기록은 불러왔지만 어시스턴트 사용량 메타데이터를 재구성하지 못했습니다.");
    public static string ReasoningReconstructedNote => T(
        "Sol reasoning counts are reconstructed statistics, not an official remaining quota.",
        "Sol 추론 횟수는 대화 기록에서 재구성한 통계이며, 공식 잔여 한도가 아닙니다.");
    public static string HistoryBasedEstimateBadge => T("History-based estimate", "기록 기반 추정");
    public static string PartialRevalidationNotice => T(
        "Some history is being revalidated.",
        "일부 기록 재검증 중");
    public static string CodexLegendUsed => T("Used", "사용");
    public static string CodexLegendRemaining => T("Remaining", "남음");

    public static string ResetServer(string stamp) => T($"{stamp} (server)", $"{stamp} (서버)");
    public static string ResetUserConfigured(string stamp) => T($"{stamp} (user configured)", $"{stamp} (사용자 설정)");

    public static string Weekday(DayOfWeek day) => day switch
    {
        DayOfWeek.Sunday => T("Sunday", "일요일"),
        DayOfWeek.Monday => T("Monday", "월요일"),
        DayOfWeek.Tuesday => T("Tuesday", "화요일"),
        DayOfWeek.Wednesday => T("Wednesday", "수요일"),
        DayOfWeek.Thursday => T("Thursday", "목요일"),
        DayOfWeek.Friday => T("Friday", "금요일"),
        _ => T("Saturday", "토요일")
    };

    public static string ThemeSystem => T("System", "시스템");
    public static string ThemeLight => T("Light", "밝게");
    public static string ThemeDark => T("Dark", "어둡게");
    public static string TrayRemainingNumber => T("Remaining number", "남은 횟수 숫자");
    public static string TrayProgressRing => T("Progress ring", "진행 링");
    public static string TransportCompanion => T("Browser companion", "브라우저 도우미");
    public static string TransportWebView => T("WebView2 fallback", "WebView2 대체 경로");
    public static string TransportExport => T("Data Export only", "데이터 내보내기만");
    public static string FloatingWidget => T("Floating widget", "플로팅 위젯");
    public static string TaskbarStatusEnabled => T("Always show taskbar status", "작업표시줄 상시 표시");
    public static string RefreshAll => T("Refresh all", "모두 새로고침");
    public static string RefreshAllProgress => T("Refreshing...", "동기화 중...");
    public static string CodexUsage => T("Codex usage", "Codex 사용량");
    public static string FiveHourUsed => T("5-hour used", "5시간 사용량");
    public static string FiveHourRemaining => T("5-hour remaining", "5시간 남음");
    public static string WeeklyUsed => T("Weekly used", "주간 사용량");
    public static string WeeklyRemaining => T("Weekly remaining", "주간 남음");
    public static string LastChecked => T("Last checked", "마지막 확인");
    public static string ResetCredits => T("Reset credits", "리셋권");
    public static string CodexNotFound => T("Codex not found", "Codex를 찾을 수 없음");
    public static string CodexSignIn => T("Sign in to Codex", "Codex에 로그인하세요");
    public static string CodexDataStale => T("Last data shown · stale", "마지막 데이터 표시 · 오래됨");
    public static string CodexRecentRefreshError => T("Recent refresh error", "최근 새로고침 오류");
    public static string CodexProtocolChanged => T("Protocol changed", "프로토콜이 변경됨");
    public static string CodexTimedOut => T("Request timed out", "요청 시간이 초과됨");
    public static string CodexRefreshing => T("Refreshing...", "새로고침 중...");
    public static string CodexUnavailable => T("Data unavailable", "데이터를 사용할 수 없음");
    public static string CodexCancelled => T("Cancelled", "취소됨");
    public static string CodexExecutable => T("Codex executable", "Codex 실행 파일");
    public static string CodexExePath => T("Codex executable path (optional)", "Codex 실행 파일 경로 (선택)");
    public static string CodexExePathHint => T(
        "Leave blank to auto-discover Codex. Use an absolute .exe, .cmd, or .bat path only when discovery cannot find it.",
        "비워 두면 Codex를 자동으로 찾습니다. 자동 검색이 실패할 때만 절대 경로의 .exe, .cmd, .bat를 지정하세요.");
    public static string Found => T("Found", "찾음");
    public static string NotFound => T("Not found", "없음");
    public static string CodexSignInState => T("Codex sign-in", "Codex 로그인");
    public static string CodexFreshness => T("Codex freshness", "Codex 최신 여부");
    public static string CodexWindows => T("Codex windows", "Codex 기간");
    public static string CodexFailureCategory => T("Codex status detail", "Codex 상태 세부 정보");
    public static string Plan => T("PLAN", "요금제");
    public static string ResetAnchor => T("RECONSTRUCTION WINDOW", "기록 재구성 기준");
    public static string TaskbarStatusHint => T(
        "Shows compact Pro/Codex status beside the Windows notification area.",
        "Windows 알림 영역 옆에 간단한 Pro/Codex 상태를 표시합니다.");
    public static string FloatingWidgetHint => T(
        "Shows server Pro status, reset time, Codex usage and reconstructed history.",
        "서버 Pro 상태, 리셋 시각, Codex 사용량, 재구성한 기록을 표시합니다.");
    public static string ResetNotProvided => T("Reset not provided", "리셋 미제공");
    public static string WidgetReset => T("Reset", "리셋");
    public static string WidgetLeft(string percent) => T($"Left {percent}", $"남음 {percent}");
    public static string WidgetAccountsConnected(int count) => count == 1
        ? T("1 account connected", "계정 1개 연결됨")
        : T($"{count} accounts connected", $"계정 {count}개 연결됨");
    public static string WidgetHideHint => T("Hide the widget (CycleArc keeps running)", "위젯 숨기기 (CycleArc는 계속 실행됨)");
    public static string WidgetOpenDetailHint => T("Open this account's details", "이 계정의 상세 정보 열기");
    public static string WidgetOpacity => T("Widget opacity", "위젯 투명도");
    public static string WidgetAlwaysOnTop => T("Always on top", "항상 위");
    public static string WidgetClickThrough => T("Click through", "클릭 통과");
    public static string WidgetClickThroughHint => T(
        "When click-through is on, the widget ignores mouse input until you turn it off.",
        "클릭 통과를 켜면 끌 때까지 위젯에서 마우스 입력을 받지 않습니다.");
    public static string ToastProRestriction => T("A Pro restriction was detected.", "Pro 제한이 감지되었습니다.");
    public static string ToastProRestrictionCleared => T("The Pro server restriction was cleared.", "Pro 서버 제한이 해제되었습니다.");
    public static string Connection => T("CONNECTION", "연결");
    public static string AppSection => T("APP", "앱");
    public static string Notifications => T("NOTIFICATIONS", "알림");
    public static string Data => T("DATA", "데이터");
    public static string Overview => T("Overview", "개요");
    public static string Trends => T("Trends", "추이");
    public static string Breakdown => T("Breakdown", "상세");
    public static string Status => T("STATUS", "상태");
    public static string SolReasoning => "SOL REASONING";
    public static string GptProSection => "GPT PRO";

    public static string WeeklyProQuota => T("Weekly Pro quota", "주간 Pro 한도");
    public static string DailyProQuota => T("Daily Pro quota", "일일 Pro 한도");
    public static string SolProDailyQuota => T("Sol Pro daily quota", "Sol Pro 일일 한도");
    public static string CombinedDailyQuota => T("Combined daily quota", "합산 일일 한도");
    public static string ReasoningQuota => T("Reasoning quota", "추론 한도");
    public static string SyncInterval => T("Sync interval (minutes)", "동기화 간격(분)");
    public static string Notify20 => T("20% remaining", "20% 남음");
    public static string Notify10 => T("10% remaining", "10% 남음");
    public static string NotifyExhausted => T("Quota exhausted", "한도 소진");
    public static string NotifyReset => T("Quota reset", "한도 리셋");
    public static string NotifySyncError => T("Sync / authentication error", "동기화 / 인증 오류");
    public static string ImportOlder => T("Import older than the current quota period", "현재 한도 기간보다 이전 기록도 가져오기");
    public static string ImportConversations => T("Import conversations.json", "conversations.json 가져오기");
    public static string ExportJson => T("Export JSON", "JSON 내보내기");
    public static string ExportCsv => T("Export CSV", "CSV 내보내기");
    public static string RegisterNativeHost => T("Register Chrome/Edge native host", "Chrome/Edge 네이티브 호스트 등록");
    public static string CheckingChatGptSession => T("Checking ChatGPT session...", "ChatGPT 세션을 확인하는 중...");
    public static string WebViewInitializationTimedOut => T(
        "WebView2 initialization timed out.",
        "WebView2 초기화 시간이 초과되었습니다.");
    public static string WebViewInitializationFailed => T(
        "WebView2 initialization failed.",
        "WebView2 초기화에 실패했습니다.");
    public static string WebViewNavigationTimedOut => T(
        "WebView2 navigation timed out.",
        "WebView2 탐색 시간이 초과되었습니다.");
    public static string WebViewNavigationFailed => T(
        "WebView2 navigation failed.",
        "WebView2 탐색에 실패했습니다.");
    public static string WebViewRequestTimedOut => T(
        "WebView2 request timed out.",
        "WebView2 요청 시간이 초과되었습니다.");
    public static string WebViewRequestFailed => T(
        "WebView2 request failed.",
        "WebView2 요청에 실패했습니다.");
    public static string ResetAnchorHint => T(
        "Used only for history statistics when a server reset time is unavailable. This is not the actual Pro quota reset.",
        "서버 리셋을 확인할 수 없을 때 통계 구간에만 사용합니다. 실제 Pro 한도 리셋 시각을 의미하지 않습니다.");
    public static string ResetAnchorCheck => T(
        "Use this reconstruction window when the server reset time is unavailable",
        "서버 리셋 시각을 모를 때 이 통계 구간을 사용");
    public static string ConnectionHint => T(
        "Browser companion uses your normal Chrome or Edge ChatGPT session and avoids embedded social OAuth. It does not make unofficial ChatGPT endpoints official. WebView2 is a fallback only when that sign-in method works. Google/Microsoft/Apple login inside WebView2 is unsupported. Data Export remains the lower-risk non-real-time fallback.",
        "브라우저 도우미는 Chrome 또는 Edge의 일반 ChatGPT 세션을 사용하며, 내장 소셜 로그인을 피합니다. 비공식 ChatGPT 엔드포인트가 공식 API가 되는 것은 아닙니다. WebView2는 그 방식으로 로그인이 될 때만 대체 경로입니다. WebView2 안의 Google/Microsoft/Apple 로그인은 지원하지 않습니다. 데이터 내보내기는 실시간은 아니지만 위험이 더 낮은 대체 수단입니다.");
    public static string ChromeExtensionId => T("Chrome unpacked extension ID (32 letters a-p)", "Chrome 압축 해제 확장 ID (a-p 32글자)");
    public static string EdgeExtensionId => T("Edge unpacked extension ID if different", "다를 경우 Edge 압축 해제 확장 ID");
    public static string PairingTokenHint => T(
        "Local pairing token (not a ChatGPT secret). The native host attaches it automatically.",
        "로컬 페어링 토큰입니다. ChatGPT 비밀값이 아니며, 네이티브 호스트가 자동으로 붙입니다.");
    public static string AppRiskHint => T(
        "CycleArc uses unofficial ChatGPT web endpoints. They are not part of the public OpenAI API and may change. Programmatic history access is unsupported and may carry account or terms risk. Review current ChatGPT terms before enabling synchronization. Official ChatGPT Data Export import is the lower-risk fallback but is not real-time.",
        "CycleArc는 비공식 ChatGPT 웹 엔드포인트를 사용합니다. 공개 OpenAI API가 아니며 예고 없이 바뀔 수 있습니다. 프로그램으로 대화 기록에 접근하는 방식은 지원되지 않으며 계정 또는 이용약관 위험이 있을 수 있습니다. 동기화를 켜기 전에 현재 ChatGPT 약관을 확인하세요. 공식 ChatGPT 데이터 내보내기 가져오기는 위험이 더 낮지만 실시간이 아닙니다.");

    public static string WelcomeTitle => T("Welcome to CycleArc", "CycleArc에 오신 것을 환영합니다");
    public static string WelcomeSubtitle => T(
        "Windows tray monitor that reconstructs ChatGPT Pro usage from account conversation history.",
        "계정 대화 기록으로 ChatGPT Pro 사용량을 재구성하는 Windows 트레이 모니터입니다.");
    public static string WelcomeStep1 => T("1. Review before enabling sync", "1. 동기화를 켜기 전에 확인");
    public static string WelcomeStep1Body => T(
        "CycleArc uses unofficial ChatGPT web endpoints. These are not part of the public OpenAI API and may change without notice. Programmatic history access is unsupported and may conflict with applicable ChatGPT terms. Review current terms before enabling synchronization. Official ChatGPT Data Export import is the lower-risk fallback but is not real-time. Only a matching server quota counter is authoritative; reconstructed history counts are estimates. The browser companion avoids embedded OAuth; it does not make this integration official.",
        "CycleArc는 비공식 ChatGPT 웹 엔드포인트를 사용합니다. 공개 OpenAI API가 아니며 예고 없이 바뀔 수 있습니다. 프로그램으로 대화 기록에 접근하는 방식은 지원되지 않으며 적용되는 ChatGPT 약관과 충돌할 수 있습니다. 동기화를 켜기 전에 현재 약관을 확인하세요. 공식 ChatGPT 데이터 내보내기 가져오기는 위험이 더 낮지만 실시간이 아닙니다. 서버 한도 집계와 일치할 때만 공식으로 볼 수 있으며, 대화 기록으로 재구성한 횟수는 추정입니다. 브라우저 도우미는 내장 OAuth를 피하지만, 이 연동이 공식 기능이 되는 것은 아닙니다.");
    public static string WelcomeStep2 => T("2. Choose how to connect", "2. 연결 방식 선택");
    public static string WelcomeTransportCompanion => T(
        "Browser companion (recommended for Chrome/Edge social login)",
        "브라우저 도우미 (Chrome/Edge 소셜 로그인에 권장)");
    public static string WelcomeTransportWebView => T(
        "WebView2 fallback (only if sign-in works there)",
        "WebView2 대체 경로 (그 방식으로 로그인이 될 때만)");
    public static string WelcomeTransportExport => T(
        "Data Export import only (not real-time)",
        "데이터 내보내기 가져오기만 (실시간 아님)");
    public static string WelcomeSocialHint => T(
        "Google, Microsoft, and Apple sign-in are not supported inside WebView2. Use the browser companion with your normal browser session. CycleArc never spoofs a user agent.",
        "Google, Microsoft, Apple 로그인은 WebView2 안에서 지원되지 않습니다. 일반 브라우저 세션과 브라우저 도우미를 사용하세요. CycleArc는 사용자 에이전트를 위장하지 않습니다.");
    public static string WelcomeCompanionHint => T(
        "Load the unpacked extension, enter its ID, register the native host, then Connect in the popup. Finish can be used after registration without running a sync.",
        "압축 해제한 확장 프로그램을 로드하고 ID를 입력한 뒤 네이티브 호스트를 등록한 다음, 팝업에서 연결하세요. 등록 후에는 동기화 없이 마침을 사용할 수 있습니다.");
    public static string OpenExtensionFolder => T("Open extension folder", "확장 프로그램 폴더 열기");
    public static string WelcomeChromeId => T("Chrome extension ID (32 letters a-p)", "Chrome 확장 ID (a-p 32글자)");
    public static string WelcomeEdgeId => T("Edge extension ID if different", "다를 경우 Edge 확장 ID");
    public static string RegisterSelectedHost => T("Register selected native host", "선택한 네이티브 호스트 등록");
    public static string OpenChatGpt => T("Open chatgpt.com in your browser", "브라우저에서 chatgpt.com 열기");
    public static string WelcomeStep3 => T("3. Choose your Pro plan", "3. Pro 요금제 선택");
    public static string Plan100 => T("Pro $100 (50 shared weekly)", "Pro $100 (주 50회 공유)");
    public static string Plan200 => T("Pro $200 (200 weekly + daily Sol Pro)", "Pro $200 (주 200회 + 일일 Sol Pro)");
    public static string PlanCustom => T("Custom (edit later in Settings)", "사용자 지정 (나중에 설정에서 수정)");
    public static string WelcomeStep4 => T("4. Optional background behavior", "4. 선택적 백그라운드 동작");
    public static string WelcomeOptInHint => T(
        "Both options stay off unless you check them.",
        "직접 선택하지 않으면 두 옵션 모두 꺼진 상태로 유지됩니다.");
    public static string WelcomeStep5 => T("5. Sign in, then run a first manual sync", "5. 로그인한 뒤 첫 수동 동기화");
    public static string WelcomeSignInHint => T(
        "Sign-in alone does not scan history. After you are signed in, use Run first manual sync.",
        "로그인만으로는 기록을 읽지 않습니다. 로그인한 뒤 첫 수동 동기화를 실행하세요.");
    public static string RunFirstManualSync => T("Run first manual sync", "첫 수동 동기화 실행");
    public static string SignInConnect => T("Sign in / connect", "로그인 / 연결");
    public static string SignInAgain => T("Sign in again", "다시 로그인");
    public static string ClaudeStatusLineCommandTooLong => T(
        "The generated statusLine command is too long. Move the existing inline command to a script file, then reconnect with a short script command.",
        "생성된 statusLine 명령이 너무 깁니다. 기존 인라인 명령을 스크립트 파일로 옮기고 짧은 호출 명령으로 다시 연결하세요.");
    public static string ClaudeDisconnected => T("Disconnected.", "연결을 해제했습니다.");
    public static string ClaudeDisconnectCleanupIncomplete => T(
        "Disconnected, but cleanup is incomplete. Claude settings may still contain CycleArc commands; check the settings file.",
        "연결은 해제됐지만 정리를 완료하지 못했습니다. Claude 설정에 CycleArc 명령이 남아 있을 수 있으니 설정 파일을 확인하세요.");
    public static string CompanionConnectedReady => T(
        "Companion connected. Run first manual sync when you are ready, or Finish to configure later.",
        "도우미가 연결되었습니다. 준비되면 첫 수동 동기화를 실행하거나, 마침으로 나중에 설정할 수 있습니다.");
    public static string DataExportSelected => T(
        "Data Export selected. Import conversations.json from Settings. No ChatGPT scan will run.",
        "데이터 내보내기가 선택되었습니다. 설정에서 conversations.json을 가져오세요. ChatGPT 검색은 실행되지 않습니다.");
    public static string OpeningWebViewSignIn => T("Opening WebView2 sign-in...", "WebView2 로그인을 여는 중...");
    public static string SignInCancelled => T(
        "Sign-in was cancelled. Google/Microsoft/Apple WebView login is unsupported.",
        "로그인이 취소되었습니다. WebView의 Google/Microsoft/Apple 로그인은 지원되지 않습니다.");
    public static string SignedInRunSync => T(
        "Signed in. Use Run first manual sync to scan history.",
        "로그인되었습니다. 첫 수동 동기화로 기록을 읽으세요.");
    public static string RunningFirstSync => T("Running first manual sync...", "첫 수동 동기화를 실행하는 중...");
    public static string CompanionNotInstalledPrefix => T("Not installed: ", "설치되지 않음: ");

    public static string AboutSubtitle => T(
        "Windows tray monitor for ChatGPT Pro quota and model usage.",
        "ChatGPT Pro 한도와 모델 사용량을 보는 Windows 트레이 모니터입니다.");
    public static string GitHubRepository => T("GitHub repository", "GitHub 저장소");
    public static string VersionPrefix => T("Version ", "버전 ");
    public static string DataSourceStatus => T("Data source status: ", "데이터 원본 상태: ");
    public static string SettingsTitle => T($"{ProductName} Settings", $"{ProductName} 설정");

    public static string GptProUsage(string usage) => T($"GPT Pro usage: {usage}", $"GPT Pro 사용량: {usage}");
    public static string GptProUsageServer(string usage, int reconstructed) => T(
        $"GPT Pro usage: {usage}  (server · reconstructed {reconstructed})",
        $"GPT Pro 사용량: {usage}  (서버 · 재구성 {reconstructed})");
    public static string GptProConfirmedHeadline(string count) => T(
        $"Confirmed Pro requests {count}",
        $"확인된 Pro 요청 {count}");
    public static string OnboardingConfirmed(string count) => T(
        $"Confirmed Pro requests {count}. Exact remaining count unavailable.",
        $"확인된 Pro 요청 {count}. 정확한 잔여 횟수는 확인할 수 없습니다.");
    public static string OnboardingPartialConfirmed(string count) => T(
        $"Confirmed Pro requests {count}. Coverage is incomplete. Exact remaining count unavailable.",
        $"확인된 Pro 요청 {count}. 데이터 상태가 불완전합니다. 정확한 잔여 횟수는 확인할 수 없습니다.");
    public static string OnboardingIncompleteConfirmed => T(
        "GPT Pro: exact remaining unavailable. Incomplete reconstruction.",
        "GPT Pro: 정확한 잔여 횟수 확인 불가. 대화 기록 재구성이 불완전합니다.");
    public static string RemainingWithCount(string remaining) => T($" remaining {remaining}", $" 남은 횟수 {remaining}");
    public static string CurrentPeriod(string start, string end) => T($"Current period {start} – {end}", $"현재 기간 {start} – {end}");
    public static string Soon => T("soon", "곧");

    public static string OnboardingUpToDate(int used, int limit) =>
        T($"GPT Pro usage: {used} / {limit}", $"GPT Pro 사용량: {used} / {limit}");
    public static string OnboardingPartial(int used, int limit) => T(
        $"Partial usage {used} / {limit}. Coverage is incomplete. You can inspect Coverage or retry.",
        $"일부 사용량 {used} / {limit}. 데이터 상태가 불완전합니다. 데이터 상태를 확인하거나 다시 시도할 수 있습니다.");
    public static string OnboardingIncompleteCount(int limit) => T(
        $"GPT Pro: ? / {limit}. Incomplete reconstruction.",
        $"GPT Pro: ? / {limit}. 대화 기록 재구성이 불완전합니다.");
    public static string OnboardingAuthRequired => T(
        "Authentication required. Sign in again before a history scan.",
        "인증이 필요합니다. 기록을 읽기 전에 다시 로그인하세요.");
    public static string OnboardingRateLimited => T(
        "Rate limited. Wait and retry the first manual sync later.",
        "요청이 제한되었습니다. 잠시 후 첫 수동 동기화를 다시 시도하세요.");
    public static string OnboardingSchemaMismatch => T(
        "Provider schema mismatch. A zero count is not a successful load.",
        "응답 형식이 맞지 않습니다. 0회는 성공적인 불러오기가 아닙니다.");

    public static string ToastNewPeriod => T("A new GPT Pro quota period has started.", "새 GPT Pro 한도 기간이 시작되었습니다.");
    public static string ToastGptProExhausted => T("GPT Pro quota is exhausted.", "GPT Pro 한도가 소진되었습니다.");
    public static string ToastGptPro10 => T("10% of GPT Pro quota remaining.", "GPT Pro 한도가 10% 남았습니다.");
    public static string ToastGptPro20 => T("20% of GPT Pro quota remaining.", "GPT Pro 한도가 20% 남았습니다.");
    public static string ToastSolExhausted => T("GPT-5.6 Sol Pro daily quota is exhausted.", "GPT-5.6 Sol Pro 일일 한도가 소진되었습니다.");
    public static string ToastSol10 => T("10% of Sol Pro daily quota remaining.", "Sol Pro 일일 한도가 10% 남았습니다.");
    public static string ToastSol20 => T("20% of Sol Pro daily quota remaining.", "Sol Pro 일일 한도가 20% 남았습니다.");
    public static string ToastCombinedExhausted => T("Combined Pro daily quota is exhausted.", "합산 Pro 일일 한도가 소진되었습니다.");
    public static string ToastCombined10 => T("10% of combined Pro daily quota remaining.", "합산 Pro 일일 한도가 10% 남았습니다.");
    public static string ToastCombined20 => T("20% of combined Pro daily quota remaining.", "합산 Pro 일일 한도가 20% 남았습니다.");
    public static string ToastSyncTitle => T($"{ProductName} sync", $"{ProductName} 동기화");

    public static string ImportTitle => T("Import official ChatGPT conversations.json", "공식 ChatGPT conversations.json 가져오기");
    public static string ImportedEvents(int count) => T($"Imported {count} usage events.", $"{count}개의 사용 기록을 가져왔습니다.");
    public static string RegisteredNativeHosts => T(
        "Registered the official Chrome/Edge native hosts.\n\n",
        "공식 Chrome/Edge 네이티브 호스트를 등록했습니다.\n\n");
    public static string NativeHostFailed => T("native-host registration failed", "네이티브 호스트 등록에 실패했습니다");
    public static string TimeColumn => T("Time", "시각");
    public static string ModelColumn => T("Model", "모델");
    public static string RawColumn => T("Raw", "원본");
    public static string EffortColumn => T("Effort", "추론 강도");
    public static string FamilyColumn => T("Family", "계열");
    public static string SourceColumn => T("Source", "출처");
    public static string Gpt6ProWeek => T("GPT-6 Pro week", "GPT-6 Pro 주간");
    public static string SolProDaily => T("Sol Pro daily", "Sol Pro 일일");
    public static string CombinedDaily => T("Combined daily", "합산 일일");
    public static string Plan100Short => "Pro $100";
    public static string Plan200Short => "Pro $200";
    public static string PlanCustomShort => T("Custom", "사용자 지정");
}
