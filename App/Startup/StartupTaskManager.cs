using System.Diagnostics;
using KoEnVue.App.Models;
using KoEnVue.Core.Logging;
using KoEnVue.Core.Xml;

namespace KoEnVue.App.Startup;

/// <summary>
/// Windows Task Scheduler (<c>schtasks.exe</c>) 기반 "시작 시 자동 실행" 작업 등록/조회/동기화.
/// Tray.cs 에서 분리(PR-04) — UI 와 무관한 schtasks XML 조립 + CLI 호출 + 결과 검증 책임을 단독 모듈로.
/// <para>
/// 외부 의존성은 <see cref="Logger"/> 와 <see cref="XmlEntityCodec"/> 만이다. 메뉴 ID 는 모르고
/// 호출자(Tray) 가 ID 와 핸들러를 mapping 한다.
/// </para>
/// <para>
/// PR-03 (asInvoker 전환) 후엔 <c>&lt;LogonTrigger&gt;&lt;UserId&gt;</c> 가 비면 ANY-user trigger 로
/// 해석되어 admin 토큰 요구가 발생하므로 본인 logon trigger 로 명시한다. Principal <c>&lt;UserId&gt;</c>
/// 는 의도적으로 비워둔다 (명시 시 SID lookup 검증에서 admin 요구).
/// </para>
/// </summary>
internal static class StartupTaskManager
{
    /// <summary>schtasks 작업 이름. v0.x 부터 변경 금지 — 마이그레이션 호환성.</summary>
    private const string TaskName = "KoEnVue";

    /// <summary>
    /// 시작 프로그램 로그온 지연 (ISO 8601 duration).
    /// 부팅 자동 실행 시 explorer 트레이 초기화 전에 앱이 떠서 Shell_NotifyIconW NIM_ADD 가 실패하는
    /// 레이스를 회피한다. 재시도로 복구되긴 하지만 매 부팅마다 warn 로그가 남는 문제 해소 목적.
    /// </summary>
    private const string StartupTaskDelay = "PT15S";

    /// <summary>v0.9.3.0 (PR-03) 부터 부여하는 RunLevel — UAC 프롬프트 없는 일반 권한 실행.</summary>
    private const string RunLevelLeastPrivilege = "LeastPrivilege";

    /// <summary>
    /// PR-15: admin_elevation=true 일 때 부여하는 RunLevel — 부팅 자동 시작 시 UAC 0 으로 admin
    /// 권한 획득해 UIPI 차단 우회. asInvoker 매니페스트는 그대로 유지 (P5 invariant).
    /// </summary>
    private const string RunLevelHighestAvailable = "HighestAvailable";

    // P3: 매직 넘버 금지
    private const int SchtasksQueryTimeoutMs = 3000;
    private const int SchtasksCommandTimeoutMs = 5000;

