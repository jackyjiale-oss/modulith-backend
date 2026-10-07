using FluentValidation;

namespace TemplateName.UnitTests.Application;

internal sealed class OrderCommandValidator : AbstractValidator<OrderCommand>
{
    public OrderCommandValidator()
    {
        RuleFor(command => command.Address).SetValidator(new AddressLineValidator());
        RuleForEach(command => command.Items).SetValidator(new ItemLineValidator());
    }

    private sealed class AddressLineValidator : AbstractValidator<OrderCommand.AddressLine>
    {
        public AddressLineValidator() => RuleFor(address => address.Street).NotEmpty();
    }

    private sealed class ItemLineValidator : AbstractValidator<OrderCommand.ItemLine>
    {
        public ItemLineValidator() => RuleFor(item => item.Name).NotEmpty();
    }
}
