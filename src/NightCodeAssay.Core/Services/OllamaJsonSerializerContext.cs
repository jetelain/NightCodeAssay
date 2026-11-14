using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OllamaSharp.Models;
using OllamaSharp.Models.Chat;

namespace NightCodeAssay.Core.Services;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ChatRequest))]
[JsonSerializable(typeof(ChatResponseStream))]
[JsonSerializable(typeof(ChatDoneResponseStream))]
[JsonSerializable(typeof(Message))]
[JsonSerializable(typeof(Message.ToolCall))]
[JsonSerializable(typeof(Message.Function), TypeInfoPropertyName = "MessageFunction")]
[JsonSerializable(typeof(OllamaSharp.Models.Chat.ChatRole))]
[JsonSerializable(typeof(ThinkValue))]
[JsonSerializable(typeof(Tool))]
[JsonSerializable(typeof(Function), TypeInfoPropertyName = "ToolFunction")]
[JsonSerializable(typeof(Parameters))]
[JsonSerializable(typeof(Property))]
[JsonSerializable(typeof(RequestOptions))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(GenerateRequest))]
[JsonSerializable(typeof(GenerateResponseStream))]
[JsonSerializable(typeof(GenerateDoneResponseStream))]
[JsonSerializable(typeof(ListModelsResponse))]
[JsonSerializable(typeof(Model))]
[JsonSerializable(typeof(Details))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(IDictionary<string, object?>))]
public partial class OllamaJsonSerializerContext : JsonSerializerContext
{
}
