using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ai.Configuration;
using Jellyfin.Plugin.Ai.Models;
using Jellyfin.Plugin.Common.Costs;
using Jellyfin.Plugin.Common.Resilience;
using Xunit;

namespace Jellyfin.Plugin.Ai.Tests;

// OpenAI and OpenAI-compatible services through the official SDK, against canned replies
public sealed class OpenAiChatModelTests : IDisposable
{
    private const string Key = "sk-test-SECRET-123456";
    private static readonly Uri Local = new("http://localhost:11434/v1");
    private readonly FakeHttp _fake = new();
    private readonly HttpClient _http;

    public OpenAiChatModelTests() => _http = _fake.Client();

    public void Dispose()
    {
        _http.Dispose();
        _fake.Dispose();
    }

    internal static AiRequest Request(bool strictSchema = true) => new()
    {
        Purpose = "ingest.match",
        Instructions = "Pick one.",
        Data = "{\"file\":\"x\"}",
        Schema = strictSchema
            ? new Dictionary<string, JsonElement>
            {
                ["type"] = JsonSerializer.SerializeToElement("object"),
                ["properties"] = JsonSerializer.SerializeToElement(new { pick = new { type = "integer" } }),
                ["required"] = JsonSerializer.SerializeToElement(new[] { "pick" }),
                ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
            }
            : new Dictionary<string, JsonElement> { ["type"] = JsonSerializer.SerializeToElement("object") },
        MaxOutputTokens = 500,
        Effort = AiEffort.Medium,
    };

    internal static string Completion(string content, string finish = "stop", string model = "gpt-6-sol-2026-07-01", string? refusal = null, string usage = "{\"prompt_tokens\":120,\"completion_tokens\":30,\"total_tokens\":150}")
        => "{\"id\":\"c1\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"" + model + "\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":"
            + JsonSerializer.Serialize(content) + ",\"refusal\":" + (refusal is null ? "null" : JsonSerializer.Serialize(refusal)) + "},\"finish_reason\":\"" + finish + "\"}],\"usage\":" + usage + "}";

    private OpenAiChatModel OpenAi() => new(KnownProviders.OpenAi, Key, "gpt-6-sol", null, _http);

    private OpenAiChatModel Compatible(string? key = null) => new(KnownProviders.OpenAiCompatible, key, "llama3.3", Local, _http);

