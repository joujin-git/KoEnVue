using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using KoEnVue.App.Localization;
using KoEnVue.App.Models;
using KoEnVue.Core.Logging;
using KoEnVue.Core.Native;

namespace KoEnVue.App.Bootstrap;

/// <summary>
/// admin_elevation 옵션 처리 — UIPI 차단 우회 위한 self-elevation.
/// Medium IL → High IL 로 ShellExecuteW("runas") 자기 재실행 + 환경 변수
/// (KOENVUE_ELEVATED=1) 가드로 재진입 무한 루프 방지.
///
/// <para>
/// 매니페스트는 asInvoker 유지 (P5 invariant — PR-03 정책 보존). 부팅 자동 시작 경로는
/// schtasks /RL HIGHEST 가 별도 처리 (StartupTaskManager) — 이미 High IL 로 부팅되므로
/// <see cref="IsCurrentProcessElevated"/> 가 true → <see cref="TryRelaunchAsAdmin"/> 즉시 Continue.
/// </para>
///
/// <para>
/// 호출 시점: <c>Program.MainImpl</c> 의 mutex 획득 <b>전</b>. 원본 인스턴스가 mutex 안
/// 잡은 상태라 자식이 깨끗하게 새로 createdNew=true 획득 (race 0).
/// </para>
///
/// <para>
/// 로깅: <see cref="LogProvider"/> Sink 가 pre-Init 버퍼 (PR-09) 에 적재하지만,
/// elevation 성공 시 원본은 <see cref="Result.ExitForChild"/> 로 즉시 종료 → Logger.Initialize
/// 안 됨 → 버퍼 flush 안 됨. 따라서 <c>Program.AppendCrashFile</c> 의 koenvue_crash.txt
/// 도 동시 기록 (ELEVATION / ELEVATION-ERR 태그). 결정 #5 — 별도 elevation.txt 대신 재사용.
/// </para>
///
/// <para>상세: <c>docs/improvement-plan/PR-15-admin-elevation.md</c></para>
/// </summary>
internal static class AdminElevation
{
    // P3: 매직 스트링/넘버 const
    private const string RunAsVerb             = "runas";
    private const string ElevatedEnvVarName    = "KOENVUE_ELEVATED";
    private const string ElevatedEnvVarValue   = "1";

    /// <summary>ShellExecuteW 반환값 (HINSTANCE) 가 이 값 이하면 실패. 32 초과면 성공.</summary>
    private const int    ShellExecSuccessRcMin = 32;

    /// <summary>UAC 다이얼로그에서 "아니요" 거부 시 GetLastError = 1223.</summary>
    private const int    ErrorCancelled        = 1223;

    /// <summary>koenvue_crash.txt 의 elevation INFO 태그.</summary>
    private const string CrashTagInfo          = "ELEVATION";

    /// <summary>koenvue_crash.txt 의 elevation ERROR 태그.</summary>
    private const string CrashTagError         = "ELEVATION-ERR";

    /// <summary>
    /// Tray 메뉴 재시작 + self-elevation spawn 시 자식이 부모 종료를 명시 대기하도록 PID 를
    /// 전달하는 환경 변수. <see cref="WaitForRelaunchParentIfAny"/> 가 consume + 클리어.
    /// PR-15 후속 fix — mutex / trayicon NIM_ADD/DELETE / WTS notification 등 race 차단.
    /// </summary>
    private const string RelaunchParentPidEnvVarName = "KOENVUE_RELAUNCH_PARENT_PID";

    /// <summary>
    /// <see cref="WaitForRelaunchParentIfAny"/> 의 최대 대기 (ms). 부모가 hang 해도 진행하기 위한 안전 timeout.
    /// </summary>
    private const int RelaunchParentWaitMs = 5000;

    /// <summary>TryRelaunchAsAdmin 결과 — caller 분기 신호.</summary>
    internal enum Result
    {
        /// <summary>옵션 비활성 / 이미 High IL / 재진입 가드 트립 — 일반 실행 계속.</summary>
        Continue,
        /// <summary>자식 프로세스 spawn 성공 — 원본은 즉시 종료해야 한다.</summary>
        ExitForChild,
        /// <summary>UAC 거부 또는 ShellExecuteW 실패 — fallback (c) 알림 후 일반 권한 계속.</summary>
        ContinueAfterDenied,
    }

    /// <summary>
    /// 현재 프로세스가 High IL (UAC elevated) 이상인지. RID 비교 —
    /// SECURITY_MANDATORY_HIGH_RID (0x3000) 이상이면 elevated.
    /// </summary>
    internal static bool IsCurrentProcessElevated()
    {
        uint rid = Advapi32.GetCurrentProcessIntegrityLevelRid();
        return rid >= Win32Constants.SECURITY_MANDATORY_HIGH_RID;
    }

