using EnterpriseObservatory.Collectors.Vsphere;

namespace EnterpriseObservatory.Collectors.Vsphere.Tests;

public sealed class VsphereServiceContentTests
{
    private static string Envelope(string about) => $"""
        <soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/">
          <soapenv:Body>
            <RetrieveServiceContentResponse xmlns="urn:vim25"><returnval>
              <rootFolder type="Folder">group-d1</rootFolder>
              <propertyCollector type="PropertyCollector">propertyCollector</propertyCollector>
              <viewManager type="ViewManager">ViewManager</viewManager>
              {about}
              <sessionManager type="SessionManager">SessionManager</sessionManager>
              <perfManager type="PerformanceManager">PerfMgr</perfManager>
            </returnval></RetrieveServiceContentResponse>
          </soapenv:Body>
        </soapenv:Envelope>
        """;

    [Fact]
    public void The_instance_uuid_is_read_from_about()
    {
        var content = VsphereServiceContent.TryParse(Envelope(
            "<about><name>vc</name><apiVersion>8.0.3.0</apiVersion><instanceUuid>4c1d2e3f-0a1b</instanceUuid></about>"));

        Assert.Equal("4c1d2e3f-0a1b", content!.InstanceUuid);
    }

    [Fact]
    public void A_server_that_sends_no_instance_uuid_leaves_it_empty() =>
        Assert.Equal(string.Empty, VsphereServiceContent.TryParse(Envelope("<about><name>vc</name></about>"))!.InstanceUuid);
}
