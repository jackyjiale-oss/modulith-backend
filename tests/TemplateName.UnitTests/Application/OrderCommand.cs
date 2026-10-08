using TemplateName.Application.Common.Messaging;

namespace TemplateName.UnitTests.Application;

internal sealed record OrderCommand(OrderCommand.AddressLine Address, IReadOnlyList<OrderCommand.ItemLine> Items)
    : ICommand<string>
{
    internal sealed record AddressLine(string Street);

    internal sealed record ItemLine(string Name);
}
