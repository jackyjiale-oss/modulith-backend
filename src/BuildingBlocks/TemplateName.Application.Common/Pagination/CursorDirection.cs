namespace TemplateName.Application.Common.Pagination;

/// <summary>Which way a cursor pages from its position. The numbers are written into cursors, so they never change.</summary>
public enum CursorDirection
{
    Next = 1,
    Previous = 2,
}
