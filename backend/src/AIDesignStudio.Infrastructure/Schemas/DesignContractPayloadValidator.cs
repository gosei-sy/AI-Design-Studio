using System.Buffers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AIDesignStudio.Application.DesignContracts;
using AIDesignStudio.Application.Payloads;

namespace AIDesignStudio.Infrastructure.Schemas;

/// <summary>
/// Decides whether a request body may become a design contract version, using
/// <c>packages/contracts/schemas/design-contract.schema.json</c> — the same file
/// the studio validates against, embedded at build time (AC-2.6, AC-2.7).
///
/// <para>Four checks, in this order, and the first that finds anything ends the
/// run:</para>
///
/// <list type="number">
/// <item><b>It parses</b> as one JSON document.</item>
/// <item><b>It can be stored as what it says.</b> PostgreSQL <c>jsonb</c> keeps
/// one member per name, refuses U+0000 and unpaired surrogates, and holds numbers
/// as <c>numeric</c>. A document relying on any of those would either fail at
/// <c>SaveChanges</c> as a 500 or be stored as something other than what was
/// validated, so each is refused here by pointer — stricter than the schema, on
/// purpose.</item>
/// <item><b>It satisfies the schema.</b></item>
/// <item><b>Its references resolve</b> — the rule the schema cannot express.</item>
/// </list>
///
/// <para>What is returned for storage is re-serialized from the one parse that
/// was checked, never the caller's bytes.</para>
/// </summary>
public sealed class DesignContractPayloadValidator : IDesignContractPayloadValidator
{
    internal const string SchemaResourceName = "AIDesignStudio.Schemas.design-contract.schema.json";

    private static readonly Lazy<JsonSchemaValidator> Schema =
        new(() => JsonSchemaValidator.Load(ReadSchema()));