    [Fact]
    public async Task OpenAI_is_asked_with_a_strict_schema_and_the_effort_and_the_answer_is_read()
    {
        _fake.Reply(Completion("{\"pick\":2}"));

        var answer = await OpenAi().AskAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(2, answer.Json.GetProperty("pick").GetInt32());
        Assert.Equal("gpt-6-sol-2026-07-01", answer.Model);
        Assert.Equal(KnownProviders.OpenAi, answer.Provider);
        Assert.Equal((120, 30), (answer.InputTokens, answer.OutputTokens));
        Assert.True(answer.UsageKnown);
        Assert.Null(answer.ReportedCost);

        var sent = Assert.Single(_fake.Seen);
        Assert.Equal("https://api.openai.com/v1/chat/completions", sent.Uri.ToString());
        Assert.Equal("Bearer " + Key, sent.Authorization);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal("gpt-6-sol", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("medium", body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal(500, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
        var format = body.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());

        // Instructions and untrusted data are kept apart
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Contains("untrusted", messages[0].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("<data>", messages[1].GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_a_closed_schema_is_sent_as_strict()
    {
        Assert.True(OpenAiChatModel.IsStrictCompatible(Request().Schema));
        Assert.False(OpenAiChatModel.IsStrictCompatible(Request(strictSchema: false).Schema));

        // A nested object that allows more properties isn't strict either
        var nested = new Dictionary<string, JsonElement>(Request().Schema)
        {
            ["properties"] = JsonSerializer.SerializeToElement(new { pick = new { type = "object", properties = new { a = new { type = "string" } } } }),
        };
        Assert.False(OpenAiChatModel.IsStrictCompatible(nested));
    }

    [Fact]
    public async Task A_compatible_service_without_a_key_gets_no_authorization_and_no_effort()
    {
        _fake.Reply(Completion("{\"pick\":1}", model: "llama3.3"));

        var answer = await Compatible().AskAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(1, answer.Json.GetProperty("pick").GetInt32());
        var sent = Assert.Single(_fake.Seen);
        Assert.Equal("http://localhost:11434/v1/chat/completions", sent.Uri.ToString());
        Assert.Null(sent.Authorization);
        Assert.DoesNotContain("reasoning_effort", sent.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_service_without_json_schemas_is_asked_again_in_json_mode()
    {
        _fake.Reply("{\"error\":{\"message\":\"response_format json_schema is not supported\"}}", HttpStatusCode.BadRequest)
            .Reply(Completion("```json\n{\"pick\":3}\n```", model: "llama3.3"));

        var answer = await Compatible("local-key-123").AskAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(3, answer.Json.GetProperty("pick").GetInt32());
        Assert.Equal(2, _fake.Seen.Count);
        using var second = JsonDocument.Parse(_fake.Seen[1].Body);
        Assert.Equal("json_object", second.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Contains("JSON schema", second.RootElement.GetProperty("messages")[0].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Equal("Bearer local-key-123", _fake.Seen[1].Authorization);
    }

    [Fact]
    public async Task A_model_without_reasoning_effort_is_asked_again_without_it()
    {
        _fake.Reply("{\"error\":{\"message\":\"Unsupported parameter: 'reasoning_effort' is not supported with this model.\",\"type\":\"invalid_request_error\"}}", HttpStatusCode.BadRequest)
            .Reply(Completion("{\"pick\":1}", model: "gpt-4.1"));

        await OpenAi().AskAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Contains("reasoning_effort", _fake.Seen[0].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("reasoning_effort", _fake.Seen[1].Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("stop", "I can't help with that.", "declined")]
    [InlineData("content_filter", null, "declined")]
    [InlineData("length", null, "cut off")]
    public async Task Unusable_billed_replies_carry_their_usage(string finish, string? refusal, string words)
    {
        _fake.Reply(Completion(refusal is null ? "{\"pick\":" : string.Empty, finish, refusal: refusal));

        var ex = await Assert.ThrowsAsync<AiException>(() => OpenAi().AskAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Contains(words, ex.Message, StringComparison.Ordinal);
        Assert.True(ex.Charged);
        Assert.Equal((120, 30), (ex.InputTokens, ex.OutputTokens));
        Assert.Equal("gpt-6-sol-2026-07-01", ex.ChargedModel);
    }

    [Fact]
    public async Task An_answer_that_isnt_json_is_charged_and_transient()
    {
        _fake.Reply(Completion("Sure! The answer is 2."));

        var ex = await Assert.ThrowsAsync<AiException>(() => OpenAi().AskAsync(Request(), TestContext.Current.CancellationToken));

        Assert.True(ex.Charged);
        Assert.Equal(FailureClass.Transient, ex.Failure);
    }

    [Theory]
    [InlineData(401, "{\"error\":{\"message\":\"Incorrect API key provided: sk-test-SECRET-123456\"}}", "Authentication")]
    [InlineData(429, "{\"error\":{\"message\":\"You exceeded your current quota\",\"type\":\"insufficient_quota\"}}", "ProviderLimit")]
    [InlineData(429, "{\"error\":{\"message\":\"Rate limit reached\",\"type\":\"requests\"}}", "Transient")]
    [InlineData(503, "{\"error\":{\"message\":\"overloaded\"}}", "Transient")]
    [InlineData(400, "{\"error\":{\"message\":\"Invalid schema\"}}", "BadRequest")]
    [InlineData(404, "{\"error\":{\"message\":\"The model does not exist\"}}", "BadRequest")]
    public async Task Failures_follow_the_shared_classes_and_never_show_the_key(int status, string body, string failure)
    {
        _fake.Reply(body, (HttpStatusCode)status, ("retry-after", "5"));

        var ex = await Assert.ThrowsAsync<AiException>(() => OpenAi().AskAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal(Enum.Parse<FailureClass>(failure), ex.Failure);
        Assert.False(ex.Charged);
        Assert.DoesNotContain(Key, ex.Message, StringComparison.Ordinal);
        Assert.Single(_fake.Seen);
    }

    [Fact]
    public async Task A_used_up_quota_says_when_it_resets()
    {
        _fake.Reply("{\"error\":{\"message\":\"You exceeded your current quota\",\"type\":\"insufficient_quota\"}}", (HttpStatusCode)429, ("retry-after", "7200"));

        var ex = await Assert.ThrowsAsync<AiException>(() => OpenAi().AskAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal(FailureClass.ProviderLimit, ex.Failure);
        Assert.Equal(TimeSpan.FromHours(2), ex.RetryAfter);
        Assert.Contains("in about 2 hours", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_network_is_no_connection()
    {
        _fake.Throw(new HttpRequestException("No such host is known", new SocketException((int)SocketError.HostNotFound)));

        var ex = await Assert.ThrowsAsync<AiException>(() => OpenAi().AskAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal(FailureClass.NoConnection, ex.Failure);
        Assert.False(ex.Charged);
    }

    [Fact]
    public async Task A_cost_the_service_reports_is_kept()
    {
        _fake.Reply(Completion("{\"pick\":1}", model: "openai/gpt-6-sol", usage: "{\"prompt_tokens\":10,\"completion_tokens\":5,\"total_tokens\":15,\"cost\":0.00042}"));

        var answer = await Compatible("sk-or-test-key").AskAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal(Money.Of(0.00042m, "USD"), answer.ReportedCost);
    }

    [Fact]
    public async Task A_reply_without_usage_is_marked_so()
    {
        _fake.Reply("{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"pick\\\":1}\"},\"finish_reason\":\"stop\"}]}");

        var answer = await Compatible().AskAsync(Request(), TestContext.Current.CancellationToken);

        Assert.False(answer.UsageKnown);
        Assert.Equal("llama3.3", answer.Model);
    }

    [Fact]
    public async Task Models_are_listed()
    {
        _fake.Reply("{\"object\":\"list\",\"data\":[{\"id\":\"gpt-6-sol\",\"object\":\"model\",\"created\":1,\"owned_by\":\"openai\"},{\"id\":\"gpt-6-luna\",\"object\":\"model\",\"created\":1,\"owned_by\":\"openai\"}]}");

        var models = await OpenAiChatModel.ListAsync(Key, null, _http, TestContext.Current.CancellationToken);

        Assert.Equal(["gpt-6-sol", "gpt-6-luna"], models);
        Assert.Equal("https://api.openai.com/v1/models", _fake.Seen[0].Uri.ToString());
    }

    [Fact]
    public void OpenAI_needs_a_key_and_a_compatible_service_an_address()
    {
        Assert.Throws<ArgumentException>(() => new OpenAiChatModel(KnownProviders.OpenAi, null, "gpt-6-sol", null, _http));
        Assert.Throws<ArgumentNullException>(() => new OpenAiChatModel(KnownProviders.OpenAiCompatible, null, "m", null, _http));
    }
}
