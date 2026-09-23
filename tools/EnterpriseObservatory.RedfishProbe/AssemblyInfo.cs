using System.Runtime.CompilerServices;

// So the unit tests can reach RedfishReport, SimplivityReport and ShapeDump
// directly -- the parsers are the thing M6.0b asks to be proven against
// recorded sample JSON, not Program's argument parsing.
[assembly: InternalsVisibleTo("EnterpriseObservatory.RedfishProbe.Tests")]
