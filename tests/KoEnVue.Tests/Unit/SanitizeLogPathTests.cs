using System.IO;
using KoEnVue.App.Config;
using Xunit;

namespace KoEnVue.Tests.Unit;

/// <summary>
/// <see cref="PortablePath.SanitizeLogPath"/> 의 PR-03 B1 보안 표면 박제.
/// asInvoker (admin 토큰 0) 가 1차 방어이지만 사용자가 <c>config.json:log_file_path</c> 에
/// 시스템 폴더를 지정해도 거부되도록 본 함수가 2차 방어.
/// </summary>
public class SanitizeLogPathTests
{
    [Theory]
    [InlineData(@"C:\Windows\System32\koenvue.log")]
    [InlineData(@"\\server\share\koenvue.log")]
    [InlineData(@"C:\Users\OtherUser\koenvue.log")]
    public void Reject_OutsideAllowedRoot_FallsBackAndReports(string requested)
    {
        string result = PortablePath.SanitizeLogPath(requested, out string? reason);
        Assert.NotNull(reason);
        Assert.Contains("outside allowed roots", reason);
        Assert.Equal(PortablePath.ResolveLogPath(), result);
    }

    [Fact]
    public void Reject_ParentTraversal_NormalizedAndChecked()
    {
        // BaseDirectory 안에서 ../../../ 로 탈출. Path.GetFullPath 가 정규화한 후
        // BaseDirectory 하위가 아니면 거부. BaseDirectory 깊이 의존성을 IsUnderAllowedRoot 로 흡수.
        string traversal = Path.Combine(PortablePath.BaseDirectory, @"..\..\..\evil.log");
        string result = PortablePath.SanitizeLogPath(traversal, out string? reason);
        if (PortablePath.IsUnderAllowedRoot(Path.GetFullPath(traversal)))
        {
            Assert.Null(reason);
        }
        else
        {
            Assert.NotNull(reason);
            Assert.Contains("outside allowed roots", reason);
            Assert.Equal(PortablePath.ResolveLogPath(), result);
        }
    }

    [Fact]
    public void Allow_UnderBaseDirectory_Preserved()
    {
        string requested = Path.Combine(PortablePath.BaseDirectory, "custom.log");
        string result = PortablePath.SanitizeLogPath(requested, out string? reason);
        Assert.Null(reason);
        Assert.Equal(Path.GetFullPath(requested), result);
    }

    [Fact]
    public void Allow_UnderFallbackRoot_Preserved()
    {
        string requested = Path.Combine(PortablePath.FallbackRoot, "custom.log");
        string result = PortablePath.SanitizeLogPath(requested, out string? reason);
        Assert.Null(reason);
        Assert.Equal(Path.GetFullPath(requested), result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Fallback_NullOrEmpty_ReturnsResolveLogPath_NoReason(string? requested)
    {
        string result = PortablePath.SanitizeLogPath(requested, out string? reason);
        Assert.Null(reason);
        Assert.Equal(PortablePath.ResolveLogPath(), result);
    }

    [Fact]
    public void Fallback_InvalidChars_NormalizationFails_ReportsReason()
    {
        // NUL 문자 — Path.GetFullPath 가 ArgumentException 던지는 확실한 invalid char.
        string invalid = "C:\\test\0invalid.log";
        string result = PortablePath.SanitizeLogPath(invalid, out string? reason);
        Assert.NotNull(reason);
        Assert.Contains("could not be normalized", reason);
        Assert.Equal(PortablePath.ResolveLogPath(), result);
    }

    [Fact]
    public void Reject_JunctionUnderAllowedRoot_ThatEscapes()
    {
        // admin_elevation 시 junction 탈출(H1) — FallbackRoot 아래 junction → Temp 바깥.
        string juncDir = Path.Combine(PortablePath.FallbackRoot, "reparse-test-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(Path.GetTempPath(), "koenvue-reparse-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(PortablePath.FallbackRoot);
        Directory.CreateDirectory(target);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{juncDir}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            Assert.NotNull(proc);
            proc.WaitForExit(10_000);
            if (proc.ExitCode != 0 || !Directory.Exists(juncDir))
            {
                // CI/권한 환경에서 junction 생성 불가 시 스킵 (문자열 접두 테스트는 기존 Theory가 커버).
                return;
            }

            string requested = Path.Combine(juncDir, "koenvue.log");
            string result = PortablePath.SanitizeLogPath(requested, out string? reason);
            Assert.NotNull(reason);
            Assert.Contains("reparse", reason, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(PortablePath.ResolveLogPath(), result);
        }
        finally
        {
            try { if (Directory.Exists(juncDir)) Directory.Delete(juncDir); } catch { /* best-effort */ }
            try { if (Directory.Exists(target)) Directory.Delete(target); } catch { /* best-effort */ }
        }
    }
}
