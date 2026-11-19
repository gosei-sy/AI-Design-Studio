using System.ComponentModel.DataAnnotations;

namespace AIDesignStudio.Api.Identity;

/// <summary>
/// Validates request DTOs with DataAnnotations and returns stable problem details
/// instead of letting nulls reach the handler.
///
/// <para>W2-007. `[Required]` on a request record does nothing on its own: a `{}`
/// body binds every property to null, the handler runs, and the caller gets a 500
/// from inside Identity — an anonymous caller could generate server errors at
/// will. Registering `AddValidation()` is the documented fix and is also wired up
/// in the host, but measured behaviour showed it did not cover these records:
/// `/register` returned 400 while `/login`, `/forgot-password` and
/// `/reset-password` still returned 500 for the same empty body. Rather than
/// guess at why, this filter makes the boundary explicit and uniform — which the
/// review names as an acceptable equivalent.</para>
///
/// <para>Runs before the antiforgery filter would matter and before any store is
/// touched, so a malformed request never reaches the database.</para>
/// </summary>
internal sealed class RequestValidationEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        foreach (var argument in context.Arguments)
        {
            // Only the request DTOs declared in this assembly. Framework
            // services resolved into the handler are not request data and have no
            // validation metadata worth running.
            if (argument is null || argument.GetType().Assembly != typeof(Program).Assembly)
            {
                continue;
            }

            var results = new List<ValidationResult>();
            var valid = Validator.TryValidateObject(
                argument,
                new ValidationContext(argument),
                results,
                validateAllProperties: true);

            if (valid)
            {
                continue;
            }

            var errors = results
                .SelectMany(r => r.MemberNames.DefaultIfEmpty(string.Empty)
                    .Select(member => new { member, r.ErrorMessage }))
                .GroupBy(e => e.member, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(e => e.ErrorMessage ?? "Invalid value.").ToArray(),
                    StringComparer.Ordinal);

            return Results.Problem(
                type: "https://aidesignstudio.dev/problems/request.invalid",
                title: "Invalid request",
                statusCode: StatusCodes.Status400BadRequest,
                detail: "One or more fields are missing or invalid.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "request.invalid",
                    ["errors"] = errors,
                });
        }

        return await next(context);
    }
}
