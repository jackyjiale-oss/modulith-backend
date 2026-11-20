using TemplateName.Application.Common.Concurrency;

namespace TemplateName.UnitTests.Concurrency;

public sealed class IfMatchTests
{
    private static readonly EntityTag Tag = new("current");
    private static readonly EntityTag Other = new("other");

    [Fact]
    public void None_is_satisfied_by_every_tag() => IfMatch.None.IsSatisfiedBy(Tag).ShouldBeTrue();

    [Fact]
    public void Any_is_satisfied_by_every_tag() => IfMatch.Any.IsSatisfiedBy(Tag).ShouldBeTrue();

    [Fact]
    public void A_list_is_satisfied_by_any_of_its_tags() => IfMatch.Of([Other, Tag]).IsSatisfiedBy(Tag).ShouldBeTrue();

    [Fact]
    public void A_list_without_the_tag_is_not_satisfied() => IfMatch.Of([Other]).IsSatisfiedBy(Tag).ShouldBeFalse();

    [Fact]
    public void An_empty_list_is_never_satisfied() => IfMatch.Of([]).IsSatisfiedBy(Tag).ShouldBeFalse();
}
