using System.Diagnostics.CodeAnalysis;

// This project is a wire adapter. Its members are named after the vim25
// operations they build, so that a reader holding VMware's API documentation
// can match them one to one. Renaming RetrievePropertiesEx to
// RetrieveProperties2 to satisfy a naming convention would break the only
// correspondence that matters here, and would leave the next person wondering
// which vim25 call it actually makes.
[assembly: SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Names mirror vim25 operation names exactly; see file header.",
    Scope = "member",
    Target = "~M:EnterpriseObservatory.Collectors.Vsphere.VsphereSoapRequests.RetrievePropertiesEx(System.String,System.String,System.String,System.Collections.Generic.IReadOnlyDictionary{System.String,System.Collections.Generic.IReadOnlyList{System.String}},System.Int32)~System.String")]
[assembly: SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Names mirror vim25 operation names exactly; see file header.",
    Scope = "member",
    Target = "~M:EnterpriseObservatory.Collectors.Vsphere.VsphereSoapRequests.ContinueRetrievePropertiesEx(System.String,System.String)~System.String")]
