using System.Net;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace FlowX.Mcp.Tests;

/// <summary>
/// The agent surface on the wire, against a running host that serves the same flows over
/// HTTP.
/// </summary>
public sealed class AgentSurfaceTests : IAsyncLifetime
{
    private AgentHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _host = await AgentHost.StartAsync(Ct);

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    /// <summary>
    /// <c>tools/list</c> publishes what the manifest declares, with the manifest's own
    /// values.
    /// </summary>
    /// <remarks>
    /// The projection's unit tests prove the derivation; this proves the derivation is what
    /// reaches an agent, through a container built by generated code, over a real socket.
    /// </remarks>
    [Fact]
    public async Task ToolsListPublishesTheManifestsAgentTriggeredFlows()
    {
        var (response, body) = await _host.RpcAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", permissions: null, Ct);

        using (body)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");

            var tools = body.RootElement.GetProperty("result").GetProperty("tools")
                .EnumerateArray()
                .ToDictionary(static tool => tool.GetProperty("name").GetString()!);

            tools.Keys.ShouldBe(["booking_book", "booking_quote"], ignoreOrder: true);

            var book = tools["booking_book"];

            book.GetProperty("description").GetString()
                .ShouldBe("Book a room for a number of nights and charge the card on file.");

            book.GetProperty("inputSchema").GetProperty("x-flowx-contract").GetString()
                .ShouldBe("FlowX.Mcp.Tests.BookRoom");

            // [Sensitive] on the contract, published so a client can keep it out of a
            // transcript or a confirmation prompt.
            book.GetProperty("inputSchema").GetProperty("x-flowx-sensitive")
                .EnumerateArray().Select(static m => m.GetString()).ShouldBe(["CardNumber"]);

            var annotations = book.GetProperty("annotations");

            annotations.GetProperty("flowId").GetString().ShouldBe("booking.book");
            annotations.GetProperty("idempotent").GetBoolean().ShouldBeFalse();
            annotations.GetProperty("confirmationRequired").GetBoolean().ShouldBeTrue();

            annotations.GetProperty("sideEffects")
                .EnumerateArray().Select(static e => e.GetString()).ShouldBe(["payment-gateway"]);

            annotations.GetProperty("requiredPermissions")
                .EnumerateArray().Select(static p => p.GetString()).ShouldBe(["booking:write"]);

            // The read-only tool is the negative of the booking one in every annotation, so
            // no assertion above can be passing because the projection answers a constant.
            var quote = tools["booking_quote"].GetProperty("annotations");

            quote.GetProperty("idempotent").GetBoolean().ShouldBeTrue();
            quote.GetProperty("confirmationRequired").GetBoolean().ShouldBeFalse();
            quote.TryGetProperty("sideEffects", out _).ShouldBeFalse();
            quote.TryGetProperty("requiredPermissions", out _).ShouldBeFalse();
        }
    }

    /// <summary>
    /// <c>tools/call</c> runs the flow and returns what it produced.
    /// </summary>
    [Fact]
    public async Task ToolsCallStartsTheFlowAndReturnsItsResult()
    {
        var (response, body) = await _host.RpcAsync(
            """
            {"jsonrpc":"2.0","id":7,"method":"tools/call","params":{
              "name":"booking_book",
              "arguments":{"room":"R-14","nights":3,"cardNumber":"4111111111111111"}}}
            """,
            permissions: "booking:write",
            Ct);

        using (body)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            body.RootElement.GetProperty("id").GetInt32().ShouldBe(7);

            var result = body.RootElement.GetProperty("result");

            result.GetProperty("isError").GetBoolean().ShouldBeFalse();

            var output = result.GetProperty("structuredContent").GetProperty("output");

            // The flow's own .Return(...) clause, through the flow's own contract metadata.
            output.GetProperty("reference").GetString().ShouldBe("R-14-1");
            output.GetProperty("total").GetDecimal().ShouldBe(3 * ChargeCard.NightlyRate);

            // MCP's required model-readable rendering carries the same document.
            result.GetProperty("content")[0].GetProperty("text").GetString()
                .ShouldNotBeNull()
                .ShouldContain("R-14-1");
        }
    }

    /// <summary>
    /// One flow declaring two triggers is reachable both ways and answers the same thing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the claim the stacked attributes make, and until now only its refusal
    /// half was tested.</strong>
    /// <see cref="ARefusedCallReturnsTheSameStanceTheHttpPathEnforces"/> drives both surfaces
    /// and compares two <em>rejections</em>, which a flow that refused everything would also
    /// satisfy. What was missing is the half that says the two addresses actually run the
    /// same flow: same input, same output, from one class carrying
    /// <c>[HttpTrigger]</c> and <c>[AgentTrigger]</c>.
    /// </para>
    /// <para>
    /// <strong>Nothing about the second address is written down anywhere.</strong> The route
    /// comes from <c>FlowXEndpoints.g.cs</c> and the tool from <c>FlowXAgentTools.g.cs</c>,
    /// both emitted from the same declaration in the same build — so this test is what turns
    /// "the compiler emits two registrations" into "two callers get the same answer".
    /// </para>
    /// <para>
    /// The two documents are not compared field by field on purpose: MCP wraps the output in
    /// <c>structuredContent</c> and the HTTP path returns it bare, and asserting the wrapping
    /// were identical would fail on a difference that is the protocols' and not the flow's.
    /// What must agree is what the flow produced.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task OneFlowWithTwoTriggersAnswersTheSameOverBoth()
    {
        const string Arguments = """{"room":"R-14","nights":3,"cardNumber":"4111111111111111"}""";
        const string Grant = "booking:write";

        var (agentResponse, agentBody) = await _host.RpcAsync(
            """{"jsonrpc":"2.0","id":14,"method":"tools/call","params":{"name":"booking_book","arguments":"""
            + Arguments + "}}",
            Grant,
            Ct);

        var (httpResponse, httpBody) = await _host.PostBookingAsync(Arguments, Grant, Ct);

        using (agentBody)
        using (httpBody)
        {
            agentResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
            httpResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

            var agent = agentBody.RootElement.GetProperty("result");

            agent.GetProperty("isError").GetBoolean().ShouldBeFalse(
                "The agent surface refused a call the HTTP surface accepted, so the two "
                + "addresses are not reaching the same flow.");

            var overAgent = agent.GetProperty("structuredContent").GetProperty("output");
            var overHttp = httpBody.RootElement;

            overHttp.GetProperty("reference").GetString().ShouldBe(
                overAgent.GetProperty("reference").GetString(),
                "The same booking, asked for twice at two addresses generated from one class, "
                + "came back with two references.");

            overHttp.GetProperty("total").GetDecimal().ShouldBe(
                overAgent.GetProperty("total").GetDecimal(),
                "The two addresses priced the same booking differently, which means they are "
                + "not running the same plan.");

            overHttp.GetProperty("reference").GetString().ShouldBe("R-14-1");
        }
    }

    /// <summary>
    /// A refused call answers with the flow's own refusal, and it is the one the HTTP path
    /// enforces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Both transports are driven in one test, against one flow, with one
    /// principal.</strong> Asserting the agent's refusal against a remembered code would
    /// prove only that somebody wrote the same string twice; running the HTTP endpoint
    /// beside it and comparing proves the two paths reach the same decision, because they
    /// are the same decision — <c>StepAuthorization.Decide</c> in the step loop, against
    /// <c>FlowInvocation.Principal</c>, which both transports fill from the same
    /// <c>HttpTriggerReader</c>.
    /// </para>
    /// <para>
    /// The caller is authenticated and holds the wrong grant, which is the case that
    /// distinguishes a permission stance from an authentication check.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARefusedCallReturnsTheSameStanceTheHttpPathEnforces()
    {
        const string Arguments = """{"room":"R-14","nights":3,"cardNumber":"4111111111111111"}""";
        const string WrongGrant = "booking:read";

        var (agentResponse, agentBody) = await _host.RpcAsync(
            """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"booking_book","arguments":"""
            + Arguments + "}}",
            WrongGrant,
            Ct);

        var (httpResponse, httpBody) = await _host.PostBookingAsync(Arguments, WrongGrant, Ct);

        using (agentBody)
        using (httpBody)
        {
            // The HTTP path: 403 and an RFC 7807 body naming the code.
            httpResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

            var httpCode = httpBody.RootElement.GetProperty("code").GetString();

            httpCode.ShouldBe(AuthorizationErrors.PermissionDeniedCode);

            // The agent path: a JSON-RPC result carrying the same Error, because it is the
            // same object from the same step loop. A refusal is a Result failure, never an
            // exception and never a bypass (ADR-0007, ADR-0029).
            agentResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

            var result = agentBody.RootElement.GetProperty("result");

            result.GetProperty("isError").GetBoolean().ShouldBeTrue();

            var error = result.GetProperty("structuredContent").GetProperty("error");

            error.GetProperty("code").GetString().ShouldBe(
                httpCode,
                "The agent and the request were refused by different codes, so they did not " +
                "meet the same stance — which is the one thing docs/25 §3 promises about " +
                "this transport.");

            error.GetProperty("category").GetString().ShouldBe(nameof(ErrorCategory.Forbidden));

            // The grant is named so the agent can ask for it. The caller's claims are not,
            // which is docs/15-Security.md §3's Boundary 1 rule.
            error.GetProperty("detail").GetProperty("permission").GetString()
                .ShouldBe("booking:write");

            error.GetProperty("message").GetString()
                .ShouldNotBeNull()
                .ShouldNotContain(WrongGrant);
        }
    }

    /// <summary>
    /// An anonymous agent meets the stance too, and is refused by authentication rather than
    /// by grant.
    /// </summary>
    /// <remarks>
    /// The pair to the test above: without it, a surface that refused every call for one
    /// reason would satisfy both. The two codes differ because the repairs differ — "sign
    /// in" against "ask for a grant".
    /// </remarks>
    [Fact]
    public async Task AnAnonymousAgentIsRefusedBeforeTheCapabilityRuns()
    {
        var (_, body) = await _host.RpcAsync(
            """
            {"jsonrpc":"2.0","id":10,"method":"tools/call","params":{
              "name":"booking_book",
              "arguments":{"room":"R-14","nights":1,"cardNumber":"4111111111111111"}}}
            """,
            permissions: null,
            Ct);

        using (body)
        {
            var result = body.RootElement.GetProperty("result");

            result.GetProperty("isError").GetBoolean().ShouldBeTrue();

            result.GetProperty("structuredContent").GetProperty("error")
                .GetProperty("code").GetString()
                .ShouldBe(AuthorizationErrors.NotAuthenticatedCode);
        }
    }

    /// <summary>
    /// A public, side-effect-free tool runs for a caller holding nothing.
    /// </summary>
    /// <remarks>
    /// The control. Without it, the two refusals above are equally explained by a surface
    /// that refuses everything.
    /// </remarks>
    [Fact]
    public async Task APublicToolRunsForAnAnonymousAgent()
    {
        var (_, body) = await _host.RpcAsync(
            """
            {"jsonrpc":"2.0","id":11,"method":"tools/call","params":{
              "name":"booking_quote","arguments":{"room":"R-9"}}}
            """,
            permissions: null,
            Ct);

        using (body)
        {
            var result = body.RootElement.GetProperty("result");

            result.GetProperty("isError").GetBoolean().ShouldBeFalse();

            result.GetProperty("structuredContent").GetProperty("output")
                .GetProperty("nightlyRate").GetDecimal().ShouldBe(ChargeCard.NightlyRate);
        }
    }

    /// <summary>An unknown tool name is a stated error, not an exception.</summary>
    [Fact]
    public async Task AnUnknownToolNameIsAStatedError()
    {
        var (response, body) = await _host.RpcAsync(
            """
            {"jsonrpc":"2.0","id":12,"method":"tools/call","params":{
              "name":"booking_cancel","arguments":{}}}
            """,
            permissions: "booking:write",
            Ct);

        using (body)
        {
            // A JSON-RPC error, not a 500: the call never reached a flow, and the client
            // has to be able to tell "the tool is not there" from "the tool refused you".
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var error = body.RootElement.GetProperty("error");

            error.GetProperty("code").GetInt32().ShouldBe(McpJsonRpc.InvalidParams);
            error.GetProperty("data").GetProperty("code").GetString()
                .ShouldBe(McpErrors.UnknownToolCode);
            error.GetProperty("data").GetProperty("category").GetString()
                .ShouldBe(nameof(ErrorCategory.NotFound));

            // The name it asked for, so a model can see what it got wrong.
            error.GetProperty("data").GetProperty("detail").GetProperty("tool").GetString()
                .ShouldBe("booking_cancel");
        }
    }

    /// <summary>A malformed argument document is a stated error, not an exception.</summary>
    /// <remarks>
    /// Three shapes: a document the contract cannot read, no <c>arguments</c> member at all,
    /// and a body that is not JSON. Each is answered rather than thrown, which is what stops
    /// a model's mistake becoming a stack trace in its transcript.
    /// </remarks>
    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":13,"method":"tools/call","params":{"name":"booking_book","arguments":{"nights":"three"}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":13,"method":"tools/call","params":{"name":"booking_book"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":13,"method":"tools/call","params":{"name":"booking_book","arguments":null}}""")]
    public async Task AMalformedArgumentDocumentIsAStatedError(string request)
    {
        var (response, body) = await _host.RpcAsync(request, "booking:write", Ct);

        using (body)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var error = body.RootElement.GetProperty("error");

            error.GetProperty("code").GetInt32().ShouldBe(McpJsonRpc.InvalidParams);
            error.GetProperty("data").GetProperty("code").GetString()
                .ShouldBe(McpErrors.MalformedArgumentsCode);

            // The contract it had to satisfy, which is what a model needs to retry.
            error.GetProperty("data").GetProperty("detail").GetProperty("contract").GetString()
                .ShouldBe("FlowX.Mcp.Tests.BookRoom");
        }
    }

    /// <summary>A body that is not JSON at all is answered, not thrown.</summary>
    [Fact]
    public async Task ABodyThatIsNotJsonIsAStatedError()
    {
        var (response, body) = await _host.RpcAsync("{\"jsonrpc\":", permissions: null, Ct);

        using (body)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

            body.RootElement.GetProperty("error").GetProperty("code").GetInt32()
                .ShouldBe(McpJsonRpc.ParseError);

            // JSON-RPC requires the member on every response, including one whose request
            // had no readable id.
            body.RootElement.GetProperty("id").ValueKind.ShouldBe(JsonValueKind.Null);
        }
    }

    /// <summary>A method nobody serves is answered, not thrown.</summary>
    /// <remarks>
    /// The example was <c>resources/list</c> until 2026-08-02, when this surface began serving
    /// it. <c>prompts/list</c> is the replacement and is a real MCP method this server declares
    /// no capability for — a made-up name would have tested the same branch and told a reader
    /// nothing about which parts of the protocol are absent.
    /// </remarks>
    [Fact]
    public async Task AnUnservedMethodIsAStatedError()
    {
        var (_, body) = await _host.RpcAsync(
            """{"jsonrpc":"2.0","id":14,"method":"prompts/list"}""", permissions: null, Ct);

        using (body)
        {
            body.RootElement.GetProperty("error").GetProperty("code").GetInt32()
                .ShouldBe(McpJsonRpc.MethodNotFound);
        }
    }

    /// <summary>
    /// <c>initialize</c> answers, so a real MCP client can complete its handshake.
    /// </summary>
    /// <remarks>
    /// Without it a conformant client never gets as far as <c>tools/list</c>, and every
    /// assertion above would be about a surface nothing could connect to.
    /// </remarks>
    [Fact]
    public async Task InitializeCompletesTheHandshake()
    {
        var (_, body) = await _host.RpcAsync(
            """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""",
            permissions: null,
            Ct);

        using (body)
        {
            var result = body.RootElement.GetProperty("result");

            result.GetProperty("protocolVersion").GetString().ShouldBe(McpJsonRpc.ProtocolVersion);
            result.GetProperty("capabilities").GetProperty("tools").ValueKind
                .ShouldBe(JsonValueKind.Object);

            // The host's configured application name, so an agent transcript and a trace
            // name the same service.
            result.GetProperty("serverInfo").GetProperty("name").GetString().ShouldBe("Bookings");
        }
    }

    /// <summary>The notification every MCP client sends is accepted in silence.</summary>
    [Fact]
    public async Task TheInitializedNotificationIsAccepted()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AgentHost.McpRoute)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        };

        var response = await _host.Client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBeEmpty();
    }
}
