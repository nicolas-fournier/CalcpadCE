using System.Text.Json;
using Calcpad.AI;

namespace Calcpad.Tests;

// Phase B4 — agentic assistant: tool loop + Calcpad tools, exercised with a fake model caller
// (no network). Verifies tools run, results are fed back, and the loop terminates correctly.
public class AgenticConversationTests
{
    private sealed class ScriptedCaller : IModelCaller
    {
        private readonly Queue<ModelReply> _replies;
        public List<IReadOnlyList<AgentMessage>> Received { get; } = new();
        public int Calls { get; private set; }

        public ScriptedCaller(params ModelReply[] replies) => _replies = new(replies);

        public Task<ModelReply> CallAsync(
            IReadOnlyList<AgentMessage> messages, IReadOnlyList<AiTool> tools, CancellationToken ct = default)
        {
            ++Calls;
            Received.Add(messages.ToList());
            var reply = _replies.Count > 0 ? _replies.Dequeue() : new ModelReply("stop", []);
            return Task.FromResult(reply);
        }
    }

    private static IReadOnlyDictionary<string, JsonElement> Args(params (string, string)[] kv)
    {
        var d = new Dictionary<string, JsonElement>();
        foreach (var (k, v) in kv)
            d[k] = JsonSerializer.SerializeToElement(v);
        return d;
    }

    private static (IReadOnlyList<AiTool> tools, List<string> log, Func<string> evalArg) MakeTools()
    {
        var log = new List<string>();
        string lastEval = "";
        var tools = CalcpadAgentTools.Create(
            getWorksheet: () => "WORKSHEET",
            insertText: t => { log.Add("insert:" + t); return "Applied."; },
            evaluate: s => { lastEval = s; return "EVAL(" + s + ")"; });
        return (tools, log, () => lastEval);
    }

    [Fact]
    public async Task NoToolCalls_ReturnsTextInOneCall()
    {
        var (tools, _, _) = MakeTools();
        var caller = new ScriptedCaller(new ModelReply("Hello!", []));
        var convo = new AgenticConversation(caller, tools);

        var final = await convo.RunAsync([AgentMessage.User("hi")]);

        Assert.Equal("Hello!", final);
        Assert.Equal(1, caller.Calls);
    }

    [Fact]
    public async Task GetWorksheetTool_FeedsResultBack()
    {
        var (tools, _, _) = MakeTools();
        var caller = new ScriptedCaller(
            new ModelReply("", [new AgentToolCall("id1", "get_worksheet", Args())]),
            new ModelReply("Here it is.", []));
        var convo = new AgenticConversation(caller, tools);

        var final = await convo.RunAsync([AgentMessage.User("show worksheet")]);

        Assert.Equal("Here it is.", final);
        Assert.Equal(2, caller.Calls);
        // The second round-trip must carry the tool result containing the worksheet.
        var toolTurn = caller.Received[1].Last();
        Assert.Equal("WORKSHEET", toolTurn.ToolResults[0].Content);
        Assert.False(toolTurn.ToolResults[0].IsError);
    }

    [Fact]
    public async Task InsertTool_InvokesHostCallback()
    {
        var (tools, log, _) = MakeTools();
        var caller = new ScriptedCaller(
            new ModelReply("", [new AgentToolCall("id1", "insert_calcpad", Args(("text", "$x = 1$")))]),
            new ModelReply("Inserted.", []));
        var convo = new AgenticConversation(caller, tools);

        await convo.RunAsync([AgentMessage.User("insert x")]);

        Assert.Contains("insert:$x = 1$", log);
        Assert.Contains("Applied.", caller.Received[1].Last().ToolResults[0].Content);
    }

    [Fact]
    public async Task EvaluateTool_ReturnsComputedText()
    {
        var (tools, _, evalArg) = MakeTools();
        var caller = new ScriptedCaller(
            new ModelReply("", [new AgentToolCall("id1", "evaluate_calcpad", Args(("source", "$2 + 3$")))]),
            new ModelReply("The answer is 5.", []));
        var convo = new AgenticConversation(caller, tools);

        await convo.RunAsync([AgentMessage.User("compute 2+3")]);

        Assert.Equal("$2 + 3$", evalArg());
        Assert.Equal("EVAL($2 + 3$)", caller.Received[1].Last().ToolResults[0].Content);
    }

    [Fact]
    public async Task UnknownTool_ProducesErrorResult()
    {
        var (tools, _, _) = MakeTools();
        var caller = new ScriptedCaller(
            new ModelReply("", [new AgentToolCall("id1", "does_not_exist", Args())]),
            new ModelReply("ok", []));
        var convo = new AgenticConversation(caller, tools);

        await convo.RunAsync([AgentMessage.User("go")]);

        var result = caller.Received[1].Last().ToolResults[0];
        Assert.True(result.IsError);
        Assert.Contains("Unknown tool", result.Content);
    }

    [Fact]
    public async Task LoopStopsAtMaxIterations()
    {
        var (tools, _, _) = MakeTools();
        // Always asks for a tool → would loop forever without the cap.
        var replies = Enumerable.Range(0, 10)
            .Select(_ => new ModelReply("working", [new AgentToolCall("id", "get_worksheet", Args())]))
            .ToArray();
        var caller = new ScriptedCaller(replies);
        var convo = new AgenticConversation(caller, tools) { MaxIterations = 3 };

        var final = await convo.RunAsync([AgentMessage.User("loop")]);

        Assert.Equal("working", final);
        Assert.Equal(3, caller.Calls);
    }

    [Fact]
    public void Tools_HaveExpectedSchema()
    {
        var (tools, _, _) = MakeTools();
        Assert.Equal(3, tools.Count);
        var insert = tools.First(t => t.Name == "insert_calcpad");
        Assert.Contains("text", insert.Required);
        var get = tools.First(t => t.Name == "get_worksheet");
        Assert.Empty(get.Required);
    }

    [Fact]
    public void GetResultTool_AddedWhenProvided()
    {
        var tools = CalcpadAgentTools.Create(
            getWorksheet: () => "W",
            insertText: _ => "ok",
            evaluate: _ => "e",
            getResult: () => "RESULT-OUTPUT");
        Assert.Equal(4, tools.Count);
        Assert.Contains(tools, t => t.Name == "get_result");
    }
}
