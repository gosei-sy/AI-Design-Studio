using System.Text.Json;
using AIDesignStudio.Application.Payloads;

namespace AIDesignStudio.Application.DesignContracts;

/// <summary>
/// Decides whether a request body may become a design contract version
/// (AC-2.6, AC-2.7).
///
/// <para>A port because the mechanism — a JSON Schema loaded from the file the
/// studio also uses — belongs in Infrastructure, while the decision is consumed
/// at the HTTP boundary. The domain treats a payload as opaque text and relies
/// on this having run first.</para>
/// </summary>
public interface IDesignContractPayloadValidator
{
    /// <param name="utf8Json">The request body, exactly as received.</param>
    PayloadValidationResult Validate(ReadOnlyMemory<byte> utf8Json);
}

/// <summary>
/// The referential rule JSON Schema cannot express (AC-2.7): every flow step
/// names a screen that the contract actually lists.
///
/// <para>Application rather than Infrastructure because it is a rule about the
/// product, not about a library, and it needs nothing but the document.</para>
/// </summary>
public static class DesignContractReferences
{
    public const string Keyword = "screenReference";

    /// <summary>
    /// The flow steps whose <c>screenKey</c> is absent from <c>/screens</c>.
    ///
    /// <para>Expects a schema-valid document but does not rely on it: an
    /// unexpected shape yields no finding rather than an exception, because
    /// reporting the shape is the schema's job.</para>
    /// </summary>
    public static IReadOnlyList<PayloadViolation> FindUnknownScreenReferences(JsonElement contract)
    {
        var violations = new List<PayloadViolation>();

        if (contract.ValueKind != JsonValueKind.Object)
        {
            return violations;
        }

        var listed = new HashSet<string>(StringComparer.Ordinal);

        if (contract.TryGetProperty("screens", out var screens) && screens.ValueKind == JsonValueKind.Array)
        {
            foreach (var screen in screens.EnumerateArray())
            {
                if (TryGetString(screen, "key", out var key))
                {
                    listed.Add(key);
                }
            }
        }

        if (!contract.TryGetProperty("informationArchitecture", out var architecture) ||
            architecture.ValueKind != JsonValueKind.Object ||
            !architecture.TryGetProperty("flows", out var flows) ||
            flows.ValueKind != JsonValueKind.Array)
        {
            return violations;
        }

        var flowIndex = 0;

        foreach (var flow in flows.EnumerateArray())
        {
            if (flow.ValueKind == JsonValueKind.Object &&
                flow.TryGetProperty("steps", out var steps) &&
                steps.ValueKind == JsonValueKind.Array)
            {
                var stepIndex = 0;

                foreach (var step in steps.EnumerateArray())
                {
                    if (TryGetString(step, "screenKey", out var screenKey) && !listed.Contains(screenKey))
                    {
                        violations.Add(new PayloadViolation(
                            $"/informationArchitecture/flows/{flowIndex}/steps/{stepIndex}/screenKey",
                            Keyword,
                            $"references screen key '{screenKey}', which is not listed in /screens"));
                    }

                    stepIndex++;
                }
            }

            flowIndex++;
        }

        return violations;
    }

    private static bool TryGetString(JsonElement parent, string name, out string value)
    {
        value = string.Empty;

        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }
}
