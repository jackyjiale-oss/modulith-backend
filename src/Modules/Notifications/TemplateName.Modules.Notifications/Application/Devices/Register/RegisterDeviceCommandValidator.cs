using FluentValidation;
using TemplateName.Modules.Notifications.Domain.PushDevices;

namespace TemplateName.Modules.Notifications.Application.Devices.Register;

internal sealed class RegisterDeviceCommandValidator : AbstractValidator<RegisterDeviceCommand>
{
    public RegisterDeviceCommandValidator()
    {
        // An FCM token has no white space or control character; one that does is not a token (and the message never quotes it).
        RuleFor(command => command.Token)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .MaximumLength(PushDevice.MaxTokenLength)
            .Must(token => !token.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            .WithMessage("'{PropertyName}' must not contain white space or control characters.");
        RuleFor(command => command.Platform).IsInEnum();
        RuleFor(command => command.DeviceName).MaximumLength(PushDevice.MaxDeviceNameLength);
        RuleFor(command => command.AppVersion).MaximumLength(PushDevice.MaxAppVersionLength);
    }
}
