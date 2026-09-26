using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ai.Models;
using Jellyfin.Plugin.Ai.Pricing;
using Jellyfin.Plugin.Common.Costs;
using Xunit;

namespace Jellyfin.Plugin.Ai.Tests;

// Review pass 3: AI-04 (a billed reply that can't be used is recorded at what it used, not released)
public sealed class BilledFailureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ai-billed-" + Guid.NewGuid().ToString("N"));

    public BilledFailureTests()
    {
        Directory.CreateDirectory(_dir);
        var today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(_dir, "rates.json"), "{\"Date\":\"" + today + "\",\"Source\":\"test\",\"Rates\":{\"EUR\":1,\"USD\":1.10,\"AUD\":1.65}}");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static AiRequest Request() => new()
    {
        Purpose = "test",
        Instructions = "Pick one.",
        Data = "{}",
        Schema = new Dictionary<string, JsonElement> { ["type"] = JsonSerializer.SerializeToElement("object") },
        MaxOutputTokens = 1000,
    };

    private static SpendLimits Usd() => new("USD", 5m, new Dictionary<string, decimal>(), 0m);

    // A canned Messages API reply; nothing leaves the process
    private static ClaudeModel Claude(string stopReason, string text, out HttpClient http)
    {
        var body = JsonSerializer.Serialize(new
        {
            id = "msg_test",
            type = "message",
            role = "assistant",
            model = ClaudeModel.DefaultModel,
            content = new[] { new { type = "text", text } },
            stop_reason = stopReason,
            stop_sequence = (string?)null,
            usage = new { input_tokens = 100, output_tokens = 300 },
        });
        http = new HttpClient(new Canned(body));
        return new ClaudeModel("test-key", null, http);
    }

    [Theory]
    [InlineData("refusal", "{}", "declined")]
    [InlineData("max_tokens", "{\"pick\":", "cut off")]
    [InlineData("end_turn", "not json", "valid JSON")]
    public async Task Unusable_billed_replies_carry_their_usage(string stopReason, string text, string message)
    {
        using var claude = Claude(stopReason, text, out var http);
        using (http)
        {
            var ex = await Assert.ThrowsAsync<AiException>(() => claude.AskAsync(Request(), TestContext.Current.CancellationToken));

            Assert.Contains(message, ex.Message, StringComparison.Ordinal);
            Assert.True(ex.Charged);
            Assert.Equal(100, ex.InputTokens);
            Assert.Equal(300, ex.OutputTokens);
            Assert.Equal(ClaudeModel.DefaultModel, ex.ChargedModel);
        }
    }

    [Fact]
    public async Task A_cut_off_answer_is_recognised_by_its_stop_reason_not_its_wording()
    {
        // Valid JSON, but the reply says it stopped at the limit
        using var claude = Claude("max_tokens", "{}", out var http);
        using (http)
        {
            var ex = await Assert.ThrowsAsync<AiException>(() => claude.AskAsync(Request(), TestContext.Current.CancellationToken));
            Assert.True(ex.Charged);
        }
    }

    [Fact]
    public async Task A_billed_failure_is_recorded_at_its_actual_usage()
    {
        using var spending = new AiSpending(_dir);
        var metered = new MeteredModel(new Throws(new AiException("Claude declined to answer this request.") { Charged = true, ChargedModel = ClaudeModel.DefaultModel, InputTokens = 100, OutputTokens = 300 }), spending, Usd());

        await Assert.ThrowsAsync<AiException>(() => metered.AskAsync(Request(), TestContext.Current.CancellationToken));

        // 100 in + 300 out at 4/20 USD per million = 0.0064 USD, not released and not the whole reservation
        Assert.Equal(Money.Of(0.0064m, "USD"), metered.LastCost);
        Assert.Equal(0.0064m, spending.Ledger.ThisMonth(Usd(), spending.Rates.Current).Total);
    }

    [Fact]
    public async Task An_unanswered_failure_is_released_even_if_its_wording_says_cut_off()
    {
        using var spending = new AiSpending(_dir);
        var metered = new MeteredModel(new Throws(new AiException("The connection was cut off.")), spending, Usd());

        await Assert.ThrowsAsync<AiException>(() => metered.AskAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Null(metered.LastCost);
        Assert.Equal(0m, spending.Ledger.ThisMonth(Usd(), spending.Rates.Current).Total);
    }

    private sealed class Throws(AiException ex) : IAiModel
    {
        public string Provider => Configuration.KnownProviders.Anthropic;

        public string Model => ClaudeModel.DefaultModel;

        public Task<AiAnswer> AskAsync(AiRequest request, CancellationToken cancellationToken) => throw ex;
    }

    private sealed class Canned(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
