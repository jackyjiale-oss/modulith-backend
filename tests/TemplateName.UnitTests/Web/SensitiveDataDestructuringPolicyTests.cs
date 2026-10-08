using Serilog;
using Serilog.Events;
using TemplateName.Web.Common.Observability;

namespace TemplateName.UnitTests.Web;

public sealed class SensitiveDataDestructuringPolicyTests
{
    [Fact]
    public void Masks_sensitive_properties_and_keeps_others()
    {
        var sink = new RecordingLogEventSink();
        using var log = new LoggerConfiguration().Destructure.With<SensitiveDataDestructuringPolicy>().WriteTo.Sink(sink).CreateLogger();

        log.Information("{@Req}", new { Email = "a@b.c", Password = "p", RefreshToken = "t" });

        var req = (StructureValue)sink.Events.Single().Properties["Req"];
        req.Properties.Single(p => p.Name == "Password").Value.ToString().ShouldBe("\"***\"");
        req.Properties.Single(p => p.Name == "RefreshToken").Value.ToString().ShouldBe("\"***\"");
        req.Properties.Single(p => p.Name == "Email").Value.ToString().ShouldBe("\"a@b.c\"");
    }

    [Fact]
    public void Masks_nested_properties()
    {
        var sink = new RecordingLogEventSink();
        using var log = new LoggerConfiguration().Destructure.With<SensitiveDataDestructuringPolicy>().WriteTo.Sink(sink).CreateLogger();

        log.Information("{@Req}", new { User = new { Name = "n", ApiKey = "k" } });

        var req = (StructureValue)sink.Events.Single().Properties["Req"];
        var user = (StructureValue)req.Properties.Single(p => p.Name == "User").Value;
        user.Properties.Single(p => p.Name == "ApiKey").Value.ToString().ShouldBe("\"***\"");
        user.Properties.Single(p => p.Name == "Name").Value.ToString().ShouldBe("\"n\"");
    }

    [Fact]
    public void Masks_every_sensitive_name_case_insensitively()
    {
        var sink = new RecordingLogEventSink();
        using var log = new LoggerConfiguration().Destructure.With<SensitiveDataDestructuringPolicy>().WriteTo.Sink(sink).CreateLogger();

        log.Information(
            "{@Req}",
            new { Password = "v", NewPASSWORD = "v", AccessToken = "v", ClientSecret = "v", Otp = "v", OtpCode = "v", ApiKey = "v", XApikeyHeader = "v", Name = "v" });

        var req = (StructureValue)sink.Events.Single().Properties["Req"];
        req.Properties.Where(p => p.Name != "Name").ShouldAllBe(p => p.Value.ToString() == "\"***\"");
        req.Properties.Single(p => p.Name == "Name").Value.ToString().ShouldBe("\"v\"");
    }

    [Fact]
    public void Leaves_values_without_sensitive_properties_to_default_destructuring()
    {
        var sink = new RecordingLogEventSink();
        using var log = new LoggerConfiguration().Destructure.With<SensitiveDataDestructuringPolicy>().WriteTo.Sink(sink).CreateLogger();

        log.Information("{@Count} {@Name} {@Items}", 3, "text", new[] { 1, 2 });

        var properties = sink.Events.Single().Properties;
        properties["Count"].ToString().ShouldBe("3");
        properties["Name"].ToString().ShouldBe("\"text\"");
        properties["Items"].ShouldBeOfType<SequenceValue>().Elements.Count.ShouldBe(2);
    }

    [Fact]
    public void Masks_sensitive_properties_of_items_inside_collections()
    {
        var sink = new RecordingLogEventSink();
        using var log = new LoggerConfiguration().Destructure.With<SensitiveDataDestructuringPolicy>().WriteTo.Sink(sink).CreateLogger();

        log.Information("{@Req}", new { Credentials = new[] { new { Secret = "s" } } });

        var req = (StructureValue)sink.Events.Single().Properties["Req"];
        var credentials = (SequenceValue)req.Properties.Single(p => p.Name == "Credentials").Value;
        var credential = (StructureValue)credentials.Elements.Single();
        credential.Properties.Single().Value.ToString().ShouldBe("\"***\"");
    }
}