    /// <summary>
    /// 재진입 가드 — 환경 변수 KOENVUE_ELEVATED=1 세팅 여부. ShellExecuteW("runas")
    /// 호출 직전 SetEnvironmentVariable 로 set → 자식 프로세스가 상속. UAC 거부 후
    /// 사용자가 다시 KoEnVue 를 실행해도 새 cmd shell 의 환경 변수에는 KOENVUE_ELEVATED
    /// 가 없음 → 정상 self-elevation 시도. 부모-자식 process tree 한정 가드.
    /// </summary>
    internal static bool IsReentryGuardSet()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable(ElevatedEnvVarName),
            ElevatedEnvVarValue,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// config + 자기 IL + 재진입 가드 검사 후 admin 권한 자기 재실행 시도.
    /// caller (Program.MainImpl) 는 결과 enum 으로 분기.
    /// </summary>
    internal static Result TryRelaunchAsAdmin(AppConfig config)
    {
        if (!config.AdminElevation)
        {
            Log("skipping (config.admin_elevation=false)");
            return Result.Continue;
        }

        if (IsCurrentProcessElevated())
        {
            Log("skipping (already High IL or higher — likely schtasks /RL HIGHEST path)");
            return Result.Continue;
        }

        if (IsReentryGuardSet())
        {
            Log($"skipping ({ElevatedEnvVarName}={ElevatedEnvVarValue} — re-entry guard tripped)");
            return Result.Continue;
        }

        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            LogError("Environment.ProcessPath unavailable — cannot self-elevate");
            ShowDeniedMessage();
            return Result.ContinueAfterDenied;
        }

        // 환경 변수 set — ShellExecuteW 가 spawn 하는 자식 프로세스 환경 상속.
        Environment.SetEnvironmentVariable(ElevatedEnvVarName, ElevatedEnvVarValue);
        // 손자 (High IL) 가 본 인스턴스 종료를 명시 대기하도록 PID 전달 — mutex / trayicon /
        // 리소스 ownership race 차단. 본 인스턴스는 ExitForChild 분기로 곧 종료 (mutex 안 잡음).
        Environment.SetEnvironmentVariable(
            RelaunchParentPidEnvVarName,
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

        Log($"attempting self-elevation (path={exePath})");
        IntPtr rc;
        try
        {
            rc = Shell32.ShellExecuteW(
                hwnd: IntPtr.Zero,
                lpOperation: RunAsVerb,
                lpFile: exePath,
                lpParameters: null,
                lpDirectory: null,
                nShowCmd: Win32Constants.SW_SHOW);
        }
        catch (Exception ex)
        {
            LogError($"ShellExecuteW threw: {ex.GetType().Name}: {ex.Message}");
            ShowDeniedMessage();
            return Result.ContinueAfterDenied;
        }

        long rcValue = rc.ToInt64();
        if (rcValue > ShellExecSuccessRcMin)
        {
            Log("ShellExecuteW success — exiting original process for elevated child");
            return Result.ExitForChild;
        }

        int lastError = Marshal.GetLastWin32Error();
        if (lastError == ErrorCancelled)
        {
            Log($"re-elevation aborted by user (ERROR_CANCELLED {ErrorCancelled})");
            ShowDeniedMessage();
            return Result.ContinueAfterDenied;
        }

        LogError($"ShellExecuteW failed (rc={rcValue}, lastError={lastError})");
        ShowDeniedMessage();
        return Result.ContinueAfterDenied;
    }

    /// <summary>
    /// fallback (c) — UAC 거부 또는 ShellExecuteW 실패 시 사용자 안내 + 일반 권한 계속.
    /// hwnd=IntPtr.Zero — 부트 단계라 메인 윈도우 아직 생성 안 됨. uType=0 (MB_OK 기본).
    /// </summary>
    private static void ShowDeniedMessage()
    {
        User32.MessageBoxW(IntPtr.Zero,
            I18n.AdminElevationDeniedMessage,
            I18n.AdminElevationDeniedTitle,
            uType: Win32Constants.MB_OK);
    }

    // === 로깅 — pre-Init 버퍼 + crash.txt 두 채널 ===

    private static void Log(string msg)
    {
        // LogProvider.Sink (PR-09) 가 Logger.Initialize 전이면 pre-Init 버퍼.
        // ExitForChild 흐름에서는 Logger.Initialize 안 됨 → buffer flush 안 됨 → log 손실.
        // 그래서 crash.txt 도 동시 기록 (결정 #5 — 별도 elevation.txt 대신 재사용 + 태그 분리).
        LogProvider.Sink?.Info($"AdminElevation: {msg}");
        Program.AppendCrashFile(CrashTagInfo, msg);
    }

    private static void LogError(string msg)
    {
        LogProvider.Sink?.Error($"AdminElevation: {msg}");
        Program.AppendCrashFile(CrashTagError, msg);
    }

