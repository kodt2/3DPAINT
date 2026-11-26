using System.ComponentModel.DataAnnotations;

namespace Auth.Models.Validation;

/// <summary>
/// A birth date must be a plausible past date: not in the future and not
/// earlier than 1900-01-01. Part of the monolith-compatible registration
/// contract (step 4.2). Absence is handled by [Required]; this attribute
/// validates the value only when one is present.
/// </summary>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter,
    AllowMultiple = false)]
public sealed class BirthDateRangeAttribute : ValidationAttribute
{
    private static readonly DateOnly EarliestAllowed = new(1900, 1, 1);

    public BirthDateRangeAttribute()
        : base("Дата рождения должна быть в прошлом (не ранее 01.01.1900).")
    {
    }

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is not DateOnly date)
        {
            return null;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (date > today || date < EarliestAllowed)
        {
            return new ValidationResult(ErrorMessage!);
        }

        return ValidationResult.Success;
    }
}
