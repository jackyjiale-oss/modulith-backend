using FluentValidation;
using TemplateName.Modules.Notifications.Application.Catalog;

namespace TemplateName.Modules.Notifications.Application.Preferences.Update;

/// <summary>
/// The shape of the request: one to 100 items, none missing, each with a type code that fits its column and a channel that is defined
/// (a JSON number outside the enum would otherwise get through). Whether the type exists, has the channel, or may be switched off is a
/// rule of the catalog, answered by the handler with its own error code.
/// </summary>
internal sealed class UpdatePreferencesCommandValidator : AbstractValidator<UpdatePreferencesCommand>
{
    /// <summary>The most items one request may carry.</summary>
    internal const int MaxItems = 100;

    public UpdatePreferencesCommandValidator()
    {
        RuleFor(command => command.Items)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .Must(items => items.Count <= MaxItems)
            .WithMessage($"'{{PropertyName}}' may hold at most {MaxItems} items.")
            .Must(items => items.All(item => item is not null))
            .WithMessage("'{PropertyName}' must not contain an empty item.");

        // Only for a list the rules above accepted: a missing or oversized list is already reported once.
        When(
            command => command.Items is { Count: > 0 and <= MaxItems } items && items.All(item => item is not null),
            () => RuleForEach(command => command.Items).ChildRules(item =>
            {
                item.RuleFor(update => update.TypeCode)
                    .Cascade(CascadeMode.Stop)
                    .NotEmpty()
                    .MaximumLength(NotificationCatalog.MaxCodeLength);

                item.RuleFor(update => update.Channel).IsInEnum();
            }));
    }
}
