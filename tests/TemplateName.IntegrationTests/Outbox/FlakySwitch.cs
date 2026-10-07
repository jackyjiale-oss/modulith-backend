namespace TemplateName.IntegrationTests.Outbox;

/// <summary>Makes <see cref="FlakyTestEventHandler"/> fail: once (<see cref="FailNext"/>) or on every call (<see cref="FailAlways"/>).</summary>
public sealed class FlakySwitch
{
    public bool FailNext { get; set; }

    public bool FailAlways { get; set; }

    public void Reset()
    {
        FailNext = false;
        FailAlways = false;
    }
}
