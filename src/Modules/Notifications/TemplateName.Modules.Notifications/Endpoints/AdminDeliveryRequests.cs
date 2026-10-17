using Microsoft.AspNetCore.Mvc;
using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.Modules.Notifications.Endpoints;

/// <summary>
/// The query string of the delivery list, bound with <c>[AsParameters]</c>. Without the explicit names the OpenAPI document would list
/// the parameters in PascalCase; query parameters are camelCase (docs/coding-conventions.md Section 7). <c>status</c> and
/// <c>channel</c> take the enum name exactly as the responses write it (<c>DeadLettered</c>, <c>Email</c>).
/// </summary>
internal sealed record ListDeliveriesRequest(
    [FromQuery(Name = "status")] DeliveryStatus? Status,
    [FromQuery(Name = "channel")] NotificationChannel? Channel,
    [FromQuery(Name = "typeCode")] string? TypeCode,
    [FromQuery(Name = "recipientUserId")] Guid? RecipientUserId,
    [FromQuery(Name = "pageSize")] int? PageSize,
    [FromQuery(Name = "cursor")] string? Cursor,
    [FromQuery(Name = "sort")] string? Sort,
    [FromQuery(Name = "includeTotalCount")] bool? IncludeTotalCount);
