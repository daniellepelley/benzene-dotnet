using System;
using System.Collections.Generic;
using System.Linq;
using Benzene.Abstractions.Results;
using Benzene.Abstractions.Serialization;
using Benzene.Abstractions.Validation;
using FluentValidation.Results;

namespace Benzene.FluentValidation;

/// <summary>
/// Projects a failed FluentValidation <see cref="ValidationResult"/> onto Benzene's structured errors.
/// </summary>
public static class ValidationResultExtensions
{
    /// <summary>
    /// One <see cref="BenzeneError"/> per <see cref="ValidationFailure"/>, in FluentValidation's order:
    /// <see cref="BenzeneError.Message"/> is the failure's <c>ErrorMessage</c>;
    /// <see cref="BenzeneError.Field"/> is its <c>PropertyName</c> converted to a JSON Pointer into the
    /// request's JSON form (<c>Items[0].Sku</c> → <c>/items/0/sku</c>, wire-contracts.md §1.3), omitted
    /// when the rule names no single location (see <see cref="FieldPointer"/>);
    /// <see cref="BenzeneError.Code"/> is its <c>ErrorCode</c>, emitted verbatim - FluentValidation's
    /// default is the validator type name (e.g. <c>NotEmptyValidator</c>), never stripped or
    /// normalized (work/benzene-result-errors-ruling.md §5.1).
    /// </summary>
    /// <param name="validationResult">The failed validation result.</param>
    /// <param name="requestType">The validated request type, which the property paths are rooted at.</param>
    /// <param name="names">Names members as the service's serializer writes them; <c>null</c> for Benzene's defaults.</param>
    /// <returns>The structured errors.</returns>
    public static BenzeneError[] ToBenzeneErrors(this ValidationResult validationResult, Type requestType, IJsonPropertyNameResolver? names = null)
    {
        return validationResult.Errors
            .Select(x => new BenzeneError(
                x.ErrorMessage,
                FieldPointer.FromPropertyPath(requestType, x.PropertyName, names),
                string.IsNullOrEmpty(x.ErrorCode) ? null : x.ErrorCode))
            .ToArray();
    }
}
