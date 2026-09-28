using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ai.Configuration;
using Jellyfin.Plugin.Ai.Models;
using Jellyfin.Plugin.Common.Resilience;
using Xunit;

namespace Jellyfin.Plugin.Ai.Tests;

// Gemini through common's provider HTTP code, against canned replies
public sealed class GeminiModelTests : IDisposable
{
    private const string Key = "AIza-test-SECRET-123456";
    private readonly FakeHttp _fake = new();
    private readonly HttpClient _http;
    private readonly List<TimeSpan> _waits = [];

    public GeminiModelTests() => _http = _fake.Client();

    public void Dispose()
    {
        _http.Dispose();
        _fake.Dispose();
    }

    private GeminiModel Gemini(string model = "gemini-3.8-flash")
        => new(Key, model, _http, ownsHttp: false, (w, _) =>
        {
            _waits.Add(w);
            return Task.CompletedTask;
        });

    private static string Reply(string text, string finish = "STOP", string extraParts = "")
        => "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[" + extraParts + "{\"text\":" + JsonSerializer.Serialize(text) + "}]},\"finishReason\":\"" + finish + "\"}],"
            + "\"usageMetadata\":{\"promptTokenCount\":200,\"candidatesTokenCount\":20,\"thoughtsTokenCount\":80,\"totalTokenCount\":300},\"modelVersion\":\"gemini-3.8-flash-001\"}";

