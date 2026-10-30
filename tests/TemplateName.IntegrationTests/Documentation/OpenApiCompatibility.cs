using System.Text.Json;
using System.Text.Json.Nodes;

namespace TemplateName.IntegrationTests.Documentation;

/// <summary>One difference between two OpenAPI documents: the operation (<c>GET /api/v1/users/{id}</c>) and what changed in it.</summary>
public sealed record OpenApiChange(string Operation, string Description);

/// <summary>The differences that break an existing client, and those that do not; each list ordered by operation, then description.</summary>
public sealed record OpenApiCompatibilityReport(IReadOnlyList<OpenApiChange> Breaking, IReadOnlyList<OpenApiChange> Compatible);

/// <summary>
/// The breaking-change gate (docs/api-compatibility.md, ADR 0031): classifies the differences between the base branch's OpenAPI document
/// and the current one. Request side means the parameters and the request body; response side means the bodies of the 2xx responses.
/// <c>$ref</c> is resolved, <c>allOf</c> members are merged, and a <c>oneOf</c> or <c>anyOf</c> is compared as a whole, so any change to
/// one is breaking even where a client would cope. A schema already being compared on the current path is not entered again.
/// </summary>
public static class OpenApiCompatibility
{
    /// <summary>The environment variable naming the baseline document (the base branch's verified snapshot); CI sets it on a pull request.</summary>
    public const string BaselineVariable = "OPENAPI_BASELINE";

    /// <summary>The environment variable CI sets to <c>true</c> when the pull-request title declares a breaking change with <c>!</c>.</summary>
    public const string AcceptedVariable = "API_BREAKING_ACCEPTED";

    private static readonly string[] Methods = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    /// <summary>Classifies every difference between the two documents; neither is changed.</summary>
    public static OpenApiCompatibilityReport Compare(string baselineJson, string currentJson)
    {
        var comparison = new Comparison(ParseObject(baselineJson, nameof(baselineJson)), ParseObject(currentJson, nameof(currentJson)));
        comparison.CompareOperations();
        return comparison.Report();
    }

    /// <summary>Returns the text of the baseline file, after checking that it holds a JSON object.</summary>
    /// <exception cref="InvalidOperationException">The file is not a JSON object; the message names it.</exception>
    public static string ReadBaseline(string path)
    {
        var text = File.ReadAllText(path);
        var problem = $"The OpenAPI baseline {path} is not a JSON object; set {BaselineVariable} to a verified OpenAPI snapshot.";
        try
        {
            return JsonNode.Parse(text) is JsonObject ? text : throw new InvalidOperationException(problem);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(problem, exception);
        }
    }

