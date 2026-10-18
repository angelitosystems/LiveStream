using StreamPilot.Infrastructure.Diagnostics;

namespace StreamPilot.UnitTests;

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
}
