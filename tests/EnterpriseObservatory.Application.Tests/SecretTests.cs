using System.ComponentModel;
using System.Text.Json;
using EnterpriseObservatory.Application.Security;

namespace EnterpriseObservatory.Application.Tests;

/// <summary>
/// The type-system half of ADR-0010.
/// </summary>
/// <remarks>
/// The previous product's DPAPI worked and a password still sat in plaintext on
/// disk for years: it had been serialised inside a free-text JSON field the
/// encryption did not cover. These tests pin down the one thing that would have
/// stopped it — a credential that cannot be written down by accident.
/// </remarks>
public class SecretTests
{
    [Fact]
    public void A_secret_prints_as_a_redaction()
    {
        Assert.Equal(Secret.Redaction, Secret.From("hunter2").ToString());
    }

    [Fact]
    public void A_record_containing_a_secret_is_safe_to_print()
    {
        // This is how it leaks when nobody is thinking about it: records
        // generate their own ToString from their members, so an options object
        // logged at startup prints everything in it.
        var printed = new Connection("vc01", Secret.From("hunter2")).ToString();

        Assert.DoesNotContain("hunter2", printed, StringComparison.Ordinal);
        Assert.Contains("vc01", printed, StringComparison.Ordinal);
    }

    [Fact]
    public void Interpolating_a_secret_redacts_it()
    {
        // String interpolation calls ToString, which is the single most likely
        // way a credential reaches a log line.
        var secret = Secret.From("hunter2");

        Assert.DoesNotContain("hunter2", $"connecting with {secret}", StringComparison.Ordinal);
    }

    [Fact]
    public void Serialising_a_secret_throws_rather_than_writing_it()
    {
        // The previous product's actual leak: a credential serialised into a
        // document that was not meant to hold one.
        var error = Assert.Throws<JsonException>(
            () => JsonSerializer.Serialize(new Connection("vc01", Secret.From("hunter2"))));

        Assert.Contains("Reveal()", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_can_still_be_read_from_configuration()
    {
        // Refusing to write is not refusing to arrive. A credential coming in
        // from user secrets or an environment variable is the normal path.
        var recovered = JsonSerializer.Deserialize<Connection>("""{"Name":"vc01","Password":"hunter2"}""");

        Assert.Equal("hunter2", recovered!.Password.Reveal());
    }

    [Fact]
    public void The_type_converter_gives_the_redaction_not_the_value()
    {
        // What a debugger window and a careless Convert.ToString both reach.
        var converter = TypeDescriptor.GetConverter(typeof(Secret));

        Assert.Equal(Secret.Redaction, converter.ConvertToString(Secret.From("hunter2")));
    }

    [Fact]
    public void The_type_converter_binds_a_string_so_configuration_still_works()
    {
        var converter = TypeDescriptor.GetConverter(typeof(Secret));

        var bound = (Secret)converter.ConvertFrom("hunter2")!;

        Assert.Equal("hunter2", bound.Reveal());
    }

    [Fact]
    public void Reveal_gives_the_value_back_unchanged()
    {
        // The escape hatch has to work, or people route around it.
        Assert.Equal("p@ss w<rd&", Secret.From("p@ss w<rd&").Reveal());
    }

    [Fact]
    public void An_absent_secret_is_empty_rather_than_null()
    {
        Assert.True(Secret.Empty.IsEmpty);
        Assert.True(Secret.From(null).IsEmpty);
        Assert.True(Secret.From(string.Empty).IsEmpty);
        Assert.False(Secret.From("x").IsEmpty);
        Assert.Equal(string.Empty, Secret.Empty.Reveal());
    }

    [Fact]
    public void Two_secrets_with_the_same_value_are_equal()
    {
        Assert.Equal(Secret.From("hunter2"), Secret.From("hunter2"));
        Assert.NotEqual(Secret.From("hunter2"), Secret.From("hunter3"));
    }

    [Fact]
    public void The_hash_code_tells_nothing_about_the_value()
    {
        // A hash derived from the value would leak information about it through
        // any dictionary it entered.
        Assert.Equal(Secret.From("hunter2").GetHashCode(), Secret.From("something else").GetHashCode());
    }

    private sealed record Connection(string Name, Secret Password);
}
