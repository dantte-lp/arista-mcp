using System.Text.Json.Serialization;

namespace AristaMcp.Cli.Commands;

[JsonSerializable(typeof(QueryRecord))]
[JsonSerializable(typeof(ValidatedQueryRecord))]
internal sealed partial class BenchQueryJsonContext : JsonSerializerContext;
