using NetworkProfileManager.Core.Models;
using NetworkProfileManager.Infrastructure.SessionProviders;
using Xunit;

namespace NetworkProfileManager.Tests;
public class ConfigurationTests
{
    [Theory]
    [InlineData("proxy.example", 8080)] [InlineData("127.0.0.1", 1080)] [InlineData("::1", 65535)]
    public void ValidProxy(string host, int port) => new ProxyConfiguration { Host = host, Port = port }.Validate();
    [Theory]
    [InlineData("", 8080)] [InlineData("https://proxy.example", 80)] [InlineData("user@host", 80)]
    [InlineData("host", 0)] [InlineData("host", 65536)] [InlineData("evil\r\nHost", 80)]
    public void InvalidProxy(string host, int port) => Assert.Throws<ArgumentException>(() => new ProxyConfiguration { Host = host, Port = port }.Validate());
    [Fact] public void ExpiredProxyRejected() => Assert.Throws<ArgumentException>(() => new ProxyConfiguration { Host = "host", ExpirationDate = DateTime.Today.AddDays(-1) }.Validate());
    [Fact] public void SecretNotInDiagnosticString()
    {
        var p = new ProxyConfiguration { Host = "host", Username = "private-user", Password = "private-pass" };
        Assert.DoesNotContain(p.Password, p.ToString()); Assert.DoesNotContain(p.Username, p.ToString());
    }
    [Fact] public void StickyNotInvented()
    {
        var provider = new StandardSessionProvider(); Assert.False(provider.SupportsSticky);
        Assert.Contains("Sticky Session chưa được cấu hình", Assert.Throws<NotSupportedException>(() => provider.Resolve(new() { Host = "host", RotationMode = RotationMode.Sticky }, "A")).Message);
    }
    [Theory] [InlineData("http://api.ipify.org", 20)] [InlineData("https://user:pass@host", 20)] [InlineData("https://host", 0)]
    public void InvalidEchoSettings(string endpoint, int timeout) => Assert.Throws<ArgumentException>(() => new AppSettings { EchoEndpoint = endpoint, TimeoutSeconds = timeout }.Validate());
    [Theory] [InlineData(false)] [InlineData(true)]
    public void RotationComparison(bool rotating)
    {
        var list = Enumerable.Range(0, 3).Select(i => new IpTestResult("A", rotating && i == 2 ? "203.0.113.2" : "203.0.113.1", true, 1, DateTimeOffset.Now, "mock", null));
        Assert.Equal(rotating ? "Phát hiện IP thay đổi" : "Ba lần kiểm tra cùng IP", RotationAnalysis.Describe(list));
    }
    [Fact] public void FailedTestDoesNotProveRotation() => Assert.Contains("Chưa đủ", RotationAnalysis.Describe([new("A", null, false, 1, DateTimeOffset.Now, "mock", "failed")]));
}
