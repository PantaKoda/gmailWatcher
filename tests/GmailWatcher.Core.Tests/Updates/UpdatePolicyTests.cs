using GmailWatcher.Core.Updates;

namespace GmailWatcher.Core.Tests.Updates;

public sealed class UpdatePolicyTests
{
    private static AppVersion V(string text) => AppVersion.TryParse(text, out var v) ? v : throw new ArgumentException(text);

    private static ReleaseInfo Release(string tag, bool draft = false, bool pre = false) => new()
    {
        Version = V(tag),
        Tag = tag,
        Title = $"Gmail Watcher {tag}",
        HtmlUrl = new Uri($"https://github.com/o/r/releases/tag/{tag}"),
        IsDraft = draft,
        IsPreRelease = pre,
    };

    [Theory]
    [InlineData("v0.2.0", 0, 2, 0, null)]
    [InlineData("1.10.3", 1, 10, 3, null)]
    [InlineData("0.3.0-rc1", 0, 3, 0, "rc1")]
    [InlineData("0.2.0+9cdd779", 0, 2, 0, null)]
    public void Versions_parse_from_tags_and_informational_versions(string text, int major, int minor, int patch, string? pre)
    {
        Assert.True(AppVersion.TryParse(text, out var version));
        Assert.Equal(new AppVersion(major, minor, patch, pre), version);
    }

    [Theory]
    [InlineData("nightly")]
    [InlineData("1.2")]
    [InlineData("v1.2.x")]
    [InlineData("1.2.3-")]
    public void Non_version_tags_are_not_versions(string text) => Assert.False(AppVersion.TryParse(text, out _));

    [Fact]
    public void Versions_compare_numerically_and_pre_releases_come_first()
    {
        Assert.True(V("0.10.0") > V("0.9.9"));
        Assert.True(V("0.3.0-rc1") < V("0.3.0"));
        Assert.True(V("0.3.0-rc1") > V("0.2.9"));
    }

    [Fact]
    public void Newer_lists_published_stable_releases_above_the_current_version_newest_first()
    {
        var releases = new[]
        {
            Release("v0.1.0"), Release("v0.2.0"), Release("v0.4.0", draft: true), Release("v0.3.0"),
            Release("v0.5.0-beta", pre: true), Release("v0.3.1", pre: true),
        };

        var newer = UpdatePolicy.Newer(releases, V("0.1.0"));

        Assert.Equal(["v0.3.0", "v0.2.0"], newer.Select(r => r.Tag));
        Assert.Empty(UpdatePolicy.Newer(releases, V("0.3.0")));
    }

    [Theory]
    [InlineData("ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789  GmailWatcher-0.2.0-win-x64.zip", true)]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789 *GmailWatcher-0.2.0-win-x64.zip\n", true)]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789  Other.zip", false)]
    [InlineData("not-a-hash  GmailWatcher-0.2.0-win-x64.zip", false)]
    public void Checksum_files_must_name_the_zip_and_hold_a_SHA256(string content, bool valid)
    {
        var hash = UpdatePolicy.ParseChecksum(content, "GmailWatcher-0.2.0-win-x64.zip");

        Assert.Equal(valid, hash is not null);
        if (valid)
        {
            Assert.Equal("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", hash);
        }
    }

    [Fact]
    public void A_release_is_installable_on_Windows_only_with_its_zip_and_checksum()
    {
        var bare = Release("v0.2.0");
        var complete = bare with
        {
            Assets =
            [
                new ReleaseAsset("GmailWatcher-0.2.0-win-x64.zip", new Uri("https://github.com/o/r/releases/download/v0.2.0/GmailWatcher-0.2.0-win-x64.zip"), 1),
                new ReleaseAsset("GmailWatcher-0.2.0-win-x64.zip.sha256", new Uri("https://github.com/o/r/releases/download/v0.2.0/GmailWatcher-0.2.0-win-x64.zip.sha256"), 1),
            ],
        };

        Assert.False(bare.CanInstallOnWindows);
        Assert.True(complete.CanInstallOnWindows);
    }
}
