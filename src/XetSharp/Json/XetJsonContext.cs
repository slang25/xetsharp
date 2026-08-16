using System.Text.Json.Serialization;
using XetSharp.Cas;
using XetSharp.Hub;

namespace XetSharp.Json;

/// <summary>
/// Source-generated serialization metadata for every JSON payload the protocol uses. Reflection-free,
/// so the library stays trimmable and AOT-compatible.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(XetTokenJson))]
[JsonSerializable(typeof(ReconstructionJson))]
internal sealed partial class XetJsonContext : JsonSerializerContext;
