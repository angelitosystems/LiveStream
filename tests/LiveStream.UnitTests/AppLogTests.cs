using LiveStream.Infrastructure.Diagnostics;

namespace LiveStream.UnitTests;

public sealed class AppLogTests
{
    [Theory]
    [InlineData("Connecting to RTMP URL rtmp://live.twitch.tv/app/live_123_abc...", "rtmp://live.twitch.tv/app/***")]
    [InlineData("url rtmps://a.rtmp.youtube.com/live2/xxxx-yyyy-zzzz", "rtmps://a.rtmp.youtube.com/live2/***")]
    public void Redact_HidesStreamKeyInIngestUrls(string input, string expected)
    {
        var redacted = AppLog.Redact(input);
        Assert.Contains(expected, redacted);
        Assert.DoesNotContain("live_123", redacted);
        Assert.DoesNotContain("xxxx-yyyy", redacted);
    }

    [Fact]
    public void Redact_LeavesOtherTextAlone() =>
        Assert.Equal("Output 'sp_stream': stopping", AppLog.Redact("Output 'sp_stream': stopping"));

    [Fact]
    public void Fatal_IsOnDiskWithItsStackTrace_BeforeReturning()
    {
        var marker = Guid.NewGuid().ToString("N");
        Exception thrown;
        try { throw new InvalidOperationException(marker); }
        catch (InvalidOperationException ex) { thrown = ex; }

        AppLog.Fatal("test", "cierre", thrown);

        var line = File.ReadLines(AppLog.CurrentFile).Single(l => l.Contains(marker));
        Assert.Contains(nameof(Fatal_IsOnDiskWithItsStackTrace_BeforeReturning), line);
    }
}
