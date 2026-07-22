using System.Runtime.Versioning;
using Ai.Tlbx.MidTerm.Models.System;
using Ai.Tlbx.MidTerm.Services.Security;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class SystemUserProviderTests
{
    [Theory]
    [InlineData(@"CONTOSO\johannes.schmidt", "johannes.schmidt")]
    [InlineData("johannes.schmidt@contoso.local", "johannes.schmidt")]
    [InlineData("johannes.schmidt", "johannes.schmidt")]
    public void NormalizeWindowsUsername_StripsDomainPrefixes(string raw, string expected)
    {
        Assert.Equal(expected, SystemUserProvider.NormalizeWindowsUsername(raw));
    }

    [Fact]
    public void AddWindowsUser_PreservesDomainQualifiedNameAndSid()
    {
        var users = new Dictionary<string, UserInfo>(StringComparer.OrdinalIgnoreCase);

        SystemUserProvider.AddWindowsUser(users, @"CONTOSO\johannes.schmidt", "S-1-5-21-123");
        SystemUserProvider.AddWindowsUser(users, @"contoso\JOHANNES.SCHMIDT");

        var entry = Assert.Single(users);
        Assert.Equal(@"CONTOSO\johannes.schmidt", entry.Value.Username);
        Assert.Equal("S-1-5-21-123", entry.Value.Sid);
    }

    [Theory]
    [InlineData("CONTOSO", "Johannes Schmidt", @"CONTOSO\Johannes Schmidt")]
    [InlineData(null, "adm.js", "adm.js")]
    [SupportedOSPlatform("windows")]
    public void BuildWindowsAccountName_CombinesDomainAndUsername(string? domain, string username, string expected)
    {
        Assert.Equal(expected, SystemUserProvider.BuildWindowsAccountName(domain, username));
    }
}
