using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace LoginAndReg.Tests;

/// <summary>
/// Request helpers mirroring the Auth test suite: a plain in-memory handler
/// (no cookie container) so cookies are passed explicitly via the Cookie
/// header — the monolith reads the JWT from the "jwt" cookie, not the
/// Authorization header.
/// </summary>
public static class ApiHelpers
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static string UniqueLogin() => "user" + Guid.NewGuid().ToString("N")[..16];

    public static async Task<HttpResponseMessage> Send<TEntryPoint>(
        WebApplicationFactory<TEntryPoint> factory,
        HttpMethod method,
        string url,
        object? body = null,
        string? jwtToken = null,
        MultipartFormDataContent? multipart = null)
        where TEntryPoint : class
    {
        using var client = new HttpClient(factory.Server.CreateHandler())
        {
            BaseAddress = factory.Server.BaseAddress,
        };

        var request = new HttpRequestMessage(method, url);
        if (multipart is not null)
        {
            request.Content = multipart;
        }
        else if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json");
        }

        if (jwtToken is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"jwt={jwtToken}");
        }

        return await client.SendAsync(request);
    }

    /// <summary>
    /// Adds a minimal valid GLB (magic + version 2 + totalLength header only,
    /// the server-side validation's full depth) as the "file" form field.
    /// </summary>
    public static MultipartFormDataContent GlbUpload(string fileName, int extraBytes = 0)
    {
        var content = new MultipartFormDataContent();
        var header = new byte[12 + extraBytes];
        Encoding.ASCII.GetBytes("glTF").CopyTo(header, 0);
        header[4] = 2; // version 2 (little-endian int32, low byte)
        header[8] = 12; // totalLength = 12 (low byte)
        var fileContent = new ByteArrayContent(header);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("model/gltf-binary");
        content.Add(fileContent, "file", fileName);
        return content;
    }
}
