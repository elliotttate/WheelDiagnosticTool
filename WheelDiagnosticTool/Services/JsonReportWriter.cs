using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WheelDiagnosticTool.Models;

namespace WheelDiagnosticTool.Services;

/// <summary>
/// Serializes the full DiagnosticSession to a stable JSON sidecar. The
/// schema version sits on the DiagnosticSession itself so consumers can
/// branch on it.
/// </summary>
public static class JsonReportWriter
{
    private static readonly JsonSerializerOptions s_opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string WriteToFile(DiagnosticSession s, string txtPath)
    {
        var jsonPath = Path.ChangeExtension(txtPath, ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(s, s_opts));
        s.LocalJsonPath = jsonPath;
        return jsonPath;
    }
}
