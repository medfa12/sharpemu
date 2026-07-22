// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

public static class Http2Exports
{
    private const int HttpErrorInvalidId = unchecked((int)0x80431100);
    private const int HttpErrorInvalidValue = unchecked((int)0x804311FE);
    private const int HttpErrorInvalidUrl = unchecked((int)0x80433060);
    private const int HttpErrorNetwork = unchecked((int)0x80431063);
    private const int HttpErrorBeforeSend = unchecked((int)0x80431065);
    private const int HttpErrorAfterSend = unchecked((int)0x80431066);
    private const int HttpErrorTimeout = unchecked((int)0x80431068);
    private const int HttpErrorNotFound = unchecked((int)0x80431025);
    private const int HttpErrorOutOfMemory = unchecked((int)0x80431022);
    private const int HttpErrorAborted = unchecked((int)0x80431080);
    private const int MaxStringLength = 2048;
    private const int MaxBodySize = 16 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);
    private static readonly HttpClient Client = CreateClient();
    private static readonly ConcurrentDictionary<int, Http2Context> Contexts = new();
    private static readonly ConcurrentDictionary<int, Http2Template> Templates = new();
    private static readonly ConcurrentDictionary<int, Http2Request> Requests = new();
    private static readonly ConcurrentDictionary<int, Http2RuntimeSettings> RuntimeSettings = new();
    private static readonly ConcurrentDictionary<int, List<KeyValuePair<string, string>>> RequestHeaders = new();
    private static readonly ConcurrentDictionary<int, ConcurrentDictionary<string, string>> CookieJars = new();
    private static readonly ConcurrentDictionary<int, int> CookieBoxes = new();
    private static readonly ConcurrentDictionary<int, ulong> RequestContentLengths = new();
    private static int _nextContextId;
    private static int _nextObjectId = 0x4000;

    private sealed record Http2Context(int NetId, int SslId, ulong PoolSize, int MaxRequests);

    private sealed record Http2Template(int ContextId, string UserAgent, int HttpVersion, bool AutoProxyConfig);

    private sealed class Http2RuntimeSettings
    {
        public int AuthEnabled { get; set; } = 1;
        public int AutoRedirect { get; set; } = 1;
    }

    private sealed class Http2Request(int templateId, HttpMethod method, Uri uri, ulong contentLength)
    {
        public int TemplateId { get; } = templateId;
        public HttpMethod Method { get; } = method;
        public Uri Uri { get; } = uri;
        public ulong ContentLength { get; } = contentLength;
        public int StatusCode { get; set; }
        public byte[]? ResponseBody { get; set; }
        public int ReadOffset { get; set; }
        public bool Sent { get; set; }
        public int Error { get; set; }
        public object Gate { get; } = new();
    }

    [SysAbiExport(Nid = "3JCe3lCbQ8A", ExportName = "sceHttp2Init", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2Init(CpuContext ctx)
    {
        var maxRequests = unchecked((int)ctx[CpuRegister.Rcx]);
        if (ctx[CpuRegister.Rdx] == 0 || maxRequests <= 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        var id = Interlocked.Increment(ref _nextContextId);
        Contexts[id] = new Http2Context(
            unchecked((int)ctx[CpuRegister.Rdi]),
            unchecked((int)ctx[CpuRegister.Rsi]),
            ctx[CpuRegister.Rdx],
            maxRequests);
        return ReturnHandle(ctx, id);
    }

    [SysAbiExport(Nid = "+wCt7fCijgk", ExportName = "sceHttp2CreateTemplate", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2CreateTemplate(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Contexts.ContainsKey(contextId))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        if (!TryReadUtf8Z(ctx, ctx[CpuRegister.Rsi], MaxStringLength, out var userAgent))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        var id = NextObjectId();
        Templates[id] = new Http2Template(contextId, userAgent, unchecked((int)ctx[CpuRegister.Rdx]), ctx[CpuRegister.Rcx] != 0);
        return ReturnHandle(ctx, id);
    }

    [SysAbiExport(Nid = "mmyOCxQMVYQ", ExportName = "sceHttp2CreateRequestWithURL", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2CreateRequestWithUrl(CpuContext ctx)
    {
        var templateId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Templates.TryGetValue(templateId, out var template)
            || !Contexts.TryGetValue(template.ContextId, out var context))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        var activeRequests = Requests.Count(pair => pair.Value.TemplateId == templateId);
        if (activeRequests >= context.MaxRequests)
        {
            return ctx.SetReturn(unchecked((int)0x80431022));
        }

        if (!TryReadUtf8Z(ctx, ctx[CpuRegister.Rsi], 32, out var methodName)
            || string.IsNullOrWhiteSpace(methodName))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        if (!TryReadAbsoluteUri(ctx, ctx[CpuRegister.Rdx], out var uri))
        {
            return ctx.SetReturn(HttpErrorInvalidUrl);
        }

        HttpMethod method;
        try
        {
            method = new HttpMethod(methodName);
        }
        catch (FormatException)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        var id = NextObjectId();
        Requests[id] = new Http2Request(templateId, method, uri, ctx[CpuRegister.Rcx]);
        return ReturnHandle(ctx, id);
    }

    [SysAbiExport(Nid = "rbqZig38AT8", ExportName = "sceHttp2SendRequest", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SendRequest(CpuContext ctx)
    {
        if (!Requests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        var size = ctx[CpuRegister.Rdx];
        if (size > MaxBodySize || (size != 0 && ctx[CpuRegister.Rsi] == 0))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        var body = new byte[unchecked((int)size)];
        if (body.Length != 0 && !ctx.Memory.TryRead(ctx[CpuRegister.Rsi], body))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        lock (request.Gate)
        {
            if (request.Sent)
            {
                return ctx.SetReturn(HttpErrorAfterSend);
            }

            request.Sent = true;
            try
            {
                using var message = new HttpRequestMessage(request.Method, request.Uri)
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                };
                if (body.Length != 0)
                {
                    message.Content = new ByteArrayContent(body);
                }

                if (Templates.TryGetValue(request.TemplateId, out var template) && !string.IsNullOrWhiteSpace(template.UserAgent))
                {
                    message.Headers.UserAgent.TryParseAdd(template.UserAgent);
                }

                using var cancellation = new CancellationTokenSource(RequestTimeout);
                using var response = Client.Send(message, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
                request.StatusCode = (int)response.StatusCode;
                response.Content.LoadIntoBufferAsync(MaxBodySize, cancellation.Token).GetAwaiter().GetResult();
                request.ResponseBody = response.Content.ReadAsByteArrayAsync(cancellation.Token).GetAwaiter().GetResult();
                request.Error = 0;
                return ctx.SetReturn(0);
            }
            catch (OperationCanceledException)
            {
                request.Error = HttpErrorTimeout;
                return ctx.SetReturn(request.Error);
            }
            catch (HttpRequestException)
            {
                request.Error = HttpErrorNetwork;
                return ctx.SetReturn(request.Error);
            }
            catch (InvalidOperationException)
            {
                request.Error = HttpErrorInvalidUrl;
                return ctx.SetReturn(request.Error);
            }
        }
    }

    [SysAbiExport(Nid = "9XYJwCf3lEA", ExportName = "sceHttp2GetStatusCode", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2GetStatusCode(CpuContext ctx)
    {
        if (!Requests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        if (ctx[CpuRegister.Rsi] == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        lock (request.Gate)
        {
            if (!request.Sent)
            {
                return ctx.SetReturn(HttpErrorBeforeSend);
            }

            if (request.Error != 0)
            {
                return ctx.SetReturn(request.Error);
            }

            return ctx.TryWriteInt32(ctx[CpuRegister.Rsi], request.StatusCode)
                ? ctx.SetReturn(0)
                : ctx.SetReturn(HttpErrorInvalidValue);
        }
    }

    [SysAbiExport(Nid = "QygCNNmbGss", ExportName = "sceHttp2ReadData", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2ReadData(CpuContext ctx)
    {
        if (!Requests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        if (ctx[CpuRegister.Rdx] > MaxBodySize || (ctx[CpuRegister.Rdx] != 0 && ctx[CpuRegister.Rsi] == 0))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        lock (request.Gate)
        {
            if (!request.Sent || request.ResponseBody is null)
            {
                return ctx.SetReturn(request.Error != 0 ? request.Error : HttpErrorBeforeSend);
            }

            var count = Math.Min(unchecked((int)ctx[CpuRegister.Rdx]), request.ResponseBody.Length - request.ReadOffset);
            if (count != 0 && !ctx.Memory.TryWrite(ctx[CpuRegister.Rsi], request.ResponseBody.AsSpan(request.ReadOffset, count)))
            {
                return ctx.SetReturn(HttpErrorInvalidValue);
            }

            request.ReadOffset += count;
            return ctx.SetReturn(count);
        }
    }

    [SysAbiExport(Nid = "c8D9qIjo8EY", ExportName = "sceHttp2DeleteRequest", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2DeleteRequest(CpuContext ctx) => Requests.TryRemove(unchecked((int)ctx[CpuRegister.Rdi]), out _)
        ? ctx.SetReturn(0)
        : ctx.SetReturn(HttpErrorInvalidId);

    [SysAbiExport(Nid = "pDom5-078DA", ExportName = "sceHttp2DeleteTemplate", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2DeleteTemplate(CpuContext ctx)
    {
        var templateId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Templates.TryRemove(templateId, out _))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        foreach (var request in Requests.Where(pair => pair.Value.TemplateId == templateId))
        {
            Requests.TryRemove(request.Key, out _);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "YiBUtz-pGkc", ExportName = "sceHttp2Term", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2Term(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Contexts.TryRemove(contextId, out _))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        foreach (var template in Templates.Where(pair => pair.Value.ContextId == contextId).ToArray())
        {
            Templates.TryRemove(template.Key, out _);
            foreach (var request in Requests.Where(pair => pair.Value.TemplateId == template.Key))
            {
                Requests.TryRemove(request.Key, out _);
            }
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "IZ-qjhRqvjk", ExportName = "sceHttp2AbortRequest", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2AbortRequest(CpuContext ctx)
    {
        if (!Requests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        lock (request.Gate)
        {
            if (!request.Sent)
            {
                request.Error = HttpErrorAborted;
            }
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "flPxnowtvWY", ExportName = "sceHttp2AddCookie", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2AddCookie(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Contexts.ContainsKey(contextId))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        if (!TryReadUtf8Z(ctx, ctx[CpuRegister.Rsi], MaxStringLength, out var url)
            || !TryReadUtf8(ctx, ctx[CpuRegister.Rdx], ctx[CpuRegister.Rcx], out var cookie)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        CookieJars.GetOrAdd(contextId, static _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase))[uri.Host] = cookie;
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "nrPfOE8TQu0", ExportName = "sceHttp2AddRequestHeader", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2AddRequestHeader(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var mode = unchecked((int)ctx[CpuRegister.Rcx]);
        if (!IsHttp2Object(id))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        if (mode is not (0 or 1)
            || !TryReadUtf8Z(ctx, ctx[CpuRegister.Rsi], MaxStringLength, out var name)
            || !TryReadUtf8Z(ctx, ctx[CpuRegister.Rdx], MaxStringLength, out var value)
            || string.IsNullOrEmpty(name)
            || name.IndexOfAny(['\r', '\n']) >= 0
            || value.IndexOfAny(['\r', '\n']) >= 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        var headers = RequestHeaders.GetOrAdd(id, static _ => []);
        lock (headers)
        {
            if (mode == 0)
            {
                headers.RemoveAll(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));
            }

            headers.Add(new KeyValuePair<string, string>(name, value));
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "WeuDjj5m4YU", ExportName = "sceHttp2AuthCacheFlush", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2AuthCacheFlush(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "JlFGR4v50Kw", ExportName = "sceHttp2CookieExport", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2CookieExport(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Contexts.ContainsKey(contextId))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        var data = SerializeCookies(contextId);
        return WriteSizedBuffer(ctx, ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx], ctx[CpuRegister.Rcx], data);
    }

    [SysAbiExport(Nid = "5VlQSzXW-SQ", ExportName = "sceHttp2CookieFlush", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2CookieFlush(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Contexts.ContainsKey(contextId))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        CookieJars.TryRemove(contextId, out _);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "B5ibZI5UlzU", ExportName = "sceHttp2CookieImport", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2CookieImport(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Contexts.ContainsKey(contextId))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        if (!TryReadUtf8(ctx, ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx], out var serialized))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        var jar = CookieJars.GetOrAdd(contextId, static _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        foreach (var line in serialized.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('\t');
            if (separator > 0)
            {
                jar[line[..separator]] = line[(separator + 1)..];
            }
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "N4UfjvWJsMw", ExportName = "sceHttp2CreateCookieBox", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2CreateCookieBox(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Contexts.ContainsKey(contextId))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        var id = NextObjectId();
        CookieBoxes[id] = contextId;
        return ReturnHandle(ctx, id);
    }

    [SysAbiExport(Nid = "O9ync3F-JVI", ExportName = "sceHttp2DeleteCookieBox", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2DeleteCookieBox(CpuContext ctx) => CookieBoxes.TryRemove(unchecked((int)ctx[CpuRegister.Rdi]), out _)
        ? ctx.SetReturn(0)
        : ctx.SetReturn(HttpErrorInvalidId);

    [SysAbiExport(Nid = "-rdXUi2XW90", ExportName = "sceHttp2GetAllResponseHeaders", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2GetAllResponseHeaders(CpuContext ctx)
    {
        if (!Requests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        if (ctx[CpuRegister.Rsi] == 0 || ctx[CpuRegister.Rdx] == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        lock (request.Gate)
        {
            if (!request.Sent)
            {
                return ctx.SetReturn(request.Error != 0 ? request.Error : HttpErrorBeforeSend);
            }
        }

        return ctx.TryWriteUInt64(ctx[CpuRegister.Rsi], 0) && ctx.TryWriteUInt64(ctx[CpuRegister.Rdx], 0)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(HttpErrorInvalidValue);
    }

    [SysAbiExport(Nid = "m-OL13q8AI8", ExportName = "sceHttp2GetAuthEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2GetAuthEnabled(CpuContext ctx) => GetBooleanSetting(ctx, static settings => settings.AuthEnabled);

    [SysAbiExport(Nid = "od5QCZhZSfw", ExportName = "sceHttp2GetAutoRedirect", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2GetAutoRedirect(CpuContext ctx) => GetBooleanSetting(ctx, static settings => settings.AutoRedirect);

    [SysAbiExport(Nid = "GQFGj0rYX+A", ExportName = "sceHttp2GetCookie", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2GetCookie(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Contexts.ContainsKey(contextId))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        if (!TryReadUtf8Z(ctx, ctx[CpuRegister.Rsi], MaxStringLength, out var url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        var value = CookieJars.TryGetValue(contextId, out var jar) && jar.TryGetValue(uri.Host, out var cookie) ? cookie : string.Empty;
        var bytes = Encoding.UTF8.GetBytes(value + "\0");
        if (ctx[CpuRegister.Rcx] == 0 || !ctx.TryWriteUInt64(ctx[CpuRegister.Rcx], unchecked((ulong)bytes.Length)))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        if (ctx[CpuRegister.Rdx] == 0)
        {
            return ctx.SetReturn(0);
        }

        return ctx[CpuRegister.R8] >= unchecked((ulong)bytes.Length) && ctx.Memory.TryWrite(ctx[CpuRegister.Rdx], bytes)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(HttpErrorOutOfMemory);
    }

    [SysAbiExport(Nid = "IX23slKvtQI", ExportName = "sceHttp2GetCookieBox", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2GetCookieBox(CpuContext ctx)
    {
        var templateId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Templates.ContainsKey(templateId))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        var boxId = CookieBoxes.FirstOrDefault(pair => pair.Value == Templates[templateId].ContextId).Key;
        return ctx[CpuRegister.Rsi] != 0 && ctx.TryWriteInt32(ctx[CpuRegister.Rsi], boxId)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(HttpErrorInvalidValue);
    }

    [SysAbiExport(Nid = "eij7UzkUqK8", ExportName = "sceHttp2GetCookieStats", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2GetCookieStats(CpuContext ctx) => WriteCookieStats(ctx);

    [SysAbiExport(Nid = "otUQuZa-mv0", ExportName = "sceHttp2GetMemoryPoolStats", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2GetMemoryPoolStats(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var output = ctx[CpuRegister.Rsi];
        if (!Contexts.TryGetValue(contextId, out var context))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        if (output == 0 || !ClearMemory(ctx, output, 32))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        return ctx.TryWriteUInt64(output, context.PoolSize) ? ctx.SetReturn(0) : ctx.SetReturn(HttpErrorInvalidValue);
    }

    [SysAbiExport(Nid = "o0DBQpFE13o", ExportName = "sceHttp2GetResponseContentLength", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2GetResponseContentLength(CpuContext ctx)
    {
        if (!Requests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        if (ctx[CpuRegister.Rsi] == 0 || ctx[CpuRegister.Rdx] == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        lock (request.Gate)
        {
            if (!request.Sent || request.ResponseBody is null)
            {
                return ctx.SetReturn(request.Error != 0 ? request.Error : HttpErrorBeforeSend);
            }

            return ctx.TryWriteInt32(ctx[CpuRegister.Rsi], 0)
                && ctx.TryWriteUInt64(ctx[CpuRegister.Rdx], unchecked((ulong)request.ResponseBody.Length))
                ? ctx.SetReturn(0)
                : ctx.SetReturn(HttpErrorInvalidValue);
        }
    }

    [SysAbiExport(Nid = "bGN-6zbo7ms", ExportName = "sceHttp2ReadDataAsync", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2ReadDataAsync(CpuContext ctx) => Http2ReadData(ctx);

    [SysAbiExport(Nid = "klwUy2Wg+q8", ExportName = "sceHttp2RedirectCacheFlush", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2RedirectCacheFlush(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "jHdP0CS4ZlA", ExportName = "sceHttp2RemoveRequestHeader", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2RemoveRequestHeader(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsHttp2Object(id))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        if (!TryReadUtf8Z(ctx, ctx[CpuRegister.Rsi], MaxStringLength, out var name) || string.IsNullOrEmpty(name))
        {
            return ctx.SetReturn(HttpErrorNotFound);
        }

        if (!RequestHeaders.TryGetValue(id, out var headers))
        {
            return ctx.SetReturn(HttpErrorNotFound);
        }

        lock (headers)
        {
            return headers.RemoveAll(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) > 0
                ? ctx.SetReturn(0)
                : ctx.SetReturn(HttpErrorNotFound);
        }
    }

    [SysAbiExport(Nid = "A+NVAFu4eCg", ExportName = "sceHttp2SendRequestAsync", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SendRequestAsync(CpuContext ctx) => Http2SendRequest(ctx);

    [SysAbiExport(Nid = "jjFahkBPCYs", ExportName = "sceHttp2SetAuthEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetAuthEnabled(CpuContext ctx) => SetBooleanSetting(ctx, static (settings, value) => settings.AuthEnabled = value);

    [SysAbiExport(Nid = "Wwj6HbB2mOo", ExportName = "sceHttp2SetAuthInfoCallback", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetAuthInfoCallback(CpuContext ctx) => ReturnForObject(ctx);

    [SysAbiExport(Nid = "b9AvoIaOuHI", ExportName = "sceHttp2SetAutoRedirect", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetAutoRedirect(CpuContext ctx) => SetBooleanSetting(ctx, static (settings, value) => settings.AutoRedirect = value);

    [SysAbiExport(Nid = "-HIO4VT87v8", ExportName = "sceHttp2SetConnectTimeOut", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetConnectTimeout(CpuContext ctx) => ReturnForObject(ctx);

    [SysAbiExport(Nid = "n8hMLe31OPA", ExportName = "sceHttp2SetConnectionWaitTimeOut", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetConnectionWaitTimeout(CpuContext ctx) => ReturnForObject(ctx);

    [SysAbiExport(Nid = "jrVHsKCXA0g", ExportName = "sceHttp2SetCookieBox", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetCookieBox(CpuContext ctx)
    {
        var templateId = unchecked((int)ctx[CpuRegister.Rdi]);
        var boxId = unchecked((int)ctx[CpuRegister.Rsi]);
        if (!Templates.ContainsKey(templateId) || !CookieBoxes.ContainsKey(boxId))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "mPKVhQqh2Es", ExportName = "sceHttp2SetCookieMaxNum", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetCookieMaxNum(CpuContext ctx) => ReturnForContext(ctx);

    [SysAbiExport(Nid = "o7+WXe4WadE", ExportName = "sceHttp2SetCookieMaxNumPerDomain", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetCookieMaxNumPerDomain(CpuContext ctx) => ReturnForContext(ctx);

    [SysAbiExport(Nid = "6a0N6GPD7RM", ExportName = "sceHttp2SetCookieMaxSize", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetCookieMaxSize(CpuContext ctx) => ReturnForContext(ctx);

    [SysAbiExport(Nid = "zdtXKn9X7no", ExportName = "sceHttp2SetCookieRecvCallback", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetCookieRecvCallback(CpuContext ctx) => ReturnForObject(ctx);

    [SysAbiExport(Nid = "McYmUpQ3-DY", ExportName = "sceHttp2SetCookieSendCallback", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetCookieSendCallback(CpuContext ctx) => ReturnForObject(ctx);

    [SysAbiExport(Nid = "uRosf8GQbHQ", ExportName = "sceHttp2SetInflateGZIPEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetInflateGzipEnabled(CpuContext ctx) => ReturnForObject(ctx);

    [SysAbiExport(Nid = "09tk+kIA1Ns", ExportName = "sceHttp2SetMinSslVersion", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetMinSslVersion(CpuContext ctx) => ReturnForObject(ctx);

    [SysAbiExport(Nid = "UL4Fviw+IAM", ExportName = "sceHttp2SetPreSendCallback", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetPreSendCallback(CpuContext ctx) => Templates.ContainsKey(unchecked((int)ctx[CpuRegister.Rdi]))
        ? ctx.SetReturn(0)
        : ctx.SetReturn(HttpErrorInvalidId);

    [SysAbiExport(Nid = "izvHhqgDt44", ExportName = "sceHttp2SetRecvTimeOut", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetRecvTimeout(CpuContext ctx) => ReturnForObject(ctx);

    [SysAbiExport(Nid = "BJgi0CH7al4", ExportName = "sceHttp2SetRedirectCallback", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetRedirectCallback(CpuContext ctx) => ReturnForObject(ctx);

    [SysAbiExport(Nid = "FSAFOzi0FpM", ExportName = "sceHttp2SetRequestContentLength", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetRequestContentLength(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Requests.TryGetValue(id, out var request))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        lock (request.Gate)
        {
            if (request.Sent)
            {
                return ctx.SetReturn(HttpErrorAfterSend);
            }

            RequestContentLengths[id] = ctx[CpuRegister.Rsi];
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "Gcjh+CisAZM", ExportName = "sceHttp2SetResolveRetry", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetResolveRetry(CpuContext ctx) => ReturnForObject(ctx);

    [SysAbiExport(Nid = "ACjtE27aErY", ExportName = "sceHttp2SetResolveTimeOut", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetResolveTimeout(CpuContext ctx) => ReturnForObject(ctx);

    [SysAbiExport(Nid = "XPtW45xiLHk", ExportName = "sceHttp2SetSendTimeOut", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetSendTimeout(CpuContext ctx) => ReturnForObject(ctx);

    [SysAbiExport(Nid = "YrWX+DhPHQY", ExportName = "sceHttp2SetSslCallback", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetSslCallback(CpuContext ctx) => ReturnForObject(ctx);

    [SysAbiExport(Nid = "VYMxTcBqSE0", ExportName = "sceHttp2SetTimeOut", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SetTimeout(CpuContext ctx) => ReturnForObject(ctx);

    [SysAbiExport(Nid = "B37SruheQ5Y", ExportName = "sceHttp2SslDisableOption", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SslDisableOption(CpuContext ctx) => ReturnForObject(ctx);

    [SysAbiExport(Nid = "EWcwMpbr5F8", ExportName = "sceHttp2SslEnableOption", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2SslEnableOption(CpuContext ctx) => ReturnForObject(ctx);

    [SysAbiExport(Nid = "MOp-AUhdfi8", ExportName = "sceHttp2WaitAsync", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp2")]
    public static int Http2WaitAsync(CpuContext ctx)
    {
        if (!Requests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        lock (request.Gate)
        {
            var result = request.Error;
            return ctx[CpuRegister.Rsi] == 0 || ctx.TryWriteInt32(ctx[CpuRegister.Rsi], result)
                ? ctx.SetReturn(result)
                : ctx.SetReturn(HttpErrorInvalidValue);
        }
    }

    private static bool IsHttp2Object(int id) => Templates.ContainsKey(id) || Requests.ContainsKey(id);

    private static int ReturnForObject(CpuContext ctx) => ctx.SetReturn(IsHttp2Object(unchecked((int)ctx[CpuRegister.Rdi])) ? 0 : HttpErrorInvalidId);

    private static int ReturnForContext(CpuContext ctx) => ctx.SetReturn(Contexts.ContainsKey(unchecked((int)ctx[CpuRegister.Rdi])) ? 0 : HttpErrorInvalidId);

    private static int GetBooleanSetting(CpuContext ctx, Func<Http2RuntimeSettings, int> getter)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsHttp2Object(id))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        return ctx[CpuRegister.Rsi] != 0 && ctx.TryWriteInt32(ctx[CpuRegister.Rsi], getter(RuntimeSettings.GetOrAdd(id, static _ => new Http2RuntimeSettings())))
            ? ctx.SetReturn(0)
            : ctx.SetReturn(HttpErrorInvalidValue);
    }

    private static int SetBooleanSetting(CpuContext ctx, Action<Http2RuntimeSettings, int> setter)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsHttp2Object(id))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        var value = unchecked((int)ctx[CpuRegister.Rsi]);
        if (value is not (0 or 1))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        setter(RuntimeSettings.GetOrAdd(id, static _ => new Http2RuntimeSettings()), value);
        return ctx.SetReturn(0);
    }

    private static int WriteCookieStats(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var output = ctx[CpuRegister.Rsi];
        if (!Contexts.ContainsKey(contextId))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        if (output == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        var entries = CookieJars.TryGetValue(contextId, out var jar) ? jar.ToArray() : [];
        var used = entries.Sum(static pair => Encoding.UTF8.GetByteCount(pair.Key) + Encoding.UTF8.GetByteCount(pair.Value) + 2);
        return ClearMemory(ctx, output, 40)
            && ctx.TryWriteUInt64(output, unchecked((ulong)used))
            && ctx.TryWriteUInt32(output + 8, unchecked((uint)entries.Length))
            && ctx.TryWriteUInt64(output + 16, unchecked((ulong)used))
            && ctx.TryWriteUInt32(output + 24, unchecked((uint)entries.Length))
            ? ctx.SetReturn(0)
            : ctx.SetReturn(HttpErrorInvalidValue);
    }

    private static byte[] SerializeCookies(int contextId)
    {
        if (!CookieJars.TryGetValue(contextId, out var jar) || jar.IsEmpty)
        {
            return [];
        }

        var text = string.Join('\n', jar.OrderBy(static pair => pair.Key).Select(static pair => $"{pair.Key}\t{pair.Value}"));
        return Encoding.UTF8.GetBytes(text);
    }

    private static int WriteSizedBuffer(CpuContext ctx, ulong output, ulong capacity, ulong requiredAddress, ReadOnlySpan<byte> data)
    {
        if (requiredAddress == 0 || !ctx.TryWriteUInt64(requiredAddress, unchecked((ulong)data.Length)))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        if (output == 0)
        {
            return ctx.SetReturn(0);
        }

        return capacity >= unchecked((ulong)data.Length) && ctx.Memory.TryWrite(output, data)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(HttpErrorOutOfMemory);
    }

    private static bool TryReadUtf8(CpuContext ctx, ulong address, ulong length, out string value)
    {
        value = string.Empty;
        if (length > MaxBodySize || (length != 0 && address == 0))
        {
            return false;
        }

        var bytes = new byte[unchecked((int)length)];
        if (bytes.Length != 0 && !ctx.Memory.TryRead(address, bytes))
        {
            return false;
        }

        value = Encoding.UTF8.GetString(bytes);
        return true;
    }

    private static bool ClearMemory(CpuContext ctx, ulong address, int length) => ctx.Memory.TryWrite(address, new byte[length]);

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            ConnectTimeout = RequestTimeout,
            UseProxy = false,
        };
        return new HttpClient(handler) { Timeout = RequestTimeout };
    }

    private static bool TryReadAbsoluteUri(CpuContext ctx, ulong address, out Uri uri)
    {
        uri = null!;
        if (!TryReadUtf8Z(ctx, address, MaxStringLength, out var value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            || candidate.Scheme is not ("http" or "https"))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    private static bool TryReadUtf8Z(CpuContext ctx, ulong address, int maxLength, out string value)
    {
        value = string.Empty;
        if (address == 0)
        {
            return true;
        }

        var bytes = new byte[maxLength];
        Span<byte> one = stackalloc byte[1];
        for (var i = 0; i < maxLength; i++)
        {
            if (!ctx.Memory.TryRead(address + (ulong)i, one))
            {
                return false;
            }

            if (one[0] == 0)
            {
                value = Encoding.UTF8.GetString(bytes, 0, i);
                return true;
            }

            bytes[i] = one[0];
        }

        return false;
    }

    private static int NextObjectId() => Interlocked.Increment(ref _nextObjectId);

    private static int ReturnHandle(CpuContext ctx, int id)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)id);
        return id;
    }
}
