using System.Data;
using Dapper;

namespace TemplateName.Infrastructure.Common.Persistence;

/// <summary>
/// Lets Dapper read SQL Server <c>date</c> columns into <see cref="DateOnly"/> (SqlClient returns them as <see cref="DateTime"/>) and
/// send <see cref="DateOnly"/> parameters as <c>date</c>. <c>AddInfrastructureCommon</c> registers it.
/// </summary>
internal sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
{
    public override DateOnly Parse(object value) => value switch
    {
        DateOnly date => date,
        DateTime dateTime => DateOnly.FromDateTime(dateTime),
        _ => throw new DataException($"Cannot convert a value of type {value.GetType()} to {nameof(DateOnly)}."),
    };

    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        parameter.DbType = DbType.Date;
        parameter.Value = value.ToDateTime(TimeOnly.MinValue);
    }
}