    [Fact]
    public async Task A_request_keeps_instructions_and_data_apart_and_asks_for_json()
    {
        _fake.Reply(Reply("{\"pick\":2}", extraParts: "{\"text\":\"thinking out loud\",\"thought\":true},"));

        var answer = await Gemini().AskAsync(OpenAiChatModelTests.Request(), TestContext.Current.CancellationToken);

        Assert.Equal(2, answer.Json.GetProperty("pick").GetInt32());
        Assert.Equal("gemini-3.8-flash-001", answer.Model);
        Assert.Equal(KnownProviders.Google, answer.Provider);

        // Thinking is billed as output
        Assert.Equal((200, 100), (answer.InputTokens, answer.OutputTokens));

        var sent = Assert.Single(_fake.Seen);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-3.8-flash:generateContent", sent.Uri.ToString());
        Assert.Equal(Key, sent.Headers["x-goog-api-key"]);
        Assert.DoesNotContain(Key, sent.Uri.ToString(), StringComparison.Ordinal);
        using var body = JsonDocument.Parse(sent.Body);
        var root = body.RootElement;
        Assert.Contains("untrusted", root.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("<data>", root.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        var generation = root.GetProperty("generationConfig");
        Assert.Equal("application/json", generation.GetProperty("responseMimeType").GetString());
        Assert.Equal("object", generation.GetProperty("responseJsonSchema").GetProperty("type").GetString());
        Assert.Equal(500, generation.GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal("high", generation.GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString());
    }

    [Fact]
    public void Earlier_models_choose_their_own_thinking()
    {
        Assert.DoesNotContain("thinkingConfig", GeminiModel.Body(OpenAiChatModelTests.Request(), "gemini-2.5-flash"), StringComparison.Ordinal);
        Assert.Equal(3, GeminiModel.Generation("gemini-3.1-pro-preview"));
        Assert.Null(GeminiModel.Generation("gemma-3"));
    }

    [Theory]
    [InlineData("MAX_TOKENS", "cut off")]
    [InlineData("SAFETY", "declined")]
    public async Task Unusable_billed_replies_carry_their_usage(string finish, string words)
    {
        _fake.Reply(Reply("{\"pick\":", finish));

        var ex = await Assert.ThrowsAsync<AiException>(() => Gemini().AskAsync(OpenAiChatModelTests.Request(), TestContext.Current.CancellationToken));

        Assert.Contains(words, ex.Message, StringComparison.Ordinal);
        Assert.True(ex.Charged);
        Assert.Equal((200, 100), (ex.InputTokens, ex.OutputTokens));
    }

    [Fact]
    public async Task A_blocked_prompt_is_declined()
    {
        _fake.Reply("{\"promptFeedback\":{\"blockReason\":\"SAFETY\"},\"usageMetadata\":{\"promptTokenCount\":50,\"totalTokenCount\":50}}");

        var ex = await Assert.ThrowsAsync<AiException>(() => Gemini().AskAsync(OpenAiChatModelTests.Request(), TestContext.Current.CancellationToken));

        Assert.Equal(FailureClass.BadRequest, ex.Failure);
        Assert.Equal(50, ex.InputTokens);
    }

    [Fact]
    public async Task A_bad_key_is_authentication_and_never_shown()
    {
        _fake.Reply("{\"error\":{\"code\":400,\"message\":\"API key not valid. Please pass a valid API key.\",\"status\":\"INVALID_ARGUMENT\",\"details\":[{\"reason\":\"API_KEY_INVALID\"}]}}", HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<AiException>(() => Gemini().AskAsync(OpenAiChatModelTests.Request(), TestContext.Current.CancellationToken));

        Assert.Equal(FailureClass.Authentication, ex.Failure);
        Assert.DoesNotContain(Key, ex.Message, StringComparison.Ordinal);
        Assert.Single(_fake.Seen);
    }

    [Fact]
    public async Task A_short_rate_limit_is_waited_out_and_tried_again()
    {
        _fake.Reply("{\"error\":{\"code\":429,\"message\":\"Quota exceeded for metric: generate_content_requests per minute\",\"status\":\"RESOURCE_EXHAUSTED\",\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.RetryInfo\",\"retryDelay\":\"8s\"}]}}", HttpStatusCode.TooManyRequests)
            .Reply(Reply("{\"pick\":1}"));

        var answer = await Gemini().AskAsync(OpenAiChatModelTests.Request(), TestContext.Current.CancellationToken);

        Assert.Equal(1, answer.Json.GetProperty("pick").GetInt32());
        var wait = Assert.Single(_waits);
        Assert.InRange(wait, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(8.9));
    }

    [Fact]
    public async Task A_daily_quota_is_a_provider_limit_with_its_reset()
    {
        _fake.Reply("{\"error\":{\"code\":429,\"message\":\"You exceeded your current quota, please check your plan and billing details. Quota exceeded for metric: GenerateRequestsPerDayPerProjectPerModel-FreeTier\",\"status\":\"RESOURCE_EXHAUSTED\",\"details\":[{\"retryDelay\":\"30s\"}]}}", HttpStatusCode.TooManyRequests);

        var ex = await Assert.ThrowsAsync<AiException>(() => Gemini().AskAsync(OpenAiChatModelTests.Request(), TestContext.Current.CancellationToken));

        Assert.Equal(FailureClass.ProviderLimit, ex.Failure);
        Assert.Empty(_waits);
    }

    [Fact]
    public async Task Billing_not_set_up_is_a_provider_limit()
    {
        _fake.Reply("{\"error\":{\"code\":400,\"message\":\"User location is not supported for the API use.\",\"status\":\"FAILED_PRECONDITION\"}}", HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<AiException>(() => Gemini().AskAsync(OpenAiChatModelTests.Request(), TestContext.Current.CancellationToken));

        Assert.Equal(FailureClass.ProviderLimit, ex.Failure);
    }

    [Fact]
    public async Task Server_trouble_is_tried_twice_more_then_left_to_the_caller()
    {
        _fake.Reply("{}", HttpStatusCode.ServiceUnavailable).Reply("{}", HttpStatusCode.ServiceUnavailable).Reply("{}", HttpStatusCode.ServiceUnavailable);

        var ex = await Assert.ThrowsAsync<AiException>(() => Gemini().AskAsync(OpenAiChatModelTests.Request(), TestContext.Current.CancellationToken));

        Assert.Equal(FailureClass.Transient, ex.Failure);
        Assert.Equal(3, _fake.Seen.Count);
        Assert.Equal(2, _waits.Count);
    }

    [Fact]
    public async Task Models_that_generate_text_are_listed_without_their_prefix()
    {
        _fake.Reply("{\"models\":[{\"name\":\"models/gemini-3.8-flash\",\"supportedGenerationMethods\":[\"generateContent\",\"countTokens\"]},{\"name\":\"models/text-embedding-004\",\"supportedGenerationMethods\":[\"embedContent\"]}],\"nextPageToken\":\"p2\"}")
            .Reply("{\"models\":[{\"name\":\"models/gemini-3.1-pro-preview\",\"supportedGenerationMethods\":[\"generateContent\"]}]}");

        var models = await GeminiModel.ListAsync(Key, _http, TestContext.Current.CancellationToken);

        Assert.Equal(["gemini-3.8-flash", "gemini-3.1-pro-preview"], models);
        Assert.Contains("pageToken=p2", _fake.Seen[1].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_network_is_no_connection()
    {
        _fake.Throw(new HttpRequestException("No such host is known", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound)));

        var ex = await Assert.ThrowsAsync<AiException>(() => Gemini().AskAsync(OpenAiChatModelTests.Request(), TestContext.Current.CancellationToken));

        Assert.Equal(FailureClass.NoConnection, ex.Failure);
    }

    [Fact]
    public async Task Cancelling_stops_without_a_failure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _fake.Throw(new TaskCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Gemini().AskAsync(OpenAiChatModelTests.Request(), cts.Token));
    }
}
