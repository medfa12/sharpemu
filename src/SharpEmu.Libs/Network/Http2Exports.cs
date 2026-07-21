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
    private const int MaxStringLength = 2048;
    private const int MaxBodySize = 16 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);
    private static readonly HttpClient Client = CreateClient();
    private static readonly ConcurrentDictionary<int, Http2Context> Contexts = new();
    private static readonly ConcurrentDictionary<int, Http2Template> Templates = new();
    private static readonly ConcurrentDictionary<int, Http2Request> Requests = new();
    private static int _nextContextId;
    private static int _nextObjectId = 0x4000;

    private sealed record Http2Context(int NetId, int SslId, ulong PoolSize, int MaxRequests);

    private sealed record Http2Template(int ContextId, string UserAgent, int HttpVersion, bool AutoProxyConfig);

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