    /// <summary>Nesting the schema never needs; deeper input is refused by the parser.</summary>
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        MaxDepth = 32,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    public PayloadValidationResult Validate(ReadOnlyMemory<byte> utf8Json)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(utf8Json, ParseOptions);
        }
        catch (JsonException exception)
        {
            return new PayloadValidationResult(
                PayloadValidationOutcome.NotJson,
                CanonicalJson: null,
                [new PayloadViolation(string.Empty, "json", exception.Message)],
                Truncated: false);
        }

        using (document)
        {
            var root = document.RootElement;

            var storage = new JsonSchemaValidator.ViolationSink(JsonSchemaValidator.MaxViolations);
            CheckStorable(root, string.Empty, storage);

            if (storage.Items.Count > 0)
            {
                return Refused(PayloadValidationOutcome.SchemaInvalid, storage.Items, storage.Truncated);
            }

            var schema = Schema.Value.Validate(root);

            if (schema.Items.Count > 0)
            {
                return Refused(PayloadValidationOutcome.SchemaInvalid, schema.Items, schema.Truncated);
            }

            var references = DesignContractReferences.FindUnknownScreenReferences(root);

            if (references.Count > 0)
            {
                var truncated = references.Count > JsonSchemaValidator.MaxViolations;

                return Refused(
                    PayloadValidationOutcome.ReferenceInvalid,
                    truncated ? [.. references.Take(JsonSchemaValidator.MaxViolations)] : references,
                    truncated);
            }

            return new PayloadValidationResult(
                PayloadValidationOutcome.Valid, Canonicalize(root), [], Truncated: false);
        }
    }

    private static PayloadValidationResult Refused(
        PayloadValidationOutcome outcome, IReadOnlyList<PayloadViolation> violations, bool truncated) =>
        new(outcome, CanonicalJson: null, violations, truncated);

    /// <summary>
    /// Refuses whatever <c>jsonb</c> would reject or silently change.
    ///
    /// <para>Runs before the schema so the schema validator can read every string
    /// without guarding, and so these are reported as what they are rather than
    /// as whichever schema rule they happen to trip.</para>
    /// </summary>
    private static void CheckStorable(JsonElement value, string pointer, JsonSchemaValidator.ViolationSink sink)
    {
        if (sink.IsFull)
        {
            sink.NoteMore();
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                CheckText(() => value.GetString()!, pointer, sink);
                break;

            case JsonValueKind.Number:
                // TryGetDecimal succeeding is not enough: it rounds. 1e-400
                // parses as 0 and a 35-digit fraction loses its tail, and either
                // would be stored as a different number from the one sent.
                if (!value.TryGetDecimal(out var number) || !IsExactly(value, number))
                {
                    sink.Add(pointer, "number", "is outside the numeric range that can be stored exactly");
                }

                break;

            case JsonValueKind.Array:
                var index = 0;

                foreach (var item in value.EnumerateArray())
                {
                    CheckStorable(item, $"{pointer}/{index}", sink);
                    index++;
                }

                break;

            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);

                foreach (var property in value.EnumerateObject())
                {
                    string name;

                    try
                    {
                        name = property.Name;
                    }
                    catch (InvalidOperationException)
                    {
                        sink.Add(pointer, "encoding", "has a property name that is not valid Unicode text");
                        continue;
                    }

                    if (name.Contains('\0', StringComparison.Ordinal))
                    {
                        sink.Add(pointer, "encoding", "has a property name containing U+0000, which cannot be stored");
                        continue;
                    }

                    if (!names.Add(name))
                    {
                        // A store that keeps the last and a validator that read
                        // the first would disagree about what was accepted.
                        sink.Add(pointer, "duplicateProperty", $"declares property '{name}' more than once");
                        continue;
                    }

                    CheckStorable(property.Value, JsonSchemaValidator.Child(pointer, name), sink);
                }

                break;
        }
    }

    /// <summary>True when <paramref name="parsed"/> is the number the text spells, not a rounding of it.</summary>
    private static bool IsExactly(JsonElement written, decimal parsed)
    {
        using var roundTrip = JsonDocument.Parse(parsed.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return JsonElement.DeepEquals(written, roundTrip.RootElement);
    }

    private static void CheckText(Func<string> read, string pointer, JsonSchemaValidator.ViolationSink sink)
    {
        string text;

        try
        {
            text = read();
        }
        catch (InvalidOperationException)
        {
            // An escape for half a surrogate pair, or bytes that are not UTF-8.
            sink.Add(pointer, "encoding", "is not valid Unicode text");
            return;
        }

        if (text.Contains('\0', StringComparison.Ordinal))
        {
            sink.Add(pointer, "encoding", "contains U+0000, which cannot be stored");
            return;
        }

        // GetString accepts an escaped lone surrogate; PostgreSQL does not.
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                sink.Add(pointer, "encoding", "contains an unpaired surrogate, which is not valid Unicode text");
                return;
            }
        }
    }

    /// <summary>
    /// One spelling of the document: no insignificant whitespace, and numbers in
    /// their shortest exact form, so <c>1.0</c> and <c>1e0</c> are stored as
    /// <c>1</c>. Member order is the caller's; <c>jsonb</c> imposes its own.
    /// </summary>
    private static string Canonicalize(JsonElement root)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(root, writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void Write(JsonElement value, Utf8JsonWriter writer)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();

                foreach (var property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    Write(property.Value, writer);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();

                foreach (var item in value.EnumerateArray())
                {
                    Write(item, writer);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;

            case JsonValueKind.Number:
                // Dividing by one written to 28 places drops trailing zeros.
                writer.WriteNumberValue(value.GetDecimal() / 1.0000000000000000000000000000m);
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static string ReadSchema()
    {
        var assembly = typeof(DesignContractPayloadValidator).GetTypeInfo().Assembly;

        using var stream = assembly.GetManifestResourceStream(SchemaResourceName)
            ?? throw new InvalidOperationException(
                $"The embedded schema '{SchemaResourceName}' is missing. It is linked from " +
                "packages/contracts/schemas in AIDesignStudio.Infrastructure.csproj.");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
