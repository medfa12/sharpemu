// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

public static class HttpExports
{
    private const int HttpErrorInvalidId = unchecked((int)0x80431100);
    private const int HttpErrorInvalidValue = unchecked((int)0x804311FE);
    private const int HttpErrorInvalidUrl = unchecked((int)0x80433060);
    private const int HttpErrorNetwork = unchecked((int)0x80431063);
    private const int HttpErrorBeforeSend = unchecked((int)0x80431065);
    private const int HttpErrorAfterSend = unchecked((int)0x80431066);
    private const int HttpErrorTimeout = unchecked((int)0x80431068);
    private const int HttpErrorUnknownMethod = unchecked((int)0x8043106B);
    private const int MaxStringLength = 2048;
    private const int MaxBodySize = 16 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);
    private static readonly HttpClient Client = CreateClient();
    private static readonly ConcurrentDictionary<int, HttpContext> Contexts = new();
    private static readonly ConcurrentDictionary<int, HttpTemplate> Templates = new();
    private static readonly ConcurrentDictionary<int, HttpConnection> Connections = new();
    private static readonly ConcurrentDictionary<int, HttpRequestState> Requests = new();
    private static int _nextContextId;
    private static int _nextObjectId = 0x1000;

    private sealed record HttpContext(int NetMemoryId, int SslContextId, ulong PoolSize);

    private sealed record HttpTemplate(int ContextId, string UserAgent, int HttpVersion, bool AutoProxyConfig);

    private sealed record HttpConnection(int TemplateId, Uri BaseUri, bool KeepAlive);

    private sealed class HttpRequestState(int connectionId, HttpMethod method, Uri uri, ulong contentLength)
    {
        public int ConnectionId { get; } = connectionId;
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

    [SysAbiExport(Nid = "A9cVMUtEp4Y", ExportName = "sceHttpInit", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpInit(CpuContext ctx)
    {
        var poolSize = ctx[CpuRegister.Rdx];
        if (poolSize == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        var id = Interlocked.Increment(ref _nextContextId);
        Contexts[id] = new HttpContext(unchecked((int)ctx[CpuRegister.Rdi]), unchecked((int)ctx[CpuRegister.Rsi]), poolSize);
        TraceHttp("init", id, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], poolSize, 0);
        return ReturnHandle(ctx, id);
    }

    [SysAbiExport(Nid = "0gYjPTR-6cY", ExportName = "sceHttpCreateTemplate", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpCreateTemplate(CpuContext ctx)
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
        Templates[id] = new HttpTemplate(contextId, userAgent, unchecked((int)ctx[CpuRegister.Rdx]), ctx[CpuRegister.Rcx] != 0);
        return ReturnHandle(ctx, id);
    }

    [SysAbiExport(Nid = "Kiwv9r4IZCc", ExportName = "sceHttpCreateConnection", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpCreateConnection(CpuContext ctx)
    {
        var templateId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Templates.ContainsKey(templateId))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        if (!TryReadUtf8Z(ctx, ctx[CpuRegister.Rsi], MaxStringLength, out var serverName)
            || !TryReadUtf8Z(ctx, ctx[CpuRegister.Rdx], 16, out var scheme)
            || string.IsNullOrWhiteSpace(serverName)
            || !TryCreateBaseUri(scheme, serverName, unchecked((ushort)ctx[CpuRegister.Rcx]), out var uri))
        {
            return ctx.SetReturn(HttpErrorInvalidUrl);
        }

        var id = NextObjectId();
        Connections[id] = new HttpConnection(templateId, uri, ctx[CpuRegister.R8] != 0);
        return ReturnHandle(ctx, id);
    }

    [SysAbiExport(Nid = "qgxDBjorUxs", ExportName = "sceHttpCreateConnectionWithURL", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpCreateConnectionWithUrl(CpuContext ctx)
    {
        var templateId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Templates.ContainsKey(templateId))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        if (!TryReadAbsoluteUri(ctx, ctx[CpuRegister.Rsi], out var uri))
        {
            return ctx.SetReturn(HttpErrorInvalidUrl);
        }

        var authority = new UriBuilder(uri.Scheme, uri.Host, uri.Port).Uri;
        var id = NextObjectId();
        Connections[id] = new HttpConnection(templateId, authority, ctx[CpuRegister.Rdx] != 0);
        return ReturnHandle(ctx, id);
    }

    [SysAbiExport(Nid = "tsGVru3hCe8", ExportName = "sceHttpCreateRequest", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpCreateRequest(CpuContext ctx)
    {
        var connectionId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Connections.TryGetValue(connectionId, out var connection))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        if (!TryMapMethod(unchecked((int)ctx[CpuRegister.Rsi]), out var method)
            || !TryReadUtf8Z(ctx, ctx[CpuRegister.Rdx], MaxStringLength, out var path)
            || !Uri.TryCreate(connection.BaseUri, path, out var uri))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        return CreateRequest(ctx, connectionId, method, uri, ctx[CpuRegister.Rcx]);
    }

    [SysAbiExport(Nid = "Aeu5wVKkF9w", ExportName = "sceHttpCreateRequestWithURL", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpCreateRequestWithUrl(CpuContext ctx)
    {
        var connectionId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Connections.ContainsKey(connectionId))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        if (!TryMapMethod(unchecked((int)ctx[CpuRegister.Rsi]), out var method))
        {
            return ctx.SetReturn(HttpErrorUnknownMethod);
        }

        if (!TryReadAbsoluteUri(ctx, ctx[CpuRegister.Rdx], out var uri))
        {
            return ctx.SetReturn(HttpErrorInvalidUrl);
        }

        return CreateRequest(ctx, connectionId, method, uri, ctx[CpuRegister.Rcx]);
    }

    [SysAbiExport(Nid = "1e2BNwI-XzE", ExportName = "sceHttpSendRequest", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSendRequest(CpuContext ctx)
    {
        var requestId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Requests.TryGetValue(requestId, out var request))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        var size = ctx[CpuRegister.Rdx];
        if (size > MaxBodySize || (size != 0 && ctx[CpuRegister.Rsi] == 0))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        var postData = new byte[unchecked((int)size)];
        if (postData.Length != 0 && !ctx.Memory.TryRead(ctx[CpuRegister.Rsi], postData))
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
                using var message = new HttpRequestMessage(request.Method, request.Uri);
                if (postData.Length != 0)
                {
                    message.Content = new ByteArrayContent(postData);
                    message.Content.Headers.ContentLength = postData.Length;
                }

                if (Connections.TryGetValue(request.ConnectionId, out var connection)
                    && Templates.TryGetValue(connection.TemplateId, out var template)
                    && !string.IsNullOrWhiteSpace(template.UserAgent))
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

    [SysAbiExport(Nid = "0a2TBNfE3BU", ExportName = "sceHttpGetStatusCode", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpGetStatusCode(CpuContext ctx)
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

    [SysAbiExport(Nid = "P5pdoykPYTk", ExportName = "sceHttpReadData", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpReadData(CpuContext ctx)
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

    [SysAbiExport(Nid = "P6A3ytpsiYc", ExportName = "sceHttpDeleteConnection", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpDeleteConnection(CpuContext ctx)
    {
        var connectionId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Connections.TryRemove(connectionId, out _))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        foreach (var pair in Requests.Where(pair => pair.Value.ConnectionId == connectionId))
        {
            Requests.TryRemove(pair.Key, out _);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "qe7oZ+v4PWA", ExportName = "sceHttpDeleteRequest", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpDeleteRequest(CpuContext ctx) => Requests.TryRemove(unchecked((int)ctx[CpuRegister.Rdi]), out _)
        ? ctx.SetReturn(0)
        : ctx.SetReturn(HttpErrorInvalidId);

    [SysAbiExport(Nid = "4I8vEpuEhZ8", ExportName = "sceHttpDeleteTemplate", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpDeleteTemplate(CpuContext ctx)
    {
        var templateId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Templates.TryRemove(templateId, out _))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        foreach (var pair in Connections.Where(pair => pair.Value.TemplateId == templateId))
        {
            Connections.TryRemove(pair.Key, out _);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "Ik-KpLTlf7Q", ExportName = "sceHttpTerm", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpTerm(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Contexts.TryRemove(contextId, out _))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        foreach (var template in Templates.Where(pair => pair.Value.ContextId == contextId).ToArray())
        {
            RemoveTemplateTree(template.Key);
        }

        return ctx.SetReturn(0);
    }

    private static int CreateRequest(CpuContext ctx, int connectionId, HttpMethod method, Uri uri, ulong contentLength)
    {
        var id = NextObjectId();
        Requests[id] = new HttpRequestState(connectionId, method, uri, contentLength);
        return ReturnHandle(ctx, id);
    }

    private static void RemoveTemplateTree(int templateId)
    {
        Templates.TryRemove(templateId, out _);
        foreach (var connection in Connections.Where(pair => pair.Value.TemplateId == templateId).ToArray())
        {
            Connections.TryRemove(connection.Key, out _);
            foreach (var request in Requests.Where(pair => pair.Value.ConnectionId == connection.Key))
            {
                Requests.TryRemove(request.Key, out _);
            }
        }
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

    private static bool TryCreateBaseUri(string scheme, string host, ushort port, out Uri uri)
    {
        uri = null!;
        if (scheme is not ("http" or "https"))
        {
            return false;
        }

        var effectivePort = port == 0 ? (scheme == "https" ? 443 : 80) : port;
        if (!Uri.TryCreate($"{scheme}://{host}:{effectivePort}/", UriKind.Absolute, out var candidate)
            || string.IsNullOrEmpty(candidate.Host))
        {
            return false;
        }

        uri = candidate;
        return true;
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

    private static bool TryMapMethod(int method, out HttpMethod result)
    {
        result = method switch
        {
            0 => HttpMethod.Get,
            1 => HttpMethod.Post,
            2 => HttpMethod.Head,
            3 => HttpMethod.Options,
            4 => HttpMethod.Put,
            5 => HttpMethod.Delete,
            6 => HttpMethod.Trace,
            7 => HttpMethod.Connect,
            8 => HttpMethod.Patch,
            _ => null!,
        };
        return result is not null;
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

    private static void TraceHttp(string operation, int id, ulong arg0, ulong arg1, ulong arg2, ulong arg3)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_HTTP"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] http.{operation} id={id} arg0=0x{arg0:X16} arg1=0x{arg1:X16} arg2=0x{arg2:X16} arg3=0x{arg3:X16}");
        }
    }
}