    /// <summary>
    /// Tray 의 "재시작" 안내 (admin_elevation 토글 후) 다음에 호출 — 자식이 새 config
    /// 기준으로 self-check 를 다시 거치도록 환경 변수 가드 해제. 호출 후 즉시 ShellExecuteW
    /// ("open") + 원본 WM_CLOSE 시퀀스 권장.
    /// </summary>
    internal static void ClearReentryGuard()
    {
        Environment.SetEnvironmentVariable(ElevatedEnvVarName, null);
    }

    /// <summary>
    /// Tray 메뉴 토글 재시작 경로 (<c>Tray.HandleMenuCommand</c> IDM_ADMIN_ELEVATION 의 YES 분기)
    /// 에서 호출 — 자식 spawn (<c>UriLauncher.Open</c>) 직전에 본 인스턴스의 PID 를 환경변수에 set.
    /// 자식은 <see cref="WaitForRelaunchParentIfAny"/> 에서 이 값을 읽어 부모 종료까지 명시 대기.
    /// <para>
    /// race 차단 대상: (1) mutex — 자식 createdNew=false 즉시 종료, (2) trayicon GUID NIM_ADD/DELETE
    /// 순서, (3) WTS notification / IME hook / log file lock 등 리소스 ownership. PR-15 §7
    /// 의 "원본이 mutex 안 잡은 상태" 가정은 부팅 시점 self-elevation 한정 — Tray 토글 재시작
    /// 경로에서는 원본이 mutex + 모든 리소스 보유 중이라 별도 동기화 필요.
    /// </para>
    /// </summary>
    internal static void SetRelaunchParentPidForTrayRestart()
    {
        Environment.SetEnvironmentVariable(
            RelaunchParentPidEnvVarName,
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// <c>Program.MainImpl</c> 의 Settings.Load 직후, <see cref="TryRelaunchAsAdmin"/> + mutex
    /// 시도 전에 호출 — 환경변수에 부모 PID 가 있으면 그 프로세스 종료까지 wait (timeout
    /// <see cref="RelaunchParentWaitMs"/>). 정상 부팅 (환경변수 없음) 에는 noop.
    /// <para>
    /// consume + 클리어 패턴 — wait 후 환경변수 제거. 다만 ShellExecuteW("runas") 분기는
    /// <see cref="TryRelaunchAsAdmin"/> 안에서 새로 set 하므로 손자 generation 에도 정확한
    /// 부모 PID (= 자식 PID) 전달.
    /// </para>
    /// <para>
    /// PID 재사용 paranoid 케이스: 부모가 종료된 후 다른 프로세스가 같은 PID 받아도
    /// <see cref="Process.GetProcessById"/> 가 그 새 프로세스 핸들을 잡고 wait 한다. 자식
    /// spawn ↔ Process.GetProcessById 사이 ms 단위라 재사용 확률 0 에 가깝지만, 그래도
    /// 일어나면 새 프로세스 종료까지 최대 5초 대기 후 강행 — 영구 hang 회피.
    /// </para>
    /// </summary>
    internal static void WaitForRelaunchParentIfAny()
    {
        string? pidStr = Environment.GetEnvironmentVariable(RelaunchParentPidEnvVarName);
        if (string.IsNullOrEmpty(pidStr)) return;

        // 환경변수 클리어 — 정상 부팅으로 진입한 인스턴스에 stale PID 가 남지 않도록.
        // 손자 spawn 경로 (TryRelaunchAsAdmin) 는 별도로 ShellExecuteW 직전 재설정.
        Environment.SetEnvironmentVariable(RelaunchParentPidEnvVarName, null);

        if (!int.TryParse(pidStr, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int parentPid))
        {
            Log($"WaitForRelaunchParent: invalid PID env value '{pidStr}'");
            return;
        }

        // catch 확장 (reviewer 권장): Process.GetProcessById = ArgumentException / InvalidOperationException,
        // Process.WaitForExit(int) = Win32Exception (handle 접근 거부 — IL 비대칭) / SystemException
        // (detached Process). 본 fix 시나리오 (자식이 같은 사용자 + 같은-or-낮은 IL 부모 wait) 잠재성
        // 낮지만 startup 단계 unhandled 예외 차단 = 영구 사망 회피.
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            bool exited = parent.WaitForExit(RelaunchParentWaitMs);
            if (exited)
                Log($"WaitForRelaunchParent: parent PID {parentPid} exited");
            else
                Log($"WaitForRelaunchParent: parent PID {parentPid} did not exit within {RelaunchParentWaitMs}ms — proceeding");
        }
        catch (Exception ex) when (ex is ArgumentException
            or InvalidOperationException
            or Win32Exception
            or SystemException)
        {
            // 부모가 이미 종료 / 접근 거부 / detached — 모두 "wait 불가, 진행" 으로 동일 처리.
            Log($"WaitForRelaunchParent: cannot wait for PID {parentPid} ({ex.GetType().Name}) — proceeding (likely already exited or inaccessible)");
        }
    }
}
