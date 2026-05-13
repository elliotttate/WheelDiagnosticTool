using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WheelDiagnosticTool.Services;

/// <summary>
/// Uploads files to filebin.net using its dead-simple POST API.
///
/// API contract (https://filebin.net):
///   POST https://filebin.net/{bin}/{filename}
///   body = raw file bytes
///   Response: 201 Created. The shareable URL is
///   https://filebin.net/{bin}/{filename}.
///
/// Bins auto-expire (~6 days). We generate a random bin name per session
/// so multiple files in one session (.txt + .json) sit in the same bin.
/// </summary>
public sealed class FilebinUploader
{
    private static readonly HttpClient s_http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
    })
    { Timeout = TimeSpan.FromSeconds(60) };

    private const string FilebinBase = "https://filebin.net";

    public sealed record UploadResult(bool Ok, string? Url, string? Error);

    public async Task<UploadResult> UploadAsync(string localPath, string? bin = null, CancellationToken ct = default)
    {
        if (!File.Exists(localPath))
            return new UploadResult(false, null, $"file not found: {localPath}");

        bin ??= GenerateBinName();
        var fileName = Path.GetFileName(localPath);
        var url = $"{FilebinBase}/{bin}/{Uri.EscapeDataString(fileName)}";

        // Pick content type based on extension — filebin doesn't really care
        // but it makes the preview behave better.
        string ext = Path.GetExtension(localPath).ToLowerInvariant();
        string mime = ext switch
        {
            ".json" => "application/json",
            ".txt" => "text/plain",
            _ => "application/octet-stream",
        };

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(localPath, ct);
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new ByteArrayContent(bytes);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue(mime) { CharSet = "utf-8" };
            req.Content.Headers.ContentLength = bytes.Length;
            req.Headers.UserAgent.ParseAdd("WheelDiagnosticTool/1.0 (+filebin.net)");
            req.Headers.Accept.ParseAdd("application/json");

            using var resp = await s_http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);

            if ((int)resp.StatusCode is >= 200 and < 300)
            {
                return new UploadResult(true, url, null);
            }
            return new UploadResult(false, null, $"HTTP {(int)resp.StatusCode}: {body[..Math.Min(body.Length, 200)]}");
        }
        catch (OperationCanceledException)
        {
            return new UploadResult(false, null, "upload cancelled");
        }
        catch (Exception ex)
        {
            return new UploadResult(false, null, ex.Message);
        }
    }

    public static string GenerateBinName()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        var bytes = new byte[12];
        rng.GetBytes(bytes);
        var sb = new StringBuilder(12);
        for (int i = 0; i < bytes.Length; i++)
            sb.Append(alphabet[bytes[i] % alphabet.Length]);
        return "wheeldiag-" + sb.ToString();
    }
}
