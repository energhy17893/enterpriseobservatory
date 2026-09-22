namespace EnterpriseObservatory.Collectors.Contract.Tests;

/// <summary>
/// Runs the wire-level collector contract suite against today's vSphere
/// transport, <see cref="EnterpriseObservatory.Collectors.Vsphere.VsphereClient"/>.
/// </summary>
public sealed class VsphereTransportContractTests : TransportContractTests<VsphereTransportContractFixture>;