    /// <summary>
    /// Returns <see langword="null"/> when nothing is breaking or the break is accepted; otherwise a message listing each breaking change
    /// and the two ways out.
    /// </summary>
    public static string? Verdict(OpenApiCompatibilityReport report, bool breakingAccepted)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Breaking.Count == 0 || breakingAccepted)
        {
            return null;
        }

        var changes = string.Concat(report.Breaking.Select(change => $"\n  {change.Operation}: {change.Description}"));
        return $"These changes to the OpenAPI document break clients written against the base branch:{changes}\n"
            + "If the break is intended, mark the pull-request title with ! (for example feat(api)!: ...) and push again, since CI reads "
            + "the title when it runs. To keep existing clients working instead, add a new API version as docs/api-compatibility.md describes.";
    }

    private static JsonObject ParseObject(string json, string parameterName)
        => JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("The OpenAPI document is not a JSON object.", parameterName);

    private static JsonNode? Child(JsonNode? node, string name) => node is JsonObject json ? json[name] : null;

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool IsTrue(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static string? ReferenceOf(JsonNode? node) => Text(Child(node, "$ref"));

    /// <summary>Follows a local <c>$ref</c> (a JSON pointer such as <c>#/components/schemas/User</c>); anything else is returned as is.</summary>
    private static JsonNode? Resolve(JsonObject root, JsonNode? node)
    {
        for (var hop = 0; hop < 16 && ReferenceOf(node) is { } reference && reference.StartsWith("#/", StringComparison.Ordinal); hop++)
        {
            JsonNode? target = root;
            foreach (var segment in reference[2..].Split('/'))
            {
                target = Child(target, segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
            }

            if (target is null)
            {
                return node;
            }

            node = target;
        }

        return node;
    }

    /// <summary>Resolves the schema and merges its <c>allOf</c> members into it: the union of their properties and required names.</summary>
    private static JsonObject? Merge(JsonObject root, JsonNode? node, HashSet<string>? merging = null)
    {
        var schema = Resolve(root, node) as JsonObject;
        if (schema?["allOf"] is not JsonArray members)
        {
            return schema;
        }

        // A member already merged on this schema (a cycle through allOf) is skipped.
        merging ??= [];
        var parts = new List<JsonObject> { schema };
        foreach (var member in members)
        {
            var reference = ReferenceOf(member);
            if ((reference is null || merging.Add(reference)) && Merge(root, member, merging) is { } part)
            {
                parts.Add(part);
            }
        }

        var merged = new JsonObject();
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var part in parts)
        {
            foreach (var (key, value) in part.Where(pair => pair.Key is not ("allOf" or "properties" or "required")))
            {
                merged.TryAdd(key, value?.DeepClone());
            }

            foreach (var (name, property) in part["properties"] as JsonObject ?? [])
            {
                properties.TryAdd(name, property?.DeepClone());
            }

            foreach (var name in part["required"] as JsonArray ?? [])
            {
                required.Add(name?.DeepClone());
            }
        }

        merged["properties"] = properties;
        merged["required"] = required;
        return merged;
    }

    private static HashSet<string> RequiredNames(JsonObject schema)
        => [.. (schema["required"] as JsonArray ?? []).Select(Text).OfType<string>()];

    private static string Types(JsonObject schema)
    {
        var types = schema["type"] switch
        {
            JsonArray array => array.Select(Text).OfType<string>().Order(StringComparer.Ordinal).ToList(),
            var single when Text(single) is { } type => [type],
            _ => [],
        };
        return types.Count == 0 ? "none" : string.Join('|', types);
    }

    private sealed class Comparison(JsonObject baseline, JsonObject current)
    {
        private readonly List<OpenApiChange> _breaking = [];
        private readonly List<OpenApiChange> _compatible = [];
        private readonly HashSet<string> _visiting = [];
        private string _operation = "";

        public OpenApiCompatibilityReport Report() => new(Ordered(_breaking), Ordered(_compatible));

        /// <summary>Operations removed or added, and security added to an operation that had none; then each kept operation's parts.</summary>
        public void CompareOperations()
        {
            var baselinePaths = baseline["paths"] as JsonObject ?? [];
            var currentPaths = current["paths"] as JsonObject ?? [];
            foreach (var (path, baselineItem) in baselinePaths)
            {
                var currentItem = currentPaths[path];
                foreach (var method in Methods)
                {
                    if (Child(baselineItem, method) is not JsonObject baselineOperation)
                    {
                        continue;
                    }

                    _operation = $"{method.ToUpperInvariant()} {path}";
                    if (currentItem is null)
                    {
                        Breaking("path removed");
                    }
                    else if (Child(currentItem, method) is not JsonObject currentOperation)
                    {
                        Breaking("operation removed");
                    }
                    else
                    {
                        if (IsOpen(baseline, baselineOperation) && !IsOpen(current, currentOperation))
                        {
                            Breaking("security requirement added");
                        }

                        CompareParameters(baselineOperation, currentOperation);
                        CompareRequestBody(baselineOperation, currentOperation);
                        CompareResponses(baselineOperation, currentOperation);
                    }
                }
            }

            foreach (var (path, currentItem) in currentPaths)
            {
                foreach (var method in Methods.Where(method => Child(currentItem, method) is JsonObject))
                {
                    if (Child(baselinePaths[path], method) is null)
                    {
                        _operation = $"{method.ToUpperInvariant()} {path}";
                        Compatible("operation added");
                    }
                }
            }
        }

        private static bool IsOpen(JsonObject root, JsonObject operation)
            => (operation["security"] ?? root["security"]) is not JsonArray requirements
                || requirements.Count == 0
                || requirements.Any(requirement => requirement is JsonObject { Count: 0 });

        private static List<OpenApiChange> Ordered(List<OpenApiChange> changes)
            => [.. changes.Distinct().OrderBy(change => change.Operation, StringComparer.Ordinal).ThenBy(change => change.Description, StringComparer.Ordinal)];

        private static Dictionary<(string In, string Name), JsonObject> Parameters(JsonObject root, JsonObject operation)
        {
            var parameters = new Dictionary<(string In, string Name), JsonObject>();
            foreach (var parameter in (operation["parameters"] as JsonArray ?? []).Select(node => Resolve(root, node)).OfType<JsonObject>())
            {
                parameters.TryAdd((Text(parameter["in"]) ?? "", Text(parameter["name"]) ?? ""), parameter);
            }

            return parameters;
        }

        /// <summary>A required parameter added, or an optional one made required, breaks; an optional one added does not.</summary>
        private void CompareParameters(JsonObject baselineOperation, JsonObject currentOperation)
        {
            var baselineParameters = Parameters(baseline, baselineOperation);
            foreach (var ((location, name), parameter) in Parameters(current, currentOperation))
            {
                var label = $"{location} parameter '{name}'";
                var isRequired = IsTrue(parameter["required"]);
                if (!baselineParameters.TryGetValue((location, name), out var baselineParameter))
                {
                    if (isRequired)
                    {
                        Breaking($"required {label} added");
                    }
                    else
                    {
                        Compatible($"optional {label} added");
                    }

                    continue;
                }

                if (isRequired && !IsTrue(baselineParameter["required"]))
                {
                    Breaking($"{label} made required");
                }

                CompareSchemas(baselineParameter["schema"], parameter["schema"], isRequest: true, label, "");
            }
        }

        private void CompareRequestBody(JsonObject baselineOperation, JsonObject currentOperation)
        {
            var baselineBody = Resolve(baseline, baselineOperation["requestBody"]) as JsonObject;
            if (Resolve(current, currentOperation["requestBody"]) is not JsonObject currentBody)
            {
                return;
            }

            if (IsTrue(currentBody["required"]) && !IsTrue(baselineBody?["required"]))
            {
                Breaking("request body made required");
            }

            CompareContent(baselineBody, currentBody, isRequest: true, "request body");
        }

        /// <summary>A success status removed breaks; any other status is the client's error handling and is not compared.</summary>
        private void CompareResponses(JsonObject baselineOperation, JsonObject currentOperation)
        {
            foreach (var (status, baselineResponse) in baselineOperation["responses"] as JsonObject ?? [])
            {
                if (!status.StartsWith('2'))
                {
                    continue;
                }

                if (Resolve(current, Child(currentOperation["responses"], status)) is not JsonObject currentResponse)
                {
                    Breaking($"success status {status} removed");
                    continue;
                }

                CompareContent(Resolve(baseline, baselineResponse) as JsonObject, currentResponse, isRequest: false, $"response {status}");
            }
        }

        private void CompareContent(JsonObject? baselineHolder, JsonObject currentHolder, bool isRequest, string location)
        {
            foreach (var (mediaType, baselineMedia) in Child(baselineHolder, "content") as JsonObject ?? [])
            {
                if (Child(currentHolder["content"], mediaType) is JsonObject currentMedia)
                {
                    CompareSchemas(Child(baselineMedia, "schema"), currentMedia["schema"], isRequest, location, "");
                }
            }
        }

        /// <summary>
        /// Compares two schemas at <paramref name="path"/> (<c>items[].name</c>; empty for the body itself). Request side: a required
        /// property added or an optional one made required, or an enum value removed, breaks. Response side: a property removed or no longer
        /// required breaks; a property or an enum value added does not. Either side: a type, format, oneOf or anyOf change breaks.
        /// </summary>
        private void CompareSchemas(JsonNode? baselineNode, JsonNode? currentNode, bool isRequest, string location, string path)
        {
            var key = $"{ReferenceOf(baselineNode)}|{ReferenceOf(currentNode)}";
            if (_visiting.Contains(key)
                || Merge(baseline, baselineNode) is not JsonObject old
                || Merge(current, currentNode) is not JsonObject @new)
            {
                return;
            }

            var isReference = key != "|";
            if (isReference)
            {
                _visiting.Add(key);
            }

            var of = path.Length == 0 ? "" : $" of '{path}'";
            foreach (var keyword in (string[])["oneOf", "anyOf"])
            {
                if (!JsonNode.DeepEquals(old[keyword], @new[keyword]))
                {
                    Breaking($"{location}: {keyword}{of} changed");
                }
                else if (old[keyword] is JsonArray variants)
                {
                    for (var index = 0; index < variants.Count; index++)
                    {
                        CompareSchemas(variants[index], ((JsonArray)@new[keyword]!)[index], isRequest, location, path);
                    }
                }
            }

            if (Types(old) != Types(@new))
            {
                Breaking($"{location}: type{of} changed from {Types(old)} to {Types(@new)}");
            }

            if (Text(old["format"]) != Text(@new["format"]))
            {
                Breaking($"{location}: format{of} changed from {Text(old["format"]) ?? "none"} to {Text(@new["format"]) ?? "none"}");
            }

            if (old["enum"] is JsonArray oldValues && @new["enum"] is JsonArray newValues)
            {
                var before = oldValues.Select(value => value?.ToJsonString() ?? "null").ToList();
                var after = newValues.Select(value => value?.ToJsonString() ?? "null").ToList();
                if (isRequest)
                {
                    before.Except(after).ToList().ForEach(value => Breaking($"{location}: value {value} removed from the enum{of}"));
                }
                else
                {
                    after.Except(before).ToList().ForEach(value => Compatible($"{location}: value {value} added to the enum{of}"));
                }
            }

            CompareProperties(old, @new, isRequest, location, path);
            CompareSchemas(old["items"], @new["items"], isRequest, location, $"{path}[]");

            if (isReference)
            {
                _visiting.Remove(key);
            }
        }

        private void CompareProperties(JsonObject old, JsonObject @new, bool isRequest, string location, string path)
        {
            var oldProperties = old["properties"] as JsonObject ?? [];
            var newProperties = @new["properties"] as JsonObject ?? [];
            var oldRequired = RequiredNames(old);
            var newRequired = RequiredNames(@new);
            foreach (var (name, newProperty) in newProperties)
            {
                var property = path.Length == 0 ? name : $"{path}.{name}";
                if (!oldProperties.TryGetPropertyValue(name, out var oldProperty))
                {
                    if (!isRequest)
                    {
                        Compatible($"{location}: property '{property}' added");
                    }
                    else if (newRequired.Contains(name))
                    {
                        Breaking($"{location}: required property '{property}' added");
                    }
                    else
                    {
                        Compatible($"{location}: optional property '{property}' added");
                    }

                    continue;
                }

                if (isRequest && newRequired.Contains(name) && !oldRequired.Contains(name))
                {
                    Breaking($"{location}: property '{property}' made required");
                }
                else if (!isRequest && oldRequired.Contains(name) && !newRequired.Contains(name))
                {
                    Breaking($"{location}: property '{property}' no longer required");
                }

                CompareSchemas(oldProperty, newProperty, isRequest, location, property);
            }

            if (!isRequest)
            {
                foreach (var (name, _) in oldProperties.Where(pair => !newProperties.ContainsKey(pair.Key)))
                {
                    Breaking($"{location}: property '{(path.Length == 0 ? name : $"{path}.{name}")}' removed");
                }
            }
        }

        private void Breaking(string description) => _breaking.Add(new OpenApiChange(_operation, description));

        private void Compatible(string description) => _compatible.Add(new OpenApiChange(_operation, description));
    }
}
