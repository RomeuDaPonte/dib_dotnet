using System.Text.Json;
using Microsoft.Net.Http.Headers;

namespace Dib4t.Server.Http;

/// <summary>
/// A JSON request body read like the legacy <c>express.json()</c> (body-parser 1.x) does, so the legacy Joi
/// validation sees the same value (PLAN.md §6): only <c>application/json</c> is parsed (another or no content type,
/// or an empty body → <c>{}</c>); strict mode (the text must start with <c>{</c> or <c>[</c>); 100 kB limit
/// (<c>"100kb"</c> → 413). A body that is not valid JSON → 400 <c>Bad Request</c> as plain text: the legacy app
/// answers with Express's HTML error page and stack trace there, the same leak D16 fixes for 500.
/// </summary>
public static class LegacyJsonBody
{
    /// <summary>body-parser's default <c>limit: "100kb"</c>.</summary>
    public const int MaxBytes = 100 * 1024;

    private const string JsonMediaType = "application/json";

    /// <summary>The parsed body, or (in <c>Error</c>) the legacy error response to send instead.</summary>
    public static async Task<(JsonElement Body, IResult? Error)> ReadAsync(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var empty = JsonElement.Parse("{}");
        if (!IsJson(request.ContentType))
        {
            return (empty, null);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, request.HttpContext.RequestAborted)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                return (empty, Text(StatusCodes.Status413PayloadTooLarge, "request entity too large"));
            }

            buffer.Write(chunk.AsSpan(0, read));
        }

        var bytes = buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
        if (bytes.Span.StartsWith("﻿"u8))
        {
            bytes = bytes[3..];
        }

        if (bytes.Length == 0)
        {
            return (empty, null);
        }

        var first = bytes.Span.IndexOfAnyExcept(" \t\n\r"u8);
        if (first < 0 || bytes.Span[first] is not ((byte)'{' or (byte)'['))
        {
            return (empty, Text(StatusCodes.Status400BadRequest, "Bad Request"));
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            return (document.RootElement.Clone(), null);
        }
        catch (JsonException)
        {
            return (empty, Text(StatusCodes.Status400BadRequest, "Bad Request"));
        }
    }

    private static IResult Text(int status, string text) =>
        Results.Text(text, LegacyResults.TextContentType, statusCode: status);

    /// <summary>body-parser's <c>type-is</c> match: the media type, case-insensitive, parameters ignored.</summary>
    private static bool IsJson(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
        && mediaType.MediaType.Equals(JsonMediaType, StringComparison.OrdinalIgnoreCase);
}
