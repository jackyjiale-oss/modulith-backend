using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.Preferences;
using TemplateName.Modules.Notifications.Domain.HubTickets;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Notifications.Application.HubTickets.Issue;

/// <summary>
/// Creates a ticket for the caller (<see cref="HubTicketTokens"/>), stores only its SHA-256 hash with <see cref="HubOptions.TicketLifetime"/>
/// and answers the ticket itself, which exists nowhere else. A caller without a user id gets the 401 failure and no ticket.
/// </summary>
internal sealed class IssueHubTicketCommandHandler(
    IHubTicketRepository tickets,
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    IOptions<HubOptions> options) : ICommandHandler<IssueHubTicketCommand, HubTicketResponse>
{
    public async Task<Result<HubTicketResponse>> HandleAsync(IssueHubTicketCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Result.Failure<HubTicketResponse>(UnauthenticatedCaller.Error);
        }

        var (ticket, hash) = HubTicketTokens.Create();
        var issued = HubTicket.Issue(userId, hash, options.Value.TicketLifetime, timeProvider.GetUtcNow());
        tickets.Add(issued);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new HubTicketResponse(ticket, issued.ExpiresAt);
    }
}
