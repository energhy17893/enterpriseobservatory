using EnterpriseObservatory.Application.Security;
using EnterpriseObservatory.Host.AllInOne.Configuration;

namespace EnterpriseObservatory.Host.AllInOne.Tests;

public class VsphereEndpointOptionsTests
{
    private static VsphereEndpointOptions Valid() => new()
    {
        InstanceId = "vc-1",
        BaseAddress = "https://vc01.corp.local",
        Username = "svc-observatory@vsphere.local",
        Password = Secret.From("supplied out of band"),
    };

    [Fact]
    public void A_complete_entry_has_nothing_wrong_with_it()
    {
        Assert.Empty(Valid().Validate(0));
    }

    [Fact]
    public void Http_is_refused_even_though_the_certificate_question_is_separate()
    {
        // Trusting an unverified certificate is a decision someone may
        // legitimately make. Sending the credentials in the clear is not the
        // same decision and is never the right one.
        var options = Valid();
        options.BaseAddress = "http://vc01.corp.local";

        Assert.Contains(options.Validate(0), p => p.Contains("https", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        // Starting a service four times to be told about four missing fields
        // is a small cruelty that costs nothing to avoid.
        var empty = new VsphereEndpointOptions();

        Assert.True(empty.Validate(0).Count >= 4);
    }

    [Fact]
    public void A_missing_password_says_where_to_put_it_instead()
    {
        var options = Valid();
        options.Password = Secret.Empty;

        var problem = Assert.Single(options.Validate(2));

        Assert.Contains("VCenters__2__Password", problem, StringComparison.Ordinal);
        Assert.Contains("not in appsettings.json", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_with_no_instance_id_is_still_identifiable_in_the_message()
    {
        // Naming it by position is the only thing left, and being told
        // "something is wrong somewhere" helps nobody.
        var options = Valid();
        options.InstanceId = string.Empty;

        Assert.All(options.Validate(3), p => Assert.Contains("VCenters[3]", p, StringComparison.Ordinal));
    }

    [Fact]
    public void A_page_size_of_zero_is_refused()
    {
        // It would page forever without ever returning an object.
        var options = Valid();
        options.InventoryPageSize = 0;

        Assert.Contains(options.Validate(0), p => p.Contains("InventoryPageSize", StringComparison.Ordinal));
    }
}
