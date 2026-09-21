using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

public class VsphereSessionsTests
{
    [Fact]
    public void A_session_list_the_account_may_not_read_is_null_not_empty()
    {
        // "Not allowed to look" must never read as "nobody is signed in" —
        // that is the one conclusion a session check exists to draw.
        var sessions = VsphereSessions.Parse("""
            <RetrievePropertiesExResponse xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <returnval><objects>
                <obj type="SessionManager">SessionManager</obj>
                <propSet><name>currentSession</name><val xsi:type="UserSession">
                  <key>52aa-0001</key><userName>VSPHERE.LOCAL\svc</userName>
                  <loginTime>2026-09-21T16:45:12.5Z</loginTime><lastActiveTime>2026-09-21T16:45:13Z</lastActiveTime>
                  <ipAddress>10.0.0.5</ipAddress><userAgent>probe</userAgent>
                </val></propSet>
                <missingSet><path>sessionList</path>
                  <fault><fault xsi:type="NoPermission"/><localizedMessage>denied</localizedMessage></fault>
                </missingSet>
              </objects></returnval>
            </RetrievePropertiesExResponse>
            """);

        Assert.Null(sessions.All);
        Assert.Equal("NoPermission", sessions.Unreadable);
        Assert.Equal("52aa-0001", sessions.Current!.Key);
        Assert.Equal(new DateTimeOffset(2026, 9, 21, 16, 45, 12, 500, TimeSpan.Zero), sessions.Current.LoginTimeUtc);
    }

    [Fact]
    public void A_readable_list_is_read_whole()
    {
        var sessions = VsphereSessions.Parse("""
            <RetrievePropertiesExResponse xmlns="urn:vim25" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <returnval><objects>
                <obj type="SessionManager">SessionManager</obj>
                <propSet><name>sessionList</name><val xsi:type="ArrayOfUserSession">
                  <UserSession xsi:type="UserSession"><key>a</key><userName>u1</userName></UserSession>
                  <UserSession xsi:type="UserSession"><key>b</key><userName>u2</userName></UserSession>
                </val></propSet>
              </objects></returnval>
            </RetrievePropertiesExResponse>
            """);

        Assert.Equal(["a", "b"], sessions.All!.Select(s => s.Key));
        Assert.Null(sessions.Unreadable);
    }

    [Fact]
    public void A_reply_without_a_current_session_is_a_session_that_has_ended()
    {
        // What a live vCenter answers on a cookie it has logged out: the call
        // is answered and the session simply is not there.
        var sessions = VsphereSessions.Parse("""
            <RetrievePropertiesExResponse xmlns="urn:vim25">
              <returnval><objects><obj type="SessionManager">SessionManager</obj></objects></returnval>
            </RetrievePropertiesExResponse>
            """);

        Assert.Null(sessions.Current);
    }
}