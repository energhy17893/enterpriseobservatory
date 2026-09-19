using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Monitoring;
using EnterpriseObservatory.Application.Security;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// The rules that keep a form from quietly breaking a working collector.
/// </summary>
/// <remarks>
/// Most of these pin behaviour that has no visible symptom when it goes wrong.
/// A connection saved with an erased password does not throw, does not log and
/// does not look different on screen — it simply stops collecting, and the
/// estate goes quiet in a way that reads as healthy.
/// </remarks>
public class SourceConnectionCatalogueTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    /// <summary>An in-memory store, so these tests are about the rules.</summary>
    private sealed class FakeStore : ISourceConnectionStore
    {
        private readonly Dictionary<string, SourceConnection> _rows = new(StringComparer.Ordinal);

        public IReadOnlyList<SourceConnection> All => [.. _rows.Values];

        public SourceConnection? Find(string instanceId) => _rows.GetValueOrDefault(instanceId);

        public bool Add(SourceConnection connection)
        {
            if (_rows.ContainsKey(connection.InstanceId))
            {
                return false;
            }

            _rows[connection.InstanceId] = connection;
            return true;
        }

        public bool Update(SourceConnection connection)
        {
            if (!_rows.TryGetValue(connection.InstanceId, out var existing))
            {
                return false;
            }

            // The same merge the real store performs, because the rule being
            // tested is "an empty password keeps the stored one" and a fake
            // that ignored it would prove the opposite of what it claims.
            _rows[connection.InstanceId] = connection.Password.IsEmpty
                ? connection with { Password = existing.Password }
                : connection;

            return true;
        }

        public bool Remove(string instanceId) => _rows.Remove(instanceId);
    }

    private static SourceConnection Vcenter(string name, string password = "hunter2") => new()
    {
        InstanceId = name,
        Kind = "vsphere",
        BaseAddress = new Uri("https://vc.example.local"),
        Username = "observatory@vsphere.local",
        Password = Secret.From(password),
    };

    private static SourceConnectionCatalogue Catalogue(
        FakeStore store, params SourceConnection[] configured) =>
        new(store, configured, new FixedClock(T0));

    [Fact]
    public void A_connection_can_be_added_and_comes_back()
    {
        var store = new FakeStore();
        var catalogue = Catalogue(store);

        var result = catalogue.Add(Vcenter("vc-1"), "ertugrul");

        Assert.True(result.Applied);
        Assert.Equal("vc-1", Assert.Single(catalogue.All()).InstanceId);
    }

    [Fact]
    public void Adding_records_who_did_it_and_when_the_password_was_set()
    {
        // "When was this last rotated" is a question every audit asks and no
        // product answers, and the answer is impossible to reconstruct later.
        var catalogue = Catalogue(new FakeStore());

        var added = catalogue.Add(Vcenter("vc-1"), "ertugrul").Connection!;

        Assert.Equal("ertugrul", added.CreatedBy);
        Assert.Equal(T0, added.PasswordSetUtc);
    }

    [Fact]
    public void An_invalid_connection_is_refused_with_every_problem_at_once()
    {
        // Being told about three missing fields one at a time is a small
        // cruelty that costs nothing to avoid.
        var catalogue = Catalogue(new FakeStore());

        var result = catalogue.Add(
            Vcenter("vc-1") with
            {
                Username = "",
                Password = Secret.Empty,
                BaseAddress = new Uri("http://vc.example.local"),
            },
            "ertugrul");

        Assert.False(result.Applied);
        Assert.Equal(ConnectionFailure.Invalid, result.Failure);
        Assert.Equal(3, result.Problems.Count);
    }

    [Fact]
    public void An_http_address_is_refused()
    {
        // Not the same question as whether the certificate is trusted: over
        // http the credentials are readable by anyone on the path.
        var catalogue = Catalogue(new FakeStore());

        var result = catalogue.Add(
            Vcenter("vc-1") with { BaseAddress = new Uri("http://vc.example.local") },
            "ertugrul");

        Assert.False(result.Applied);
        Assert.Contains("https", Assert.Single(result.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_with_a_separator_in_it_is_refused()
    {
        // EntityId is built as source:localId and refuses separators. Caught
        // here so it reads as a form error rather than as an exception from
        // three layers down on the first collection cycle.
        var catalogue = Catalogue(new FakeStore());

        var result = catalogue.Add(Vcenter("dc/vc-1"), "ertugrul");

        Assert.False(result.Applied);
        Assert.Equal(ConnectionFailure.Invalid, result.Failure);
    }

    [Fact]
    public void A_name_that_is_already_taken_is_refused()
    {
        var store = new FakeStore();
        var catalogue = Catalogue(store);

        catalogue.Add(Vcenter("vc-1"), "ertugrul");
        var again = catalogue.Add(Vcenter("vc-1"), "ertugrul");

        Assert.False(again.Applied);
        Assert.Equal(ConnectionFailure.NameTaken, again.Failure);
    }

    [Fact]
    public void A_name_a_configured_connection_already_uses_is_refused()
    {
        // Otherwise the stored row sits in the list looking configured and is
        // never polled, because configuration wins. Silent, and indisputably
        // the operator's fault when they eventually find it.
        var catalogue = Catalogue(new FakeStore(), Vcenter("vc-1"));

        var result = catalogue.Add(Vcenter("vc-1"), "ertugrul");

        Assert.False(result.Applied);
        Assert.Equal(ConnectionFailure.NameTaken, result.Failure);
    }

    [Fact]
    public void An_empty_password_on_an_edit_keeps_the_stored_one()
    {
        // The form cannot show a password it is not allowed to read back, so
        // an unchanged form submits an empty one. Taking that at face value
        // would break a working collector by opening a page and saving it.
        var store = new FakeStore();
        var catalogue = Catalogue(store);

        catalogue.Add(Vcenter("vc-1", "hunter2"), "ertugrul");

        var result = catalogue.Update(
            Vcenter("vc-1") with { Password = Secret.Empty, Username = "other@vsphere.local" },
            "ertugrul");

        Assert.True(result.Applied);
        Assert.Equal(Secret.From("hunter2"), store.Find("vc-1")!.Password);
        Assert.Equal("other@vsphere.local", store.Find("vc-1")!.Username);
    }

    [Fact]
    public void An_edit_that_keeps_the_password_does_not_move_the_rotation_date()
    {
        // Otherwise every unrelated edit resets the rotation clock, and the
        // field becomes a record of when someone last opened the form.
        var store = new FakeStore();
        var clock = new FixedClock(T0);
        var catalogue = new SourceConnectionCatalogue(store, [], clock);

        catalogue.Add(Vcenter("vc-1"), "ertugrul");
        clock.UtcNow = T0.AddDays(30);

        var result = catalogue.Update(
            Vcenter("vc-1") with { Password = Secret.Empty, PageSize = 100 }, "ertugrul");

        Assert.Equal(T0, result.Connection!.PasswordSetUtc);
    }

    [Fact]
    public void Changing_the_password_moves_the_rotation_date()
    {
        var store = new FakeStore();
        var clock = new FixedClock(T0);
        var catalogue = new SourceConnectionCatalogue(store, [], clock);

        catalogue.Add(Vcenter("vc-1"), "ertugrul");
        clock.UtcNow = T0.AddDays(30);

        var result = catalogue.Update(Vcenter("vc-1", "rotated"), "ertugrul");

        Assert.Equal(T0.AddDays(30), result.Connection!.PasswordSetUtc);
    }

    [Fact]
    public void A_configured_connection_cannot_be_edited()
    {
        // An edit here would be undone by the next restart, which looks
        // exactly like the product ignoring what it was told.
        var catalogue = Catalogue(new FakeStore(), Vcenter("vc-1"));

        var result = catalogue.Update(Vcenter("vc-1", "different"), "ertugrul");

        Assert.False(result.Applied);
        Assert.Equal(ConnectionFailure.ReadOnly, result.Failure);
    }

    [Fact]
    public void A_configured_connection_cannot_be_removed()
    {
        var catalogue = Catalogue(new FakeStore(), Vcenter("vc-1"));

        var result = catalogue.Remove("vc-1");

        Assert.False(result.Applied);
        Assert.Equal(ConnectionFailure.ReadOnly, result.Failure);
    }

    [Fact]
    public void Configuration_wins_a_name_collision()
    {
        // Whoever deploys the service is the higher authority. A stored row
        // overriding it would make the service behave differently from what it
        // was told, with nothing on either screen to show why.
        var store = new FakeStore();
        store.Add(Vcenter("vc-1") with { Username = "stored@vsphere.local" });

        var catalogue = Catalogue(store, Vcenter("vc-1") with { Username = "configured@vsphere.local" });

        var only = Assert.Single(catalogue.All());

        Assert.Equal("configured@vsphere.local", only.Username);
        Assert.Equal(ConnectionOrigin.Configuration, only.Origin);
    }

    [Fact]
    public void Editing_an_unknown_connection_says_so_rather_than_creating_one()
    {
        var catalogue = Catalogue(new FakeStore());

        var result = catalogue.Update(Vcenter("vc-1"), "ertugrul");

        Assert.False(result.Applied);
        Assert.Equal(ConnectionFailure.NotFound, result.Failure);
    }

    [Fact]
    public void A_removed_connection_is_gone()
    {
        var store = new FakeStore();
        var catalogue = Catalogue(store);

        catalogue.Add(Vcenter("vc-1"), "ertugrul");

        Assert.True(catalogue.Remove("vc-1").Applied);
        Assert.Empty(catalogue.All());
    }
}
