namespace EnterpriseObservatory.Collectors.Contract.Tests;

/// <summary>
/// Runs the source-level collector contract suite against today's vSphere
/// inventory source.
/// </summary>
public sealed class VsphereInventoryContractTests : InventoryContractTests<VsphereInventoryContractFixture>;
