namespace TemplateName.Application.Common.Pagination;

/// <summary>
/// A field a list endpoint allows sorting by. <paramref name="Name"/> is the camelCase API name (<c>createdAt</c>);
/// <paramref name="Column"/> is the bracketed SQL column (<c>[CreatedAt]</c>), always a constant in code and never built from input,
/// because it is written into SQL; the column must be non-nullable. <paramref name="ValueType"/> is the CLR type of its values, which
/// cursors are decoded into.
/// </summary>
public sealed record SortField(string Name, string Column, Type ValueType);
