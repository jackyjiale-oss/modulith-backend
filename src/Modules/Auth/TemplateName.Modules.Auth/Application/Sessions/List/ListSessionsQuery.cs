using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;

namespace TemplateName.Modules.Auth.Application.Sessions.List;

/// <summary>A page of the caller's active sessions.</summary>
internal sealed record ListSessionsQuery(CursorPageRequest Page) : IQuery<CursorPage<SessionResponse>>;
