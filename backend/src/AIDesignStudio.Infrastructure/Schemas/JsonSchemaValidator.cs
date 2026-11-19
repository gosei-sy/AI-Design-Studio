using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIDesignStudio.Application.Payloads;

namespace AIDesignStudio.Infrastructure.Schemas;

/// <summary>
/// Validates a JSON document against a JSON Schema — for **exactly the
/// keywords this product's schemas use**, and nothing else.
///
/// <para><b>Why this exists instead of a library (M01-05).</b> The widely used
/// .NET validator moved to a paid end-user licence for commercial use, and the
/// alternatives either do not implement draft 2020-12 or bring a code generator
/// and a dependency tree larger than the problem. Both schemas in
/// <c>packages/contracts/schemas</c> use the same nineteen keywords, so the
/// smaller thing to own is a validator for those.</para>
///
/// <para><b>It fails closed.</b> <see cref="Load"/> throws on any keyword,
/// keyword form or regular-expression construct it does not implement. A schema
/// change that needs something new therefore breaks the build's tests, rather
/// than being silently ignored and letting through payloads the studio's
/// validator rejects.</para>
///
/// <para><b>It is pinned to the studio's validator, not merely to the
/// specification.</b> <c>packages/contracts/conformance</c> holds documents with
/// the verdict Ajv gives each one; the contracts package asserts Ajv still agrees
/// with that file, and <c>SchemaConformanceTests</c> asserts this class does. Two
/// validators for one schema can only be trusted while something compares
/// them.</para>
/// </summary>
internal sealed class JsonSchemaValidator
{
    /// <summary>Most violations reported for one document. See <see cref="ViolationSink"/>.</summary>
    public const int MaxViolations = 100;

    private readonly SchemaNode _root;

    private JsonSchemaValidator(SchemaNode root) => _root = root;

    /// <exception cref="NotSupportedException">The schema uses something this class does not implement.</exception>
    public static JsonSchemaValidator Load(string schemaJson)
    {
        using var document = JsonDocument.Parse(schemaJson);

        // Cloned so enum and const values outlive the document.
        return new JsonSchemaValidator(new Compiler(document.RootElement.Clone()).CompileRoot());
    }

    public ViolationSink Validate(JsonElement instance)
    {
        var sink = new ViolationSink(MaxViolations);
        _root.Validate(instance, string.Empty, sink);
        return sink;
    }

    /// <summary>
    /// A bounded collector. A hostile document can break a rule a million times;
    /// reporting the first hundred is as useful and costs a bounded response.
    /// </summary>
    internal sealed class ViolationSink(int limit)
    {
        private readonly List<PayloadViolation> _items = [];

        public IReadOnlyList<PayloadViolation> Items => _items;

        public bool Truncated { get; private set; }

        public bool IsFull => _items.Count >= limit;

        public void Add(string pointer, string keyword, string message)
        {
            if (IsFull)
            {
                Truncated = true;
                return;
            }

            _items.Add(new PayloadViolation(pointer, keyword, message));
        }

        /// <summary>Records that validation stopped early because the sink was full.</summary>
        public void NoteMore() => Truncated = true;
    }

    /// <summary>Appends one reference token to a JSON Pointer (RFC 6901 §3).</summary>
    internal static string Child(string pointer, string token) =>
        $"{pointer}/{token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)}";

    /// <summary>
    /// Rewrites an ECMA-262 pattern — the dialect JSON Schema specifies and the
    /// studio's validator uses — as a .NET one **with identical meaning**, or
    /// refuses.
    ///
    /// <para>The two dialects mostly agree, and the places they do not are the
    /// dangerous kind: a .NET <c>$</c> also matches before a final newline, so
    /// <c>"home\n"</c> would pass a pattern the browser rejects it against;
    /// <c>\d</c>, <c>\w</c> and <c>\s</c> cover different characters; <c>.</c>
    /// excludes different line terminators. Rather than emulate each one, this
    /// accepts only the subset where the dialects are the same — literals,
    /// explicit character classes, groups, alternation, quantifiers and anchors —
    /// translating <c>$</c> to <c>\z</c>, and throws on everything else.</para>
    /// </summary>
    internal static string TranslateEcmaPattern(string pattern)
    {
        var translated = new StringBuilder(pattern.Length + 8);
        var inClass = false;

        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];

            if (ch == '\\')
            {
                if (i + 1 >= pattern.Length)
                {
                    throw UnsupportedPattern(pattern, "a trailing backslash");
                }

                var escaped = pattern[i + 1];

                // An escaped punctuation character is that character in both
                // dialects. An escaped letter or digit is a class, an anchor or
                // a back-reference, and those are where the dialects part.
                if (char.IsAsciiLetterOrDigit(escaped))
                {
                    throw UnsupportedPattern(pattern, $"the escape \\{escaped}");
                }

                translated.Append(ch).Append(escaped);
                i++;
                continue;
            }

