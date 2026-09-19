using EnterpriseObservatory.Application.Collection;
using EnterpriseObservatory.Application.Security;

namespace EnterpriseObservatory.Persistence.Sqlite.Tests;

/// <summary>
/// What the store does with a credential, including when it cannot read one.
/// </summary>
public class SourceConnectionStoreTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"eo-conn-{Guid.NewGuid():n}.db");

    private ObservatoryDatabase _database;

    public SourceConnectionStoreTests() => _database = Open();

    private ObservatoryDatabase Open() => new(new SqliteStoreOptions { Path = _path });

    private void Restart()
    {
        _database.Dispose();
        _database = Open();
    }

    /// <summary>Reversible, like the real one, and obviously not cryptography.</summary>
    /// <remarks>
    /// A fake rather than the real protector because these tests are about the
    /// store's behaviour, not about ASP.NET Core's key management. What matters
    /// is that the column holds something other than the password and that the
    /// round trip works — both of which a reversal demonstrates without
    /// dragging a key ring into a unit test.
    /// </remarks>
    private sealed class ReversingProtector : ISecretProtector
    {
        public bool Fail { get; set; }

        public string Protect(Secret secret) =>
            secret.IsEmpty ? string.Empty : new string(secret.Reveal().Reverse().ToArray());

        public Secret Unprotect(string protectedValue)
        {
            if (protectedValue.Length == 0)
            {
                return Secret.Empty;
            }

            return Fail
                ? throw new SecretUnprotectException("no key material here")
                : Secret.From(new string(protectedValue.Reverse().ToArray()));
        }
    }

    private static SourceConnection Vcenter(string password = "hunter2") => new()
    {
        InstanceId = "vc-1",
        Kind = "vsphere",
        BaseAddress = new Uri("https://vc.example.local/"),
        Username = "observatory@vsphere.local",
        Password = Secret.From(password),
        AcceptUntrustedCertificate = true,
        PageSize = 100,
        CreatedUtc = T0,
        CreatedBy = "ertugrul",
        PasswordSetUtc = T0,
    };

    [Fact]
    public void A_connection_survives_a_restart_with_its_password()
    {
        var protector = new ReversingProtector();
        new SqliteSourceConnectionStore(_database, protector).Add(Vcenter());

        Restart();

        var recovered = Assert.Single(new SqliteSourceConnectionStore(_database, protector).All);

        Assert.Equal("vc-1", recovered.InstanceId);
        Assert.Equal(Secret.From("hunter2"), recovered.Password);
        Assert.True(recovered.AcceptUntrustedCertificate);
        Assert.Equal(100, recovered.PageSize);
        Assert.Equal("ertugrul", recovered.CreatedBy);
    }

    [Fact]
    public void The_password_is_not_stored_as_itself()
    {
        // The narrow claim, checked rather than assumed. A protector that
        // silently did nothing would pass every other test in this file.
        new SqliteSourceConnectionStore(_database, new ReversingProtector()).Add(Vcenter());

        var stored = _database.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT password_protected FROM source_connection;";
            return (string)command.ExecuteScalar()!;
        });

        Assert.NotEqual("hunter2", stored);
    }

    [Fact]
    public void A_password_that_cannot_be_decrypted_does_not_stop_the_store_from_loading()
    {
        // The database was restored without the key ring beside it. Refusing
        // to load would take the whole estate's alerting down over one
        // unreadable column — a recoverable mistake turned into an outage.
        var protector = new ReversingProtector();
        new SqliteSourceConnectionStore(_database, protector).Add(Vcenter());

        Restart();
        protector.Fail = true;

        var recovered = Assert.Single(new SqliteSourceConnectionStore(_database, protector).All);

        Assert.True(recovered.PasswordUnreadable);
        Assert.True(recovered.Password.IsEmpty);
        Assert.Equal("vc-1", recovered.InstanceId);
    }

    [Fact]
    public void An_empty_password_on_an_update_keeps_the_stored_one()
    {
        var protector = new ReversingProtector();
        var store = new SqliteSourceConnectionStore(_database, protector);

        store.Add(Vcenter());
        store.Update(Vcenter() with { Password = Secret.Empty, PageSize = 250 });

        Restart();

        var recovered = Assert.Single(new SqliteSourceConnectionStore(_database, protector).All);

        Assert.Equal(Secret.From("hunter2"), recovered.Password);
        Assert.Equal(250, recovered.PageSize);
    }

    [Fact]
    public void An_unreadable_password_is_replaced_rather_than_preserved()
    {
        // The one case where an empty submitted password must NOT mean "keep
        // what is stored": what is stored cannot be read, so preserving it
        // would keep the connection broken forever and the form would appear
        // to do nothing.
        var protector = new ReversingProtector();
        new SqliteSourceConnectionStore(_database, protector).Add(Vcenter());

        Restart();
        protector.Fail = true;

        var store = new SqliteSourceConnectionStore(_database, protector);
        store.Update(Vcenter() with { Password = Secret.From("entered-again") });

        protector.Fail = false;
        Restart();

        var recovered = Assert.Single(new SqliteSourceConnectionStore(_database, protector).All);

        Assert.False(recovered.PasswordUnreadable);
        Assert.Equal(Secret.From("entered-again"), recovered.Password);
    }

    [Fact]
    public void A_removed_connection_stays_removed()
    {
        var protector = new ReversingProtector();
        var store = new SqliteSourceConnectionStore(_database, protector);

        store.Add(Vcenter());
        Assert.True(store.Remove("vc-1"));

        Restart();

        Assert.Empty(new SqliteSourceConnectionStore(_database, protector).All);
    }

    [Fact]
    public void Adding_a_name_that_exists_is_refused_rather_than_overwriting()
    {
        var protector = new ReversingProtector();
        var store = new SqliteSourceConnectionStore(_database, protector);

        Assert.True(store.Add(Vcenter()));
        Assert.False(store.Add(Vcenter("different")));
        Assert.Equal(Secret.From("hunter2"), store.Find("vc-1")!.Password);
    }

    public void Dispose()
    {
        _database.Dispose();

        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        GC.SuppressFinalize(this);
    }
}
