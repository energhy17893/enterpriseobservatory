using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EnterpriseObservatory.Application.Security;

/// <summary>
/// A value that must never be written down.
/// </summary>
/// <remarks>
/// <para>
/// The previous product encrypted its credentials with DPAPI and still leaked
/// one. The mechanism worked; a password was simply serialised inside a
/// free-text JSON field that the encryption did not cover, and nothing said so.
/// ADR-0010 closed the runtime half of that — a credential read from a settings
/// file stops the service. This is the other half: a secret that cannot be
/// written down by accident, because the compiler will not let it.
/// </para>
/// <para>
/// What that means in practice:
/// </para>
/// <list type="bullet">
/// <item>It is not a string, so it cannot be concatenated, interpolated,
/// logged or passed to something that takes a string without saying so.</item>
/// <item><see cref="ToString"/> returns a redaction, so a record containing one
/// stays safe to print — records generate their own <c>ToString</c> from their
/// members, which is exactly how this leaks when nobody thinks about it.</item>
/// <item>Serialising one throws rather than writing it, so it cannot be
/// embedded in a document the way the previous one was.</item>
/// <item>Reading the value takes a call named <see cref="Reveal"/>, which is
/// short enough to use and conspicuous enough to review.</item>
/// </list>
/// <para>
/// This cannot stop someone determined to leak a credential. It stops the
/// accident, which is what actually happened.
/// </para>
/// </remarks>
[JsonConverter(typeof(SecretJsonConverter))]
[TypeConverter(typeof(SecretTypeConverter))]
public readonly struct Secret : IEquatable<Secret>
{
    private readonly string? _value;

    private Secret(string? value) => _value = value;

    /// <summary>What a secret looks like anywhere it should not appear.</summary>
    public const string Redaction = "<redacted>";

    public static Secret Empty => default;

    /// <summary>Whether no value was supplied. Safe to ask; says nothing.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(_value);

    public static Secret From(string? value) => new(value);

    /// <summary>
    /// The value itself.
    /// </summary>
    /// <remarks>
    /// Every call is a place where a credential enters ordinary code and can be
    /// logged, concatenated or stored. There should be very few, and each
    /// should be obvious in a diff — which is the entire reason this is a
    /// method with a name rather than a property.
    /// </remarks>
    public string Reveal() => _value ?? string.Empty;

    /// <summary>Redacted, always.</summary>
    public override string ToString() => Redaction;

    /// <summary>
    /// Compares by value, in constant time.
    /// </summary>
    /// <remarks>
    /// Ordinary string comparison returns as soon as two characters differ, and
    /// how long that takes is measurable. It is unlikely to matter here — these
    /// are compared at configuration time, not per request — but writing the
    /// timing-safe version costs one call and removes the question.
    /// </remarks>
    public bool Equals(Secret other) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(_value ?? string.Empty),
            System.Text.Encoding.UTF8.GetBytes(other._value ?? string.Empty));

    public override bool Equals(object? obj) => obj is Secret other && Equals(other);

    /// <summary>
    /// Deliberately constant.
    /// </summary>
    /// <remarks>
    /// A hash derived from the value would leak information about it through
    /// any dictionary it entered, and a secret has no business being a
    /// dictionary key in the first place.
    /// </remarks>
    public override int GetHashCode() => 0;

    public static bool operator ==(Secret left, Secret right) => left.Equals(right);

    public static bool operator !=(Secret left, Secret right) => !left.Equals(right);
}

/// <summary>Refuses to serialise a secret; reads one without complaint.</summary>
/// <remarks>
/// Asymmetric on purpose. Accepting one from configuration is the normal way a
/// credential arrives; writing one into a document is how the previous
/// product's leak happened.
/// </remarks>
internal sealed class SecretJsonConverter : JsonConverter<Secret>
{
    public override Secret Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Secret.From(reader.GetString());

    public override void Write(Utf8JsonWriter writer, Secret value, JsonSerializerOptions options) =>
        throw new JsonException(
            "A Secret cannot be serialised. If this value genuinely has to be written down, " +
            "call Reveal() at the point where that decision is made, so it appears in review.");

    public override void WriteAsPropertyName(
        Utf8JsonWriter writer, Secret value, JsonSerializerOptions options) =>
        throw new JsonException("A Secret cannot be used as a property name.");
}

/// <summary>Lets the configuration binder turn a string into a secret.</summary>
internal sealed class SecretTypeConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) =>
        value is string text ? Secret.From(text) : base.ConvertFrom(context, culture, value)!;

    /// <summary>
    /// Converts to a string as the redaction, never as the value.
    /// </summary>
    /// <remarks>
    /// This is what a debugger window, a diagnostic dump and a careless
    /// <c>Convert.ToString</c> all end up calling.
    /// </remarks>
    public override object ConvertTo(
        ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType) =>
        destinationType == typeof(string)
            ? Secret.Redaction
            : base.ConvertTo(context, culture, value, destinationType)!;
}