            if (inClass)
            {
                if (ch == '[')
                {
                    // .NET reads "[a-z-[aeiou]]" as class subtraction.
                    throw UnsupportedPattern(pattern, "'[' inside a character class");
                }

                inClass = ch != ']';
                translated.Append(ch);
                continue;
            }

            switch (ch)
            {
                case '[':
                    var first = i + 1 < pattern.Length ? pattern[i + 1] : '\0';
                    var second = i + 2 < pattern.Length ? pattern[i + 2] : '\0';

                    // "[]" is an empty class in ECMA-262 and a literal ']' in .NET.
                    if (first == ']' || (first == '^' && second == ']'))
                    {
                        throw UnsupportedPattern(pattern, "a character class beginning with ']'");
                    }

                    inClass = true;
                    translated.Append(ch);
                    break;

                case '$':
                    translated.Append(@"\z");
                    break;

                case '.':
                    throw UnsupportedPattern(pattern, "an unescaped '.'");

                case '(' when i + 1 < pattern.Length && pattern[i + 1] == '?':
                    if (i + 2 >= pattern.Length || pattern[i + 2] != ':')
                    {
                        throw UnsupportedPattern(pattern, "a group other than (...) or (?:...)");
                    }

                    translated.Append(ch);
                    break;

                default:
                    translated.Append(ch);
                    break;
            }
        }

        if (inClass)
        {
            throw UnsupportedPattern(pattern, "an unterminated character class");
        }

        return translated.ToString();
    }

    private static NotSupportedException UnsupportedPattern(string pattern, string construct) => new(
        $"The schema pattern '{pattern}' uses {construct}, which does not mean the same thing in " +
        ".NET and in ECMA-262. JsonSchemaValidator accepts only constructs that are identical in " +
        "both; extend TranslateEcmaPattern deliberately, with conformance cases.");

    /// <summary>
    /// A text key that is equal for two JSON values exactly when the values are
    /// equal as JSON Schema defines it — numbers by value, objects regardless of
    /// member order. Used for <c>uniqueItems</c>, so a long array costs a hash
    /// set rather than a comparison of every pair.
    /// </summary>
    internal static string EqualityKey(JsonElement value)
    {
        var builder = new StringBuilder();
        AppendEqualityKey(value, builder);
        return builder.ToString();
    }

    private static void AppendEqualityKey(JsonElement value, StringBuilder builder)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                builder.Append('s').Append(JsonEncodedText.Encode(value.GetString()!).ToString()).Append(';');
                break;

            case JsonValueKind.Number:
                builder.Append('n');
                builder.Append(value.TryGetDecimal(out var number)
                    ? (number / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture)
                    : value.GetRawText());
                builder.Append(';');
                break;

            case JsonValueKind.Array:
                builder.Append('[');

                foreach (var item in value.EnumerateArray())
                {
                    AppendEqualityKey(item, builder);
                }

                builder.Append(']');
                break;

            case JsonValueKind.Object:
                builder.Append('{');

                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    builder.Append(JsonEncodedText.Encode(property.Name).ToString()).Append(':');
                    AppendEqualityKey(property.Value, builder);
                }

                builder.Append('}');
                break;

            default:
                builder.Append(value.ValueKind switch
                {
                    JsonValueKind.True => "t;",
                    JsonValueKind.False => "f;",
                    _ => "z;",
                });
                break;
        }
    }

    // -----------------------------------------------------------------------
    // Compilation
    // -----------------------------------------------------------------------

    private sealed class Compiler(JsonElement root)
    {
        /// <summary>Keywords that say something about a schema without constraining a value.</summary>
        private static readonly HashSet<string> Annotations =
            new(StringComparer.Ordinal) { "$schema", "$id", "title", "description", "default" };

        private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
        {
            "object", "array", "string", "number", "integer", "boolean", "null",
        };

        private readonly Dictionary<string, SchemaNode> _definitions = new(StringComparer.Ordinal);

        public SchemaNode CompileRoot()
        {
            var node = Compile(root, "#");

            // Every definition, referenced or not: an unsupported keyword in an
            // unused one is still a schema this class cannot vouch for.
            if (root.TryGetProperty("$defs", out var definitions))
            {
                foreach (var definition in definitions.EnumerateObject())
                {
                    Resolve($"#/$defs/{definition.Name}", "#");
                }
            }

            return node;
        }

        private SchemaNode Compile(JsonElement schema, string location)
        {
            if (schema.ValueKind != JsonValueKind.Object)
            {
                throw Unsupported(location, "a schema that is not an object");
            }

            var node = new SchemaNode();

            foreach (var keyword in schema.EnumerateObject())
            {
                var at = $"{location}/{keyword.Name}";
                var value = keyword.Value;

                switch (keyword.Name)
                {
                    case "$defs":
                        if (location != "#")
                        {
                            throw Unsupported(at, "$defs anywhere but the schema root");
                        }

                        break;

                    case "$ref":
                        node.Reference = Resolve(RequireString(value, at), at);
                        break;

                    case "type":
                        var type = RequireString(value, at);

                        if (!Types.Contains(type))
                        {
                            throw Unsupported(at, $"the type '{type}'");
                        }

                        node.Type = type;
                        break;

                    case "const":
                        node.Const = value;
                        break;

                    case "enum":
                        Require(value.ValueKind == JsonValueKind.Array, at, "an enum that is not an array");
                        node.Enum = [.. value.EnumerateArray()];
                        break;

                    case "required":
                        Require(value.ValueKind == JsonValueKind.Array, at, "a required that is not an array");
                        node.Required = [.. value.EnumerateArray().Select(name => RequireString(name, at))];
                        break;

                    case "properties":
                        Require(value.ValueKind == JsonValueKind.Object, at, "a properties that is not an object");
                        node.Properties = value.EnumerateObject().ToDictionary(
                            property => property.Name,
                            property => Compile(property.Value, Child(at, property.Name)),
                            StringComparer.Ordinal);
                        break;

                    case "additionalProperties":
                        switch (value.ValueKind)
                        {
                            case JsonValueKind.False:
                                node.AdditionalPropertiesForbidden = true;
                                break;
                            case JsonValueKind.True:
                                break;
                            default:
                                node.AdditionalProperties = Compile(value, at);
                                break;
                        }

                        break;

                    case "items":
                        node.Items = Compile(value, at);
                        break;

                    case "minItems":
                        node.MinItems = RequireCount(value, at);
                        break;

                    case "uniqueItems":
                        Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, at, "a non-boolean uniqueItems");
                        node.UniqueItems = value.GetBoolean();
                        break;

                    case "minLength":
                        node.MinLength = RequireCount(value, at);
                        break;

                    case "maxLength":
                        node.MaxLength = RequireCount(value, at);
                        break;

                    case "pattern":
                        var pattern = RequireString(value, at);
                        node.PatternSource = pattern;

                        // NonBacktracking: matching time is linear in the input,
                        // so no value can make a pattern expensive.
                        node.Pattern = new Regex(
                            TranslateEcmaPattern(pattern),
                            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
                        break;

                    default:
                        if (!Annotations.Contains(keyword.Name))
                        {
                            throw Unsupported(at, $"the keyword '{keyword.Name}'");
                        }

                        break;
                }
            }

            return node;
        }

        private SchemaNode Resolve(string reference, string at)
        {
            const string prefix = "#/$defs/";

            if (!reference.StartsWith(prefix, StringComparison.Ordinal) || reference.IndexOf('/', prefix.Length) >= 0)
            {
                throw Unsupported(at, $"the reference '{reference}' (only #/$defs/<name> is implemented)");
            }

            if (_definitions.TryGetValue(reference, out var known))
            {
                return known;
            }

            if (!root.TryGetProperty("$defs", out var definitions) ||
                !definitions.TryGetProperty(reference[prefix.Length..], out var definition))
            {
                throw Unsupported(at, $"the reference '{reference}', which resolves to nothing");
            }

            // Registered before it is compiled, so a definition that refers to
            // itself resolves to the node under construction instead of recursing.
            var placeholder = new SchemaNode();
            _definitions[reference] = placeholder;
            placeholder.Reference = Compile(definition, reference);

            return placeholder;
        }

        private static string RequireString(JsonElement value, string at)
        {
            Require(value.ValueKind == JsonValueKind.String, at, "a value that should be a string and is not");
            return value.GetString()!;
        }

        private static int RequireCount(JsonElement value, string at)
        {
            Require(
                value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) && count >= 0,
                at,
                "a count that is not a non-negative integer");

            return value.GetInt32();
        }

        private static void Require(bool condition, string at, string what)
        {
            if (!condition)
            {
                throw Unsupported(at, what);
            }
        }

        private static NotSupportedException Unsupported(string at, string what) => new(
            $"The schema uses {what} at {at}, which JsonSchemaValidator does not implement. It supports " +
            "only what the product's schemas need and refuses the rest rather than ignore it; add the " +
            "feature deliberately, with conformance cases in packages/contracts/conformance.");
    }

    // -----------------------------------------------------------------------
    // Evaluation
    // -----------------------------------------------------------------------

    private sealed class SchemaNode
    {
        public SchemaNode? Reference { get; set; }
        public string? Type { get; set; }
        public JsonElement? Const { get; set; }
        public JsonElement[]? Enum { get; set; }
        public string[]? Required { get; set; }
        public Dictionary<string, SchemaNode>? Properties { get; set; }
        public bool AdditionalPropertiesForbidden { get; set; }
        public SchemaNode? AdditionalProperties { get; set; }
        public SchemaNode? Items { get; set; }
        public int? MinItems { get; set; }
        public bool UniqueItems { get; set; }
        public int? MinLength { get; set; }
        public int? MaxLength { get; set; }
        public Regex? Pattern { get; set; }
        public string? PatternSource { get; set; }

        public void Validate(JsonElement value, string pointer, ViolationSink sink)
        {
            if (sink.IsFull)
            {
                sink.NoteMore();
                return;
            }

            Reference?.Validate(value, pointer, sink);

            if (Type is not null && !IsOfType(value, Type))
            {
                sink.Add(pointer, "type", $"must be {Type}");
            }

            if (Const is { } constant && !JsonElement.DeepEquals(value, constant))
            {
                sink.Add(pointer, "const", $"must be equal to {constant.GetRawText()}");
            }

            if (Enum is not null && !Enum.Any(allowed => JsonElement.DeepEquals(value, allowed)))
            {
                sink.Add(pointer, "enum", $"must be one of: {string.Join(", ", Enum.Select(e => e.GetRawText()))}");
            }

            // Every remaining keyword constrains one kind of value and says
            // nothing about the others — a number is not "too short".
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    ValidateString(value.GetString()!, pointer, sink);
                    break;

                case JsonValueKind.Array:
                    ValidateArray(value, pointer, sink);
                    break;

                case JsonValueKind.Object:
                    ValidateObject(value, pointer, sink);
                    break;
            }
        }

        private void ValidateString(string text, string pointer, ViolationSink sink)
        {
            if (MinLength is not null || MaxLength is not null)
            {
                // Characters, not UTF-16 units: an emoji is one character to the
                // specification and to the studio, and two to string.Length.
                var length = text.EnumerateRunes().Count();

                if (MinLength is { } min && length < min)
                {
                    sink.Add(pointer, "minLength", $"must NOT have fewer than {min} characters");
                }

                if (MaxLength is { } max && length > max)
                {
                    sink.Add(pointer, "maxLength", $"must NOT have more than {max} characters");
                }
            }

            if (Pattern is not null && !Pattern.IsMatch(text))
            {
                sink.Add(pointer, "pattern", $"must match pattern \"{PatternSource}\"");
            }
        }

        private void ValidateArray(JsonElement array, string pointer, ViolationSink sink)
        {
            if (MinItems is { } min && array.GetArrayLength() < min)
            {
                sink.Add(pointer, "minItems", $"must NOT have fewer than {min} items");
            }

            if (UniqueItems)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);

                foreach (var item in array.EnumerateArray())
                {
                    if (!seen.Add(EqualityKey(item)))
                    {
                        sink.Add(pointer, "uniqueItems", "must NOT have duplicate items");
                        break;
                    }
                }
            }

            if (Items is null)
            {
                return;
            }

            var index = 0;

            foreach (var item in array.EnumerateArray())
            {
                if (sink.IsFull)
                {
                    sink.NoteMore();
                    return;
                }

                Items.Validate(item, $"{pointer}/{index.ToString(CultureInfo.InvariantCulture)}", sink);
                index++;
            }
        }

        private void ValidateObject(JsonElement instance, string pointer, ViolationSink sink)
        {
            if (Required is not null)
            {
                foreach (var name in Required)
                {
                    if (!instance.TryGetProperty(name, out _))
                    {
                        sink.Add(pointer, "required", $"must have required property '{name}'");
                    }
                }
            }

            foreach (var property in instance.EnumerateObject())
            {
                if (sink.IsFull)
                {
                    sink.NoteMore();
                    return;
                }

                if (Properties is not null && Properties.TryGetValue(property.Name, out var declared))
                {
                    declared.Validate(property.Value, Child(pointer, property.Name), sink);
                }
                else if (AdditionalPropertiesForbidden)
                {
                    sink.Add(pointer, "additionalProperties", $"must NOT have additional property '{property.Name}'");
                }
                else
                {
                    AdditionalProperties?.Validate(property.Value, Child(pointer, property.Name), sink);
                }
            }
        }

        private static bool IsOfType(JsonElement value, string type) => type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            "number" => value.ValueKind == JsonValueKind.Number,

            // 1.0 is an integer: the specification asks about the value, not
            // about how it was written.
            "integer" => value.ValueKind == JsonValueKind.Number && (value.TryGetDecimal(out var number)
                ? number == decimal.Truncate(number)
                : value.TryGetDouble(out var approximate) && double.IsFinite(approximate) &&
                  Math.Floor(approximate) == approximate),

            _ => false,
        };
    }
}