    /// <summary>현재 사용자 컨텍스트에 <see cref="TaskName"/> 작업이 등록돼 있는지 확인.</summary>
    internal static bool IsStartupRegistered()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/query /tn \"{TaskName}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(SchtasksQueryTimeoutMs);
            return proc?.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
            or PlatformNotSupportedException or FileNotFoundException)
        {
            // 정책 항목 1(타입 좁히기): Process.Start 실패(스케쥴러 없음/실행 권한 없음) 만 false 로 폴백.
            // 로직 버그(NullRef 등)는 propagate 시켜 표면화.
            return false;
        }
    }

    /// <summary>
    /// 등록 ↔ 해제 토글. 메뉴 핸들러에서 호출.
    /// PR-15: <paramref name="config"/> 의 <c>AdminElevation</c> 으로 RunLevel 분기.
    /// </summary>
    internal static void ToggleStartupRegistration(AppConfig config)
    {
        try
        {
            if (IsStartupRegistered())
            {
                bool deleteOk = RunSchtasks($"/delete /tn \"{TaskName}\" /f");
                if (deleteOk && !IsStartupRegistered())
                    Logger.Info("Startup registration removed");
                else
                    Logger.Warning("Startup registration delete did not take effect (see schtasks log above)");
            }
            else
            {
                string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
                bool createOk = RegisterStartupTaskWithXml(exePath, config.AdminElevation);
                // schtasks 가 silent 로 거부하는 케이스가 있어 post-check 로 실제 등록 여부 검증
                if (createOk && IsStartupRegistered())
                    Logger.Info($"Startup registration created (admin_elevation={config.AdminElevation})");
                else
                    Logger.Warning("Startup registration create did not take effect (see schtasks log above)");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
            or PlatformNotSupportedException or FileNotFoundException
            or IOException or UnauthorizedAccessException)
        {
            // 정책 항목 1(타입 좁히기): schtasks.exe 실행 실패 + 임시 XML 파일 write 실패만 잡음.
            Logger.Warning($"Failed to toggle startup registration: {ex.Message}");
        }
    }

    /// <summary>
    /// LogonTrigger.Delay 를 포함한 Task Scheduler 2.0 XML 을 생성해 schtasks /xml 로 등록한다.
    /// /tr 방식과 달리 초 단위 지연이 지정 가능 — Shell(explorer) 트레이 초기화 레이스 회피.
    /// <para>
    /// tempPath 는 GUID 기반 unpredictable 이름 + <see cref="FileMode.CreateNew"/> + <see cref="FileShare.None"/>
    /// 로 작성한다. 같은 위치에 사전 placed symlink 가 있어도 CreateNew 가 실패하므로 schtasks 가
    /// 가짜 XML 을 읽어들이는 TOCTOU 표면이 차단된다 (B2). PR-03 asInvoker 전환 후 Admin 토큰 표면은
    /// 사라졌지만, 양식 차원의 안전망 + 동시 등록 race 방어 목적으로 유지.
    /// </para>
    /// </summary>
    private static bool RegisterStartupTaskWithXml(string exePath, bool adminElevation)
    {
        string xml = BuildStartupTaskXml(exePath, adminElevation);
        // %TEMP% 는 per-user. GUID 로 attacker 가 미리 같은 경로를 점유하지 못하게 한다.
        string tempPath = Path.Combine(Path.GetTempPath(), $"koenvue-task-{Guid.NewGuid():N}.xml");
        try
        {
            // schtasks /xml 은 UTF-16 LE + BOM 을 기대한다. Encoding.Unicode 가 정확히 그 포맷.
            // FileMode.CreateNew + FileShare.None: pre-placed file/symlink 발견 시 IOException 발생.
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs, System.Text.Encoding.Unicode))
            {
                sw.Write(xml);
            }
            return RunSchtasks($"/create /tn \"{TaskName}\" /xml \"{tempPath}\" /f");
        }
        finally
        {
            try { File.Delete(tempPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ex; }
        }
    }

    /// <summary>
    /// schtasks /xml 에 전달할 Task Scheduler 2.0 XML 을 조립한다. LogonTrigger.Delay 에
    /// <see cref="StartupTaskDelay"/> 삽입. 최소 필드만 쓰고 나머지는 schtasks 기본값에 위임.
    /// <para>
    /// <b>두 개의 <c>UserId</c> 구분</b>:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <c>&lt;LogonTrigger&gt;&lt;UserId&gt;...&lt;/UserId&gt;</c> — <b>trigger 대상</b> user.
    /// 본 필드가 비면 schtasks 는 ANY-user logon trigger 로 해석 → admin 권한 요구
    /// (asInvoker 토큰에서 "액세스가 거부되었습니다" ExitCode=1). 본인 logon 만 발화하도록 명시 필요.
    /// </item>
    /// <item>
    /// <c>&lt;Principal&gt;&lt;UserId&gt;...&lt;/UserId&gt;</c> — task <b>실행 user</b>.
    /// 의도적 미포함 — 명시 시 schtasks 가 SID lookup 검증에서 admin 토큰을 요구 (v0.9.x 의
    /// requireAdministrator 가 그 검증을 통과시켜 줬을 뿐). 비워두면 schtasks 가 current user 의
    /// SID 로 자동 채워 권한 검증을 우회.
    /// </item>
    /// </list>
    /// <para>
    /// 두 필드의 의도가 완전히 다르고 둘 다 채우면 다시 admin 요구로 회귀하므로 LogonTrigger 쪽만 채운다.
    /// </para>
    /// </summary>
    private static string BuildStartupTaskXml(string exePath, bool adminElevation)
    {
        // LogonTrigger.<UserId> — trigger 대상 user 식별. 본인 logon 만 발화하도록 명시 필요.
        string userId = XmlEntityCodec.Escape($"{Environment.UserDomainName}\\{Environment.UserName}");
        // /tr 방식의 기존 Command 형식("\"path\"")을 XML 에서도 동일하게 유지 — QueryRegisteredTask 의
        // Trim('"') 로직이 두 방식 모두 호환되어 마이그레이션 후에도 경로 비교가 안정적.
        string command = XmlEntityCodec.Escape($"\"{exePath}\"");
        // PR-15: admin_elevation=true 시 RunLevel=HighestAvailable (부팅 자동 시작 시 UAC 0 으로 admin).
        // 매니페스트는 asInvoker 유지 — schtasks 의 RunLevel 만 분기.
        string runLevel = adminElevation ? RunLevelHighestAvailable : RunLevelLeastPrivilege;
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <Delay>{StartupTaskDelay}</Delay>
      <UserId>{userId}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>{runLevel}</RunLevel>
    </Principal>
  </Principals>
  <Actions Context="Author">
    <Exec>
      <Command>{command}</Command>
    </Exec>
  </Actions>
</Task>
""";
    }

    /// <summary>
    /// 등록된 시작 프로그램 태스크의 exe 경로가 현재 실행 파일 경로와 다르면 재등록한다.
    /// 포터블 모드에서 exe를 다른 폴더로 옮겼을 때 태스크 스케줄러가 오래된 절대 경로를 가리키는 문제를 해결.
    /// schtasks 호출 지연(~100~300ms)을 main 스레드에서 분리하기 위해 백그라운드 스레드에서 실행.
    /// </summary>
    internal static void SyncStartupPathAsync(AppConfig config)
    {
        var thread = new Thread(() => SyncStartupPathCore(config))
        {
            IsBackground = true,
            Name = "StartupPathSync",
        };
        thread.Start();
    }

    private static void SyncStartupPathCore(AppConfig config)
    {
        try
        {
            var (registeredCommand, registeredDelay, registeredRunLevel) = QueryRegisteredTask();
            if (registeredCommand is null)
            {
                // 등록 안 돼 있거나 쿼리 실패 — 정상 케이스 (대부분 사용자는 startup 안 씀)
                return;
            }

            // 구 버전(/tr 방식) 및 신 버전(/xml + "\"path\"" 보존) 모두 Command 양끝에 리터럴 큰따옴표가
            // 있다. Path.GetFullPath 는 " 를 잘못된 문자로 보고 ArgumentException 을 던져 PathsEqual 이
            // 원본 문자열 비교로 폴백하므로, 비교 전 감싼 따옴표를 제거해 매 부팅마다 재등록되는 것을 막는다.
            string registeredPath = registeredCommand.Trim('"');

            string? currentPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(currentPath)) return;

            bool pathMatches = PathsEqual(registeredPath, currentPath);
            // 구 버전(/tr 방식)은 <Delay> 요소가 없어 null. 이 경우도 마이그레이션 대상.
            bool delayMatches = string.Equals(registeredDelay, StartupTaskDelay, StringComparison.Ordinal);
            // PR-15: expected RunLevel 을 config.AdminElevation 에서 derive.
            // v0.9.x admin 잔재 + PR-03 LeastPrivilege 잔재 + PR-15 admin 토글 모두 마이그레이션 대상.
            string expectedRunLevel = config.AdminElevation ? RunLevelHighestAvailable : RunLevelLeastPrivilege;
            bool runLevelMatches = string.Equals(registeredRunLevel, expectedRunLevel, StringComparison.Ordinal);

            if (pathMatches && delayMatches && runLevelMatches)
            {
                Logger.Debug("Startup task already in sync (path + delay + runlevel)");
                return;
            }

            Logger.Info(
                $"Startup task out of sync (path='{registeredPath}' delay='{registeredDelay ?? "<none>"}' "
                + $"runlevel='{registeredRunLevel ?? "<none>"}'), re-registering with delay {StartupTaskDelay} + {expectedRunLevel}");
            RegisterStartupTaskWithXml(currentPath, config.AdminElevation);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
            or PlatformNotSupportedException or FileNotFoundException
            or IOException or UnauthorizedAccessException)
        {
            // 정책 항목 1(타입 좁히기): schtasks.exe 실행 실패 + 임시 XML 파일 write 실패만 잡음.
            Logger.Warning($"Failed to sync startup task path: {ex.Message}");
        }
    }

    /// <summary>
    /// 등록된 시작 프로그램 태스크의 실행 명령 경로 / LogonTrigger 지연 / RunLevel 을 반환.
    /// 미등록 또는 실패 시 모두 null. RunLevel 은 PR-03 (v0.9.3.0) admin-elevation → LeastPrivilege
    /// 마이그레이션 감지 목적.
    /// </summary>
    private static (string? Command, string? Delay, string? RunLevel) QueryRegisteredTask()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/query /tn \"{TaskName}\" /xml ONE")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (null, null, null);
            string xml = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(SchtasksQueryTimeoutMs);
            if (proc.ExitCode != 0) return (null, null, null);
            return (ExtractTagFromXml(xml, "Command", unescape: true),
                    ExtractTagFromXml(xml, "Delay", unescape: false),
                    ExtractTagFromXml(xml, "RunLevel", unescape: false));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or InvalidOperationException
            or PlatformNotSupportedException
            or FileNotFoundException
            or IOException)
        {
            // schtasks.exe 실행/stdout 파이프 읽기 실패만 흡수 → null(미등록으로 취급).
            // 로직 버그는 전파.
            _ = ex;
            return (null, null, null);
        }
    }

    /// <summary>
    /// PR-15: admin_elevation 옵션 토글 직후 호출 — 이미 등록된 startup task 의 RunLevel 을
    /// 현재 config 에 맞게 즉시 재등록. 미등록 상태면 noop. 동기 호출 (UI 응답 즉시 반영).
    /// </summary>
    internal static void ReregisterIfAdminChanged(AppConfig config)
    {
        try
        {
            if (!IsStartupRegistered()) return;
            string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (string.IsNullOrEmpty(exePath))
            {
                Logger.Warning("ReregisterIfAdminChanged: exe path unavailable");
                return;
            }
            bool ok = RegisterStartupTaskWithXml(exePath, config.AdminElevation);
            if (ok)
                Logger.Info($"Startup task re-registered with admin_elevation={config.AdminElevation}");
            else
                Logger.Warning($"Startup task re-registration failed for admin_elevation={config.AdminElevation}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
            or PlatformNotSupportedException or FileNotFoundException
            or IOException or UnauthorizedAccessException)
        {
            Logger.Warning($"ReregisterIfAdminChanged: {ex.Message}");
        }
    }

    /// <summary>
    /// schtasks XML 출력에서 지정한 단일 태그의 내용을 추출한다.
    /// <paramref name="unescape"/>가 true 면 <see cref="XmlEntityCodec.Unescape"/> 로 복원 —
    /// Command 경로엔 필요하지만 Delay(ISO 8601) 같은 순수 텍스트에는 불필요.
    /// </summary>
    private static string? ExtractTagFromXml(string xml, string tagName, bool unescape)
    {
        string openTag = $"<{tagName}>";
        string closeTag = $"</{tagName}>";
        int start = xml.IndexOf(openTag, StringComparison.Ordinal);
        if (start < 0) return null;
        start += openTag.Length;
        int end = xml.IndexOf(closeTag, start, StringComparison.Ordinal);
        if (end < 0) return null;

        string content = xml[start..end];
        if (unescape) content = XmlEntityCodec.Unescape(content);
        return content.Trim();
    }

    /// <summary>Windows 경로 동일성 비교 (대소문자 무시 + 정규화). 정규화 실패 시 원본 비교 폴백.</summary>
    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a),
                Path.GetFullPath(b),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException
            or System.Security.SecurityException
            or PathTooLongException
            or NotSupportedException)
        {
            // 경로 정규화 실패(잘못된 문자/권한/길이 초과/플랫폼 미지원) 시 원본 문자열 비교로 폴백.
            // 로직 버그는 전파.
            _ = ex;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// schtasks.exe 실행 + ExitCode / STDOUT / STDERR 검사. 실패 시 Warning 1줄 로깅.
    /// PR-03 asInvoker 전환 후 schtasks 가 XML 의 <c>&lt;UserId&gt;</c> / <c>&lt;RunLevel&gt;</c> 조합을
    /// 거부할 수 있는 케이스를 진단하기 위해 silent ExitCode 무시 동작을 제거. 성공 시 true.
    /// </summary>
    private static bool RunSchtasks(string arguments)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi);
        if (proc is null)
        {
            Logger.Warning($"schtasks {arguments}: Process.Start returned null");
            return false;
        }
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        bool exited = proc.WaitForExit(SchtasksCommandTimeoutMs);
        if (!exited)
        {
            Logger.Warning($"schtasks {arguments}: timed out after {SchtasksCommandTimeoutMs}ms");
            return false;
        }
        if (proc.ExitCode != 0)
        {
            Logger.Warning(
                $"schtasks {arguments}: ExitCode={proc.ExitCode}, "
                + $"stderr={stderr.Trim()}, stdout={stdout.Trim()}");
            return false;
        }
        return true;
    }
}
