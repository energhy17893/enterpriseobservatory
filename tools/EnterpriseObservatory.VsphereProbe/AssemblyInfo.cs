using System.Runtime.CompilerServices;

// So the unit tests can reach InventoryTiming.Summarize directly -- the
// aggregation is the thing worth proving against a recorded list of calls,
// not Program's argument parsing.
[assembly: InternalsVisibleTo("EnterpriseObservatory.VsphereProbe.Tests")]
