using System.Text;
using System.Text.Json;
using AIDesignStudio.Application.DesignContracts;
using AIDesignStudio.Application.Payloads;
using AIDesignStudio.Infrastructure.Schemas;
using Shouldly;

namespace AIDesignStudio.UnitTests;

/// <summary>
/// The API's validator against the corpus the studio's validator is recorded in.
///
/// <para><c>packages/contracts/conformance/design-contract.cases.json</c> holds
/// 150-odd documents and what Ajv says about each; the contracts package fails if
/// that file stops matching Ajv. This class fails if
/// <see cref="JsonSchemaValidator"/> — loaded from the schema embedded in
/// Infrastructure, the one the API uses — disagrees on any of them, by verdict or
/// by which instance broke which keyword.</para>
///
/// <para>This is the evidence for "the API and the studio enforce the same
/// contract definition". Without it, that sentence would rest on two
/// implementations of a specification happening to coincide.</para>
/// </summary>
public sealed class SchemaConformanceTests
{
    private static readonly JsonSchemaValidator Validator = JsonSchemaValidator.Load(EmbeddedSchema());

    public static TheoryData<string, string, bool, string[]> Cases()
    {
        var data = new TheoryData<string, string, bool, string[]>();

        foreach (var entry in Corpus().RootElement.GetProperty("cases").EnumerateArray())
        {
            data.Add(
                entry.GetProperty("name").GetString()!,
                entry.GetProperty("json").GetString()!,
                entry.GetProperty("valid").GetBoolean(),
                [.. entry.GetProperty("failures").EnumerateArray().Select(f => f.GetString()!)]);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_api_validator_gives_every_case_the_verdict_ajv_gives(
        string name, string json, bool valid, string[] failures)
    {
        using var document = JsonDocument.Parse(json);

        var result = Validator.Validate(document.RootElement);

        var actual = result.Items
            .Select(v => $"{v.Pointer} {v.Keyword}")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();

        actual.ShouldBe(failures.Order(StringComparer.Ordinal).ToArray(), name);
        (result.Items.Count == 0).ShouldBe(valid, name);
        result.Truncated.ShouldBeFalse(name);
    }

    /// <summary>
    /// A theory over an empty file passes vacuously, and so would one that lost
    /// most of its cases to a path mistake.
    /// </summary>
    [Fact]
    public void The_corpus_is_the_real_one()
    {
        using var corpus = Corpus();
        var cases = corpus.RootElement.GetProperty("cases");

        corpus.RootElement.GetProperty("schema").GetString().ShouldBe("design-contract.schema.json");
        cases.GetArrayLength().ShouldBeGreaterThanOrEqualTo(150);
        cases.EnumerateArray().Count(c => c.GetProperty("valid").GetBoolean()).ShouldBeGreaterThanOrEqualTo(20);
    }

    private static JsonDocument Corpus()
    {
        using var stream = typeof(SchemaConformanceTests).Assembly
            .GetManifestResourceStream("Conformance.design-contract.cases.json")
            ?? throw new InvalidOperationException("The conformance corpus is not embedded in the test assembly.");

        return JsonDocument.Parse(stream);
    }

    internal static string EmbeddedSchema()
    {
        using var stream = typeof(DesignContractPayloadValidator).Assembly
            .GetManifestResourceStream(DesignContractPayloadValidator.SchemaResourceName)
            ?? throw new InvalidOperationException("The contract schema is not embedded in Infrastructure.");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// The validator's own rules: what it refuses to load, and the few semantics
/// worth stating directly rather than only through the corpus.
/// </summary>
public sealed class JsonSchemaValidatorTests
{
    private static string[] Failures(string schema, string instance)
    {
        using var document = JsonDocument.Parse(instance);

        return [.. JsonSchemaValidator.Load(schema).Validate(document.RootElement).Items
            .Select(v => $"{v.Pointer} {v.Keyword}")];
    }

    // --- It fails closed ---------------------------------------------------

    /// <summary>
    /// Ignoring a keyword it does not implement would accept documents the
    /// studio rejects. Each of these is valid JSON Schema; the point is that
    /// support is added on purpose, never assumed.
    /// </summary>
    [Theory]
    [InlineData("""{"minimum":1}""")]
    [InlineData("""{"maxItems":3}""")]
    [InlineData("""{"format":"email"}""")]
    [InlineData("""{"oneOf":[{"type":"string"}]}""")]
    [InlineData("""{"patternProperties":{"^x":{}}}""")]
    [InlineData("""{"properties":{"a":{"exclusiveMaximum":2}}}""")]
    [InlineData("""{"$defs":{"unused":{"multipleOf":2}}}""")]
    [InlineData("""{"type":["string","null"]}""")]
    [InlineData("""{"type":"text"}""")]
    [InlineData("""{"items":false}""")]
    [InlineData("""{"$ref":"https://example.test/other.json"}""")]
    [InlineData("""{"$ref":"#/$defs/missing"}""")]
    [InlineData("""{"$ref":"#/properties/a"}""")]
    [InlineData("""{"minLength":-1}""")]
    [InlineData("""{"minLength":1.5}""")]
    [InlineData("""{"properties":{"a":{"$defs":{"b":{}}}}}""")]
    public void Loading_a_schema_that_uses_something_unimplemented_is_refused(string schema)
    {
        Should.Throw<NotSupportedException>(() => JsonSchemaValidator.Load(schema));
    }

    [Fact]
    public void Both_product_schemas_keywords_are_implemented()
    {
        // The contract schema is embedded; loading it is what the API does at
        // first use, so a keyword it cannot handle would surface as a 500.
        Should.NotThrow(() => JsonSchemaValidator.Load(SchemaConformanceTests.EmbeddedSchema()));
    }

    // --- Patterns ------------------------------------------------------------

    /// <summary>
    /// Constructs that mean different things in .NET and in ECMA-262. The
    /// translator refuses them rather than produce a pattern that compiles and
    /// quietly matches a different set of strings.
    /// </summary>
    [Theory]
    [InlineData(@"^\d+$")]
    [InlineData(@"^\w+$")]
    [InlineData(@"\s")]
    [InlineData(@"\bword\b")]
    [InlineData(@"^a.c$")]
    [InlineData(@"(?<name>a)")]
    [InlineData(@"(?=a)b")]
    [InlineData(@"(?i)abc")]
    [InlineData(@"[]a]")]
    [InlineData(@"[^]a]")]
    [InlineData(@"[a-z-[aeiou]]")]
    [InlineData(@"\p{L}")]
    [InlineData(@"(a)\1")]
    [InlineData(@"[abc")]
    [InlineData(@"abc\")]
    public void A_pattern_whose_meaning_differs_between_dialects_is_refused(string pattern)
    {
        Should.Throw<NotSupportedException>(() => JsonSchemaValidator.TranslateEcmaPattern(pattern));
    }

    [Theory]
    [InlineData("^[a-z0-9]+(?:[-/][a-z0-9]+)*$")]
    [InlineData("^[1-9]00$|^(normal|bold)$")]
    [InlineData("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    [InlineData(@"^(?:0|[0-9]*\.?[0-9]+(?:px|rem|em|%|vh|vw))$")]
    public void Every_pattern_in_the_product_schemas_translates(string pattern)
    {
        Should.NotThrow(() => JsonSchemaValidator.TranslateEcmaPattern(pattern));
    }

    /// <summary>
    /// The reason <c>$</c> is rewritten. Left alone, .NET accepts a key with a
    /// newline on the end — which the studio rejects, and which would then be a
    /// "stable key" that is not the string anyone typed.
    /// </summary>
    [Theory]
    [InlineData("\"cart\"", true)]
    [InlineData("\"cart\\n\"", false)]
    [InlineData("\"cart\\r\\n\"", false)]
    [InlineData("\"\\ncart\"", false)]
    public void An_end_anchor_means_the_end_of_the_text(string instance, bool valid)
    {
        Failures("""{"pattern":"^[a-z]+$"}""", instance).Length.ShouldBe(valid ? 0 : 1);
    }

    [Fact]
    public void An_alternation_anchors_each_branch()
    {
        const string schema = """{"pattern":"^[1-9]00$|^(normal|bold)$"}""";

        Failures(schema, "\"400\"").ShouldBeEmpty();
        Failures(schema, "\"bold\"").ShouldBeEmpty();
        Failures(schema, "\"400\\n\"").ShouldBe([" pattern"]);
        Failures(schema, "\"bolder\"").ShouldBe([" pattern"]);
        Failures(schema, "\"4000\"").ShouldBe([" pattern"]);
    }

    // --- Semantics ----------------------------------------------------------

    [Theory]
    [InlineData("1", true)]
    [InlineData("1.0", true)]
    [InlineData("1e0", true)]
    [InlineData("10e-1", true)]
    [InlineData("1.00000000000000000000", true)]
    [InlineData("1.0000001", false)]
    [InlineData("2", false)]
    [InlineData("\"1\"", false)]
    [InlineData("true", false)]
    public void A_constant_number_is_compared_by_value_not_by_spelling(string instance, bool valid)
    {
        Failures("""{"const":1}""", instance).Length.ShouldBe(valid ? 0 : 1);
    }

    [Theory]
    [InlineData("[1, 1.0]", false)]
    [InlineData("[1, 1e0]", false)]
    [InlineData("[1, \"1\"]", true)]
    [InlineData("""[{"a":1,"b":2},{"b":2,"a":1}]""", false)]
    [InlineData("""[{"a":1},{"a":2}]""", true)]
    [InlineData("[[1,2],[2,1]]", true)]
    [InlineData("[[1,2],[1,2]]", false)]
    [InlineData("[null, false, 0, \"\"]", true)]
    public void Unique_items_compares_values_as_json_does(string instance, bool valid)
    {
        Failures("""{"uniqueItems":true}""", instance).ShouldBe(valid ? [] : [" uniqueItems"]);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("1.0", true)]
    [InlineData("-3e2", true)]
    [InlineData("1.5", false)]
    [InlineData("\"1\"", false)]
    public void An_integer_is_a_number_with_no_fractional_part(string instance, bool valid)
    {
        Failures("""{"type":"integer"}""", instance).Length.ShouldBe(valid ? 0 : 1);
    }

    /// <summary>
    /// A keyword for one kind of value says nothing about another. Reporting
    /// "too short" for a number would be noise beside the real error.
    /// </summary>
    [Fact]
    public void A_keyword_applies_only_to_the_kind_of_value_it_describes()
    {
        const string schema = """{"type":"string","minLength":3,"pattern":"^a"}""";

        Failures(schema, "5").ShouldBe([" type"]);
        Failures(schema, "\"b\"").ShouldBe([" minLength", " pattern"]);
    }

    [Fact]
    public void A_pointer_escapes_the_characters_that_delimit_it()
    {
        Failures(
                """{"additionalProperties":{"type":"string"}}""",
                """{"a/b":1,"c~d":2,"":3}""")
            .ShouldBe(["/a~1b type", "/c~0d type", "/ type"]);
    }

    [Fact]
    public void A_definition_may_refer_to_itself()
    {
        const string schema = """
            {
              "$ref": "#/$defs/node",
              "$defs": {
                "node": {
                  "type": "object",
                  "required": ["name"],
                  "additionalProperties": false,
                  "properties": {
                    "name": { "type": "string" },
                    "children": { "type": "array", "items": { "$ref": "#/$defs/node" } }
                  }
                }
              }
            }
            """;

        Failures(schema, """{"name":"a","children":[{"name":"b","children":[{"nome":"c"}]}]}""")
            .ShouldBe(["/children/0/children/0 required", "/children/0/children/0 additionalProperties"]);
    }

    /// <summary>
    /// One rule broken a hundred thousand times is one finding. Unbounded, the
    /// response to a hostile document would be as large as the attacker liked.
    /// </summary>
    [Fact]
    public void Violations_are_capped_and_the_cap_is_reported()
    {
        var instance = $"[{string.Join(',', Enumerable.Repeat("1", 100_000))}]";
        using var document = JsonDocument.Parse(instance);

        var result = JsonSchemaValidator.Load("""{"items":{"type":"string"}}""").Validate(document.RootElement);

        result.Items.Count.ShouldBe(JsonSchemaValidator.MaxViolations);
        result.Truncated.ShouldBeTrue();
    }

    /// <summary>
    /// 200,000 items would be 20 billion comparisons pairwise. Finishing at all
    /// is the assertion; the generous bound only guards against a regression to
    /// quadratic time.
    /// </summary>
    [Fact]
    public void Unique_items_does_not_compare_every_pair()
    {
        var instance = $"[{string.Join(',', Enumerable.Range(0, 200_000))}]";
        using var document = JsonDocument.Parse(instance);
        var validator = JsonSchemaValidator.Load("""{"uniqueItems":true}""");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = validator.Validate(document.RootElement);
        watch.Stop();

        result.Items.ShouldBeEmpty();
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }
}

/// <summary>
/// What the API accepts as a contract payload: the schema, plus what the store
/// needs, plus the one referential rule (AC-2.6, AC-2.7).
/// </summary>
public sealed class DesignContractPayloadValidatorTests
{
    private const string Minimal = """
        {
          "schemaVersion": 1,
          "product": { "name": "Acme", "platforms": ["web"] },
          "informationArchitecture": {
            "flows": [{ "key": "checkout", "name": "Checkout", "steps": [{ "screenKey": "cart" }] }]
          },
          "screens": [{ "key": "cart", "title": "Cart", "purpose": "Review items." }]
        }
        """;

    private static readonly DesignContractPayloadValidator Validator = new();

    private static PayloadValidationResult Validate(string json) =>
        Validator.Validate(Encoding.UTF8.GetBytes(json));

    private static string[] Failures(PayloadValidationResult result) =>
        [.. result.Violations.Select(v => $"{v.Pointer} {v.Keyword}")];

    [Fact]
    public void A_valid_contract_is_accepted_and_returned_in_one_spelling()
    {
        var result = Validate(Minimal.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1.0", StringComparison.Ordinal));

        result.Outcome.ShouldBe(PayloadValidationOutcome.Valid);
        result.Violations.ShouldBeEmpty();
        // Single quotes stand in for double ones so the expectation stays readable.
        const string expected =
            "{'schemaVersion':1,'product':{'name':'Acme','platforms':['web']}," +
            "'informationArchitecture':{'flows':[{'key':'checkout','name':'Checkout'," +
            "'steps':[{'screenKey':'cart'}]}]},'screens':[{'key':'cart','title':'Cart'," +
            "'purpose':'Review items.'}]}";

        result.CanonicalJson.ShouldBe(expected.Replace('\'', '"'));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not json")]
    [InlineData("{\"schemaVersion\":1,}")]
    [InlineData("// a comment\n{}")]
    [InlineData("{} {}")]
    [InlineData("\uFEFF{}x")]
    public void A_body_that_is_not_one_json_document_is_refused_as_such(string body)
    {
        var result = Validate(body);

        result.Outcome.ShouldBe(PayloadValidationOutcome.NotJson);
        result.CanonicalJson.ShouldBeNull();
        result.Violations.ShouldHaveSingleItem().Keyword.ShouldBe("json");
    }

    [Fact]
    public void Nesting_deeper_than_any_contract_needs_is_refused_by_the_parser()
    {
        var deep = $"{new string('[', 40)}{new string(']', 40)}";

        Validate(deep).Outcome.ShouldBe(PayloadValidationOutcome.NotJson);
    }

    [Fact]
    public void A_schema_violation_is_reported_by_pointer_and_keyword()
    {
        var result = Validate(Minimal
            .Replace("\"name\": \"Acme\"", "\"name\": \"\"", StringComparison.Ordinal)
            .Replace("\"key\": \"cart\"", "\"key\": \"My Cart\"", StringComparison.Ordinal));

        result.Outcome.ShouldBe(PayloadValidationOutcome.SchemaInvalid);
        result.CanonicalJson.ShouldBeNull();
        Failures(result).ShouldBe(["/product/name minLength", "/screens/0/key pattern"]);
    }

    /// <summary>
    /// AC-2.7. The schema is satisfied — the conformance corpus pins that — so
    /// this is a different outcome, and the API answers it with 422 not 400.
    /// </summary>
    [Fact]
    public void A_flow_step_naming_an_unlisted_screen_is_a_reference_failure_not_a_schema_one()
    {
        var result = Validate(Minimal.Replace(
            "[{ \"screenKey\": \"cart\" }]",
            "[{ \"screenKey\": \"cart\" }, { \"screenKey\": \"checkout/payment\" }]",
            StringComparison.Ordinal));

        result.Outcome.ShouldBe(PayloadValidationOutcome.ReferenceInvalid);
        result.CanonicalJson.ShouldBeNull();

        var violation = result.Violations.ShouldHaveSingleItem();
        violation.Pointer.ShouldBe("/informationArchitecture/flows/0/steps/1/screenKey");
        violation.Keyword.ShouldBe(DesignContractReferences.Keyword);
        violation.Message.ShouldContain("checkout/payment");
    }

    [Fact]
    public void A_schema_failure_is_reported_before_any_reference_is_examined()
    {
        var result = Validate(Minimal
            .Replace("\"title\": \"Cart\"", "\"title\": \"\"", StringComparison.Ordinal)
            .Replace("\"screenKey\": \"cart\"", "\"screenKey\": \"elsewhere\"", StringComparison.Ordinal));

        result.Outcome.ShouldBe(PayloadValidationOutcome.SchemaInvalid);
        Failures(result).ShouldBe(["/screens/0/title minLength"]);
    }

    // --- Stricter than the schema, because of the store ---------------------
    // PostgreSQL jsonb keeps one member per name, refuses U+0000 and unpaired
    // surrogates, and holds numbers as numeric. Each of these passes the schema
    // in the studio's validator; unrefused, each would be a 500 at SaveChanges
    // or a stored value that is not the one that was validated.

    /// <summary>
    /// The store keeps the last duplicate. Here the last <c>screens</c> is the
    /// invalid one, so a validator reading the first would accept a document
    /// whose stored form breaks the schema.
    /// </summary>
    [Fact]
    public void A_duplicated_property_is_refused_rather_than_resolved_one_way_or_the_other()
    {
        var body = Minimal.TrimEnd().TrimEnd('}') + ", \"screens\": [] }";

        var result = Validate(body);

        result.Outcome.ShouldBe(PayloadValidationOutcome.SchemaInvalid);
        Failures(result).ShouldBe([" duplicateProperty"]);
    }

    [Fact]
    public void A_duplicate_is_found_at_any_depth()
    {
        var body = Minimal.Replace(
            "\"name\": \"Acme\"", "\"name\": \"Acme\", \"name\": \"Other\"", StringComparison.Ordinal);

        Failures(Validate(body)).ShouldBe(["/product duplicateProperty"]);
    }

    [Theory]
    [InlineData("Ac\\u0000me")]
    [InlineData("\\u0000")]
    public void A_nul_character_is_refused_because_it_cannot_be_stored(string name)
    {
        var result = Validate(Minimal.Replace("\"name\": \"Acme\"", $"\"name\": \"{name}\"", StringComparison.Ordinal));

        result.Outcome.ShouldBe(PayloadValidationOutcome.SchemaInvalid);
        Failures(result).ShouldBe(["/product/name encoding"]);
    }

    [Theory]
    [InlineData("\\uD83D")]
    [InlineData("x\\uDE00")]
    [InlineData("\\uDE00\\uD83D")]
    public void Half_a_surrogate_pair_is_refused_as_text_that_is_not_unicode(string name)
    {
        var result = Validate(Minimal.Replace("\"name\": \"Acme\"", $"\"name\": \"{name}\"", StringComparison.Ordinal));

        result.Outcome.ShouldBe(PayloadValidationOutcome.SchemaInvalid);
        Failures(result).ShouldBe(["/product/name encoding"]);
    }

    [Fact]
    public void A_whole_surrogate_pair_is_ordinary_text()
    {
        var result = Validate(Minimal.Replace("\"name\": \"Acme\"", "\"name\": \"\\uD83D\\uDE00\"", StringComparison.Ordinal));

        result.Outcome.ShouldBe(PayloadValidationOutcome.Valid);
    }

    [Fact]
    public void Bytes_that_are_not_utf8_are_refused()
    {
        var bytes = Encoding.UTF8.GetBytes(Minimal);
        var at = Encoding.UTF8.GetString(bytes).IndexOf("Acme", StringComparison.Ordinal);
        bytes[at] = 0xFF;

        var result = Validator.Validate(bytes);

        result.IsValid.ShouldBeFalse();
        result.CanonicalJson.ShouldBeNull();
    }

    /// <summary>
    /// Parsing these "succeeds" by rounding — to zero, or to one. The last would
    /// otherwise satisfy <c>const: 1</c> and be stored as a number nobody sent.
    /// </summary>
    [Theory]
    [InlineData("1e400")]
    [InlineData("-1e400")]
    [InlineData("1e-400")]
    [InlineData("1.00000000000000000000000000000000001")]
    public void A_number_that_cannot_be_held_exactly_is_refused(string number)
    {
        var result = Validate(Minimal.Replace("\"schemaVersion\": 1", $"\"schemaVersion\": {number}", StringComparison.Ordinal));

        result.Outcome.ShouldBe(PayloadValidationOutcome.SchemaInvalid);
        Failures(result).ShouldBe(["/schemaVersion number"]);
    }

    [Fact]
    public void A_hostile_document_gets_a_bounded_answer()
    {
        var screens = string.Join(',', Enumerable.Repeat("""{"key":"BAD"}""", 5_000));
        var result = Validate(Minimal.Replace(
            "[{ \"key\": \"cart\", \"title\": \"Cart\", \"purpose\": \"Review items.\" }]",
            $"[{screens}]",
            StringComparison.Ordinal));

        result.Outcome.ShouldBe(PayloadValidationOutcome.SchemaInvalid);
        result.Violations.Count.ShouldBe(JsonSchemaValidator.MaxViolations);
        result.Truncated.ShouldBeTrue();
    }
}

public sealed class DesignContractReferencesTests
{
    private static IReadOnlyList<PayloadViolation> Find(string json)
    {
        using var document = JsonDocument.Parse(json);
        return DesignContractReferences.FindUnknownScreenReferences(document.RootElement);
    }

    [Fact]
    public void Every_unresolved_step_is_reported_with_its_own_pointer()
    {
        var found = Find("""
            {
              "screens": [{ "key": "home" }, { "key": "cart" }],
              "informationArchitecture": { "flows": [
                { "steps": [{ "screenKey": "home" }, { "screenKey": "lost" }] },
                { "steps": [{ "screenKey": "cart" }] },
                { "steps": [{ "screenKey": "also-lost" }, { "screenKey": "lost" }] }
              ] }
            }
            """);

        found.Select(v => v.Pointer).ShouldBe([
            "/informationArchitecture/flows/0/steps/1/screenKey",
            "/informationArchitecture/flows/2/steps/0/screenKey",
            "/informationArchitecture/flows/2/steps/1/screenKey",
        ]);
    }

    /// <summary>Keys are compared exactly; a near miss is a miss.</summary>
    [Theory]
    [InlineData("Home")]
    [InlineData("home ")]
    [InlineData("hom")]
    public void A_key_that_merely_resembles_a_listed_one_does_not_resolve(string screenKey)
    {
        Find($$"""
            {
              "screens": [{ "key": "home" }],
              "informationArchitecture": { "flows": [{ "steps": [{ "screenKey": "{{screenKey}}" }] }] }
            }
            """).Count.ShouldBe(1);
    }

    /// <summary>
    /// Shape is the schema's business. Given something unexpected this reports
    /// nothing instead of throwing, so it can never turn a 400 into a 500.
    /// </summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"screens":"none","informationArchitecture":{"flows":"none"}}""")]
    [InlineData("""{"screens":[1,null],"informationArchitecture":{"flows":[1,{"steps":7},{"steps":[1,{"screenKey":2}]}]}}""")]
    public void An_unexpected_shape_yields_no_finding_and_no_exception(string json)
    {
        Find(json).ShouldBeEmpty();
    }
}
