using System.Globalization;
using System.Text.Json.Nodes;
using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Collectors.Redfish;
using EnterpriseObservatory.Collectors.Redfish.Tests;
using EnterpriseObservatory.Domain;

namespace EnterpriseObservatory.Collectors.Contract.Tests;

/// <summary>
/// Drives the Redfish inventory source (<see cref="RedfishInventorySource"/>)
/// through Kibar's recorded iLO 5. One connection is one server, so its read
/// targets are the drives it walks: <c>hostCount</c> drives behind one
/// controller, and each failure a drive link that answers 404.
/// </summary>
public sealed class RedfishInventoryContractFixture : IInventoryContractFixture
{
    private static readonly EntityId Host = EntityId.For("vc-contract", "host-1");

    public string InstanceId => "ilo-test";

    public IInventorySource CreateHealthy(int hostCount) => Source(Ilo(hostCount, 0));

    public IInventorySource CreateSlow() => Source(new FakeIlo { Delay = TimeSpan.FromSeconds(5) });

    public IInventorySource CreateWithFailures(int hostCount, int failureCount) => Source(Ilo(hostCount, failureCount));

    /// <summary>One per drive read — the annotation carries their count.</summary>
    public IReadOnlyList<string> AcceptedTargets(InventorySnapshot snapshot) =>
    [
        .. snapshot.Annotations.SelectMany(a => Enumerable.Range(
            0, int.Parse(a.Settings["redfish.storage.drives"], CultureInfo.InvariantCulture))
            .Select(i => $"{a.Entity.Value}/drive-{i}")),
    ];

    private static RedfishInventorySource Source(FakeIlo ilo)
    {
        var directory = new FakeDirectory();
        directory.Uuids[ilo.Uuid] = Host;
        return ilo.Source(directory, new TestClock());
    }

    private static FakeIlo Ilo(int drives, int missing)
    {
        var ilo = new FakeIlo();
        const string storage = "/redfish/v1/Systems/1/Storage";
        const string controller = storage + "/C0";
        var recordedDrive = ilo.Docs[storage + "/DE07C000/Drives/0"];

        ilo.Docs[storage] = new JsonObject { ["Members"] = new JsonArray(new JsonObject { ["@odata.id"] = controller }) };

        var links = new JsonArray();
        for (var i = 0; i < drives + missing; i++)
        {
            var path = $"{controller}/Drives/{i}";
            links.Add(new JsonObject { ["@odata.id"] = path });

            if (i < drives)
            {
                ilo.Docs[path] = recordedDrive.DeepClone();
            }
        }

        var controllerDoc = ilo.Docs[storage + "/DE07C000"].DeepClone();
        controllerDoc["Drives"] = links;
        ilo.Docs[controller] = controllerDoc;

        return ilo;
    }
}

/// <summary>Runs the source-level collector contract suite against the Redfish inventory source.</summary>
public sealed class RedfishInventoryContractTests : InventoryContractTests<RedfishInventoryContractFixture>;
