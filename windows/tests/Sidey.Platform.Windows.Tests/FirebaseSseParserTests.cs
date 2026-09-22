using System.Text;
using System.Text.Json.Nodes;

namespace Sidey.Platform.Windows.Tests;

public sealed class FirebaseSseParserTests
{
    [Fact]
    public void PartialUtf8CrlfAndMultilineDataProduceCanonicalEvents()
    {
        const string Payload = "event: put\r\n"
            + "data: {\"path\":\"/친구\",\r\n"
            + "data: \"data\":{\"state\":\"online\"}}\r\n\r\n"
            + ": keepalive comment\r\n"
            + "event: keep-alive\r\ndata: null\r\n\r\n"
            + "event: cancel\r\ndata: \"permission_denied\"\r\n\r\n"
            + "event: auth_revoked\r\ndata: \"expired\"\r\n\r\n";
        byte[] bytes = Encoding.UTF8.GetBytes(Payload);
        var parser = new FirebaseSseParser();
        var events = new List<FirebaseSseEvent>();

        foreach (byte value in bytes)
        {
            events.AddRange(parser.Append([value]));
        }

        events.AddRange(parser.Complete());
        Assert.Equal(
            [
                FirebaseSseEventKind.Put,
                FirebaseSseEventKind.KeepAlive,
                FirebaseSseEventKind.Cancel,
                FirebaseSseEventKind.AuthRevoked,
            ],
            events.Select(item => item.Kind));
        FirebaseRtdbMutation mutation = events[0].GetMutation();
        Assert.Equal("/친구", mutation.Path);
        Assert.Equal("online", mutation.Data!["state"]!.GetValue<string>());
        Assert.False(events[1].IsTerminal);
        Assert.True(events[2].IsTerminal);
        Assert.True(events[3].IsTerminal);
        Assert.DoesNotContain("online", events[0].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("online", mutation.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PutPatchAndNullDeleteApplyAtRelativeAndMultipathLocations()
    {
        var snapshot = new FirebaseRtdbSnapshot();
        snapshot.Apply(new FirebaseRtdbMutation(
            FirebaseSseEventKind.Put,
            "/",
            JsonNode.Parse("""{"member":{"state":"online","typing":false}}""")));
        snapshot.Apply(new FirebaseRtdbMutation(
            FirebaseSseEventKind.Patch,
            "/member",
            JsonNode.Parse("""{"state":"away","typing":true}""")));
        snapshot.Apply(new FirebaseRtdbMutation(
            FirebaseSseEventKind.Patch,
            "/",
            JsonNode.Parse("""{"member/state":"offline","member/typing":null,"revision":7}""")));
        snapshot.Apply(new FirebaseRtdbMutation(
            FirebaseSseEventKind.Put,
            "/revision",
            null));

        JsonNode value = snapshot.Value!;
        Assert.Equal("offline", value["member"]!["state"]!.GetValue<string>());
        Assert.Null(value["member"]!["typing"]);
        Assert.Null(value["revision"]);
    }

    [Fact]
    public void ArrayUpdatesPreserveSiblingsAndPercentTextIsNotUriDecoded()
    {
        var snapshot = new FirebaseRtdbSnapshot();
        snapshot.Apply(new FirebaseRtdbMutation(
            FirebaseSseEventKind.Put,
            "/",
            JsonNode.Parse("""{"items":["a","b"],"%2F":"old"}""")));
        snapshot.Apply(new FirebaseRtdbMutation(
            FirebaseSseEventKind.Put,
            "/items/1",
            JsonValue.Create("c")));
        snapshot.Apply(new FirebaseRtdbMutation(
            FirebaseSseEventKind.Put,
            "/%2F",
            JsonValue.Create("literal")));

        JsonNode value = snapshot.Value!;
        Assert.Equal("a", value["items"]![0]!.GetValue<string>());
        Assert.Equal("c", value["items"]![1]!.GetValue<string>());
        Assert.Equal("literal", value["%2F"]!.GetValue<string>());
        Assert.Null(value[""]);
    }

    [Fact]
    public void NonCanonicalArrayChildConvertsToObjectWithoutLosingExistingChildren()
    {
        var snapshot = new FirebaseRtdbSnapshot();
        snapshot.Apply(new FirebaseRtdbMutation(
            FirebaseSseEventKind.Put,
            "/",
            JsonNode.Parse("""{"items":["a","b"]}""")));
        snapshot.Apply(new FirebaseRtdbMutation(
            FirebaseSseEventKind.Put,
            "/items/01",
            JsonValue.Create("leading-zero")));

        JsonNode items = snapshot.Value!["items"]!;
        Assert.IsType<JsonObject>(items);
        Assert.Equal("a", items["0"]!.GetValue<string>());
        Assert.Equal("b", items["1"]!.GetValue<string>());
        Assert.Equal("leading-zero", items["01"]!.GetValue<string>());
    }

    [Fact]
    public void PatchWithALaterMalformedChildIsRejectedAtomically()
    {
        var snapshot = new FirebaseRtdbSnapshot();
        snapshot.Apply(new FirebaseRtdbMutation(
            FirebaseSseEventKind.Put,
            "/",
            JsonNode.Parse("""{"state":"before"}""")));

        Assert.Throws<InvalidDataException>(() => snapshot.Apply(new FirebaseRtdbMutation(
            FirebaseSseEventKind.Patch,
            "/",
            JsonNode.Parse("""{"state":"after","bad//path":true}"""))));

        Assert.Equal("before", snapshot.Value!["state"]!.GetValue<string>());
        Assert.Null(snapshot.Value!["bad"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("/repeated//segment")]
    [InlineData("/trailing/")]
    public void MalformedEventPathFailsClosed(string path)
    {
        var snapshot = new FirebaseRtdbSnapshot();

        Assert.Throws<InvalidDataException>(() => snapshot.Apply(new FirebaseRtdbMutation(
            FirebaseSseEventKind.Put,
            path,
            JsonValue.Create(true))));
    }

    [Fact]
    public void InvalidUtf8AndMalformedMutationFailClosed()
    {
        var parser = new FirebaseSseParser();

        Assert.Throws<DecoderFallbackException>(() => parser.Append([0xc3, 0x28]));
        var malformed = new FirebaseSseEvent(FirebaseSseEventKind.Put, "{\"data\":null}");
        Assert.Throws<InvalidDataException>(malformed.GetMutation);
    }

    [Fact]
    public void ReconnectDropsAnEventWithoutItsBlankLineTerminator()
    {
        var parser = new FirebaseSseParser();
        byte[] truncated = Encoding.UTF8.GetBytes(
            "event: put\ndata: {\"path\":\"/state\",\"data\":\"online\"}\n");

        Assert.Empty(parser.Append(truncated));
        Assert.Empty(parser.Complete());
    }

    [Fact]
    public void LeadingBomIsIgnoredAndDataLessEventIsNotDispatched()
    {
        var parser = new FirebaseSseParser();
        byte[] bytes = Encoding.UTF8.GetBytes(
            "\uFEFFevent: auth_revoked\n\n"
            + "event: put\ndata: {\"path\":\"/state\",\"data\":true}\n\n");

        FirebaseSseEvent parsed = Assert.Single(parser.Append(bytes));

        Assert.Equal(FirebaseSseEventKind.Put, parsed.Kind);
    }

    [Fact]
    public void AggregateEventDataHasABoundedSize()
    {
        var parser = new FirebaseSseParser();
        string line = $"data: {new string('x', 140_000)}\n";

        Assert.Empty(parser.Append(Encoding.UTF8.GetBytes($"event: put\n{line}")));
        Assert.Throws<InvalidDataException>(() => parser.Append(Encoding.UTF8.GetBytes(line)));
    }
}
