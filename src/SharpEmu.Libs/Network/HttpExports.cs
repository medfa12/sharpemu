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
    private const int HttpErrorNotFound = unchecked((int)0x80431025);
    private const int HttpErrorOutOfMemory = unchecked((int)0x80431022);
    private const int HttpErrorAborted = unchecked((int)0x80431080);
    private const int HttpErrorParseNotFound = unchecked((int)0x80432025);
    private const int HttpErrorParseInvalidResponse = unchecked((int)0x80432060);
    private const int HttpErrorParseInvalidValue = unchecked((int)0x804321FE);
    private const int MaxStringLength = 2048;
    private const int MaxBodySize = 16 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);
    private static readonly HttpClient Client = CreateClient();
    private static readonly ConcurrentDictionary<int, HttpContext> Contexts = new();
    private static readonly ConcurrentDictionary<int, HttpTemplate> Templates = new();
    private static readonly ConcurrentDictionary<int, HttpConnection> Connections = new();
    private static readonly ConcurrentDictionary<int, HttpRequestState> Requests = new();
    private static readonly ConcurrentDictionary<int, HttpRuntimeSettings> RuntimeSettings = new();
    private static readonly ConcurrentDictionary<int, List<KeyValuePair<string, string>>> RequestHeaders = new();
    private static readonly ConcurrentDictionary<int, HttpEpollState> Epolls = new();
    private static readonly ConcurrentDictionary<int, HttpEpollBinding> EpollBindings = new();
    private static readonly ConcurrentDictionary<int, ConcurrentDictionary<string, string>> CookieJars = new();
    private static readonly ConcurrentDictionary<int, byte> LoadedCertificates = new();
    private static readonly ConcurrentDictionary<int, ulong> RequestContentLengths = new();
    private static int _nextContextId;
    private static int _nextObjectId = 0x1000;
    private static int _defaultAcceptEncodingGzip = 1;

    private sealed record HttpContext(int NetMemoryId, int SslContextId, ulong PoolSize);

    private sealed record HttpTemplate(int ContextId, string UserAgent, int HttpVersion, bool AutoProxyConfig);

    private sealed record HttpConnection(int TemplateId, Uri BaseUri, bool KeepAlive);

    private sealed class HttpRuntimeSettings
    {
        public int AcceptEncodingGzip { get; set; } = Volatile.Read(ref _defaultAcceptEncodingGzip);
        public int AuthEnabled { get; set; } = 1;
        public int AutoRedirect { get; set; } = 1;
        public int CookieEnabled { get; set; } = 1;
        public int Nonblock { get; set; }
        public uint SslFlags { get; set; } = 0xA5;
    }

    private sealed record HttpEpollState(int ContextId)
    {
        public bool AbortRequested { get; set; }
    }

    private sealed record HttpEpollBinding(int EpollId, ulong UserArgument);

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

    [SysAbiExport(Nid = "hvG6GfBMXg8", ExportName = "sceHttpAbortRequest", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpAbortRequest(CpuContext ctx)
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

    [SysAbiExport(Nid = "JKl06ZIAl6A", ExportName = "sceHttpAbortRequestForce", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpAbortRequestForce(CpuContext ctx) => HttpAbortRequest(ctx);

    [SysAbiExport(Nid = "sWQiqKvYTVA", ExportName = "sceHttpAbortWaitRequest", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpAbortWaitRequest(CpuContext ctx)
    {
        var epollId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Epolls.TryGetValue(epollId, out var epoll))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        epoll.AbortRequested = true;
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "mNan6QSnpeY", ExportName = "sceHttpAddCookie", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpAddCookie(CpuContext ctx)
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

    [SysAbiExport(Nid = "JM58a21mtrQ", ExportName = "sceHttpAddQuery", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpAddQuery(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "EY28T2bkN7k", ExportName = "sceHttpAddRequestHeader", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpAddRequestHeader(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var mode = unchecked((int)ctx[CpuRegister.Rcx]);
        if (!IsHttpObject(id))
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

    [SysAbiExport(Nid = "lGAjftanhFs", ExportName = "sceHttpAddRequestHeaderRaw", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpAddRequestHeaderRaw(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "Y1DCjN-s2BA", ExportName = "sceHttpAuthCacheExport", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpAuthCacheExport(CpuContext ctx) => WriteEmptyBlob(ctx, ctx[CpuRegister.Rcx]);

    [SysAbiExport(Nid = "zzB0StvRab4", ExportName = "sceHttpAuthCacheFlush", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpAuthCacheFlush(CpuContext ctx) => ReturnForContext(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "wF0KcxK20BE", ExportName = "sceHttpAuthCacheImport", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpAuthCacheImport(CpuContext ctx) => ReturnForContext(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "A7n9nNg7NBg", ExportName = "sceHttpCacheRedirectedConnectionEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpCacheRedirectedConnectionEnabled(CpuContext ctx) => ReturnForObject(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "nOkViL17ZOo", ExportName = "sceHttpCookieExport", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpCookieExport(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Contexts.ContainsKey(contextId))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        var data = SerializeCookies(contextId);
        return WriteSizedBuffer(ctx, ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx], ctx[CpuRegister.Rcx], data);
    }

    [SysAbiExport(Nid = "seCvUt91WHY", ExportName = "sceHttpCookieFlush", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpCookieFlush(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Contexts.ContainsKey(contextId))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        CookieJars.TryRemove(contextId, out _);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "pFnXDxo3aog", ExportName = "sceHttpCookieImport", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpCookieImport(CpuContext ctx)
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

    [SysAbiExport(Nid = "6381dWF+xsQ", ExportName = "sceHttpCreateEpoll", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpCreateEpoll(CpuContext ctx)
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

        var id = NextObjectId();
        Epolls[id] = new HttpEpollState(contextId);
        return ctx.TryWriteUInt64(output, unchecked((ulong)id)) ? ctx.SetReturn(0) : ctx.SetReturn(HttpErrorInvalidValue);
    }

    [SysAbiExport(Nid = "rGNm+FjIXKk", ExportName = "sceHttpCreateRequest2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpCreateRequest2(CpuContext ctx) => CreateRequestWithMethodName(ctx, false);

    [SysAbiExport(Nid = "Cnp77podkCU", ExportName = "sceHttpCreateRequestWithURL2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpCreateRequestWithUrl2(CpuContext ctx) => CreateRequestWithMethodName(ctx, true);

    [SysAbiExport(Nid = "Lffcxao-QMM", ExportName = "sceHttpDbgEnableProfile", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpDbgEnableProfile(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "6gyx-I0Oob4", ExportName = "sceHttpDbgGetConnectionStat", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpDbgGetConnectionStat(CpuContext ctx) => ClearOptionalOutput(ctx, ctx[CpuRegister.Rsi], 32);

    [SysAbiExport(Nid = "fzzBpJjm9Kw", ExportName = "sceHttpDbgGetRequestStat", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpDbgGetRequestStat(CpuContext ctx) => ClearOptionalOutput(ctx, ctx[CpuRegister.Rsi], 32);

    [SysAbiExport(Nid = "VmqSnjZ5mE4", ExportName = "sceHttpDbgSetPrintf", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpDbgSetPrintf(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "KJtUHtp6y0U", ExportName = "sceHttpDbgShowConnectionStat", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpDbgShowConnectionStat(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "oEuPssSYskA", ExportName = "sceHttpDbgShowMemoryPoolStat", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpDbgShowMemoryPoolStat(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "L2gM3qptqHs", ExportName = "sceHttpDbgShowRequestStat", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpDbgShowRequestStat(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "pxBsD-X9eH0", ExportName = "sceHttpDbgShowStat", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpDbgShowStat(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "wYhXVfS2Et4", ExportName = "sceHttpDestroyEpoll", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpDestroyEpoll(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        var epollId = unchecked((int)ctx[CpuRegister.Rsi]);
        if (!Contexts.ContainsKey(contextId))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        return Epolls.TryRemove(epollId, out _) ? ctx.SetReturn(0) : ctx.SetReturn(HttpErrorInvalidId);
    }

    [SysAbiExport(Nid = "1rpZqxdMRwQ", ExportName = "sceHttpGetAcceptEncodingGZIPEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpGetAcceptEncodingGzipEnabled(CpuContext ctx) => GetBooleanSetting(ctx, static settings => settings.AcceptEncodingGzip);

    [SysAbiExport(Nid = "aCYPMSUIaP8", ExportName = "sceHttpGetAllResponseHeaders", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpGetAllResponseHeaders(CpuContext ctx)
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

    [SysAbiExport(Nid = "9m8EcOGzcIQ", ExportName = "sceHttpGetAuthEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpGetAuthEnabled(CpuContext ctx) => GetBooleanSetting(ctx, static settings => settings.AuthEnabled);

    [SysAbiExport(Nid = "mmLexUbtnfY", ExportName = "sceHttpGetAutoRedirect", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpGetAutoRedirect(CpuContext ctx) => GetBooleanSetting(ctx, static settings => settings.AutoRedirect);

    [SysAbiExport(Nid = "L-DwVoHXLtU", ExportName = "sceHttpGetConnectionStat", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpGetConnectionStat(CpuContext ctx) => ClearOptionalOutput(ctx, ctx[CpuRegister.Rsi], 32);

    [SysAbiExport(Nid = "+G+UsJpeXPc", ExportName = "sceHttpGetCookie", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpGetCookie(CpuContext ctx)
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

    [SysAbiExport(Nid = "iSZjWw1TGiA", ExportName = "sceHttpGetCookieEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpGetCookieEnabled(CpuContext ctx) => GetBooleanSetting(ctx, static settings => settings.CookieEnabled);

    [SysAbiExport(Nid = "xkymWiGdMiI", ExportName = "sceHttpGetCookieStats", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpGetCookieStats(CpuContext ctx)
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

    [SysAbiExport(Nid = "7j9VcwnrZo4", ExportName = "sceHttpGetEpoll", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpGetEpoll(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsHttpObject(id))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        if (ctx[CpuRegister.Rsi] == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        EpollBindings.TryGetValue(id, out var binding);
        return ctx.TryWriteUInt64(ctx[CpuRegister.Rsi], unchecked((ulong)(binding?.EpollId ?? 0)))
            && (ctx[CpuRegister.Rdx] == 0 || ctx.TryWriteUInt64(ctx[CpuRegister.Rdx], binding?.UserArgument ?? 0))
            ? ctx.SetReturn(0)
            : ctx.SetReturn(HttpErrorInvalidValue);
    }

    [SysAbiExport(Nid = "IQOP6McWJcY", ExportName = "sceHttpGetEpollId", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpGetEpollId(CpuContext ctx) => HttpGetEpoll(ctx);

    [SysAbiExport(Nid = "0onIrKx9NIE", ExportName = "sceHttpGetLastErrno", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpGetLastErrno(CpuContext ctx)
    {
        if (!Requests.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var request))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        return ctx[CpuRegister.Rsi] != 0 && ctx.TryWriteInt32(ctx[CpuRegister.Rsi], request.Error)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(HttpErrorInvalidValue);
    }

    [SysAbiExport(Nid = "16sMmVuOvgU", ExportName = "sceHttpGetMemoryPoolStats", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpGetMemoryPoolStats(CpuContext ctx)
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

    [SysAbiExport(Nid = "Wq4RNB3snSQ", ExportName = "sceHttpGetNonblock", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpGetNonblock(CpuContext ctx) => GetBooleanSetting(ctx, static settings => settings.Nonblock);

    [SysAbiExport(Nid = "hkcfqAl+82w", ExportName = "sceHttpGetRegisteredCtxIds", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpGetRegisteredContextIds(CpuContext ctx)
    {
        var ids = Contexts.Keys.Order().ToArray();
        var capacity = unchecked((int)ctx[CpuRegister.Rsi]);
        if (ctx[CpuRegister.Rdx] != 0 && !ctx.TryWriteInt32(ctx[CpuRegister.Rdx], ids.Length))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        if (ctx[CpuRegister.Rdi] == 0 || capacity <= 0)
        {
            return ctx.SetReturn(0);
        }

        for (var index = 0; index < Math.Min(ids.Length, capacity); index++)
        {
            if (!ctx.TryWriteInt32(ctx[CpuRegister.Rdi] + unchecked((ulong)(index * sizeof(int))), ids[index]))
            {
                return ctx.SetReturn(HttpErrorInvalidValue);
            }
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "yuO2H2Uvnos", ExportName = "sceHttpGetResponseContentLength", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpGetResponseContentLength(CpuContext ctx)
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

    [SysAbiExport(Nid = "hPTXo3bICzI", ExportName = "sceHttpParseResponseHeader", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpParseResponseHeader(CpuContext ctx)
    {
        var headerAddress = ctx[CpuRegister.Rdi];
        var headerLength = ctx[CpuRegister.Rsi];
        var fieldAddress = ctx[CpuRegister.Rdx];
        var valueAddress = ctx[CpuRegister.Rcx];
        var lengthAddress = ctx[CpuRegister.R8];
        if (headerAddress == 0)
        {
            return ctx.SetReturn(HttpErrorParseInvalidResponse);
        }

        if (fieldAddress == 0 || valueAddress == 0 || lengthAddress == 0
            || headerLength > MaxBodySize
            || !TryReadBytes(ctx, headerAddress, headerLength, out var header)
            || !TryReadUtf8Z(ctx, fieldAddress, MaxStringLength, out var field))
        {
            return ctx.SetReturn(HttpErrorParseInvalidValue);
        }

        var text = Encoding.Latin1.GetString(header);
        var offset = 0;
        foreach (var line in text.Split('\n'))
        {
            var lineLength = line.Length + (offset + line.Length < text.Length ? 1 : 0);
            var colon = line.IndexOf(':');
            if (colon >= 0 && string.Equals(line[..colon].Trim(), field, StringComparison.OrdinalIgnoreCase))
            {
                var start = colon + 1;
                while (start < line.Length && char.IsWhiteSpace(line[start]))
                {
                    start++;
                }

                var value = line[start..].TrimEnd('\r');
                return ctx.TryWriteUInt64(valueAddress, headerAddress + unchecked((ulong)(offset + start)))
                    && ctx.TryWriteUInt64(lengthAddress, unchecked((ulong)value.Length))
                    ? ctx.SetReturn(offset + lineLength)
                    : ctx.SetReturn(HttpErrorParseInvalidValue);
            }

            offset += lineLength;
        }

        return ctx.SetReturn(HttpErrorParseNotFound);
    }

    [SysAbiExport(Nid = "Qq8SfuJJJqE", ExportName = "sceHttpParseStatusLine", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpParseStatusLine(CpuContext ctx)
    {
        if (!TryGetStackArgument(ctx, 0, out var phraseLengthAddress))
        {
            return ctx.SetReturn(HttpErrorParseInvalidValue);
        }

        var address = ctx[CpuRegister.Rdi];
        var length = ctx[CpuRegister.Rsi];
        if (address == 0)
        {
            return ctx.SetReturn(HttpErrorParseInvalidResponse);
        }

        if (ctx[CpuRegister.Rdx] == 0 || ctx[CpuRegister.Rcx] == 0 || ctx[CpuRegister.R8] == 0
            || ctx[CpuRegister.R9] == 0 || phraseLengthAddress == 0
            || length > MaxStringLength || !TryReadBytes(ctx, address, length, out var bytes))
        {
            return ctx.SetReturn(HttpErrorParseInvalidValue);
        }

        var line = Encoding.ASCII.GetString(bytes);
        var newline = line.IndexOf('\n');
        if (newline < 0)
        {
            return ctx.SetReturn(HttpErrorParseInvalidResponse);
        }

        var status = line[..newline].TrimEnd('\r');
        if (!status.StartsWith("HTTP/", StringComparison.Ordinal))
        {
            return ctx.SetReturn(HttpErrorParseInvalidResponse);
        }

        var firstSpace = status.IndexOf(' ');
        var secondSpace = firstSpace < 0 ? -1 : status.IndexOf(' ', firstSpace + 1);
        var dot = firstSpace < 0 ? -1 : status.IndexOf('.', 5, firstSpace - 5);
        if (dot < 0 || firstSpace < 0
            || !int.TryParse(status.AsSpan(5, dot - 5), out var major)
            || !int.TryParse(status.AsSpan(dot + 1, firstSpace - dot - 1), out var minor)
            || status.Length < firstSpace + 4
            || !int.TryParse(status.AsSpan(firstSpace + 1, 3), out var code))
        {
            return ctx.SetReturn(HttpErrorParseInvalidResponse);
        }

        var phraseOffset = secondSpace < 0 ? firstSpace + 4 : secondSpace + 1;
        var phraseLength = secondSpace < 0 ? 0 : status.Length - phraseOffset;
        return ctx.TryWriteInt32(ctx[CpuRegister.Rdx], major)
            && ctx.TryWriteInt32(ctx[CpuRegister.Rcx], minor)
            && ctx.TryWriteInt32(ctx[CpuRegister.R8], code)
            && ctx.TryWriteUInt64(ctx[CpuRegister.R9], address + unchecked((ulong)phraseOffset))
            && ctx.TryWriteUInt64(phraseLengthAddress, unchecked((ulong)phraseLength))
            ? ctx.SetReturn(newline + 1)
            : ctx.SetReturn(HttpErrorParseInvalidValue);
    }

    [SysAbiExport(Nid = "u05NnI+P+KY", ExportName = "sceHttpRedirectCacheFlush", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpRedirectCacheFlush(CpuContext ctx) => ReturnForContext(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "zNGh-zoQTD0", ExportName = "sceHttpRemoveRequestHeader", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpRemoveRequestHeader(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsHttpObject(id))
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

    [SysAbiExport(Nid = "4fgkfVeVsGU", ExportName = "sceHttpRequestGetAllHeaders", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpRequestGetAllHeaders(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsHttpObject(id))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        if (ctx[CpuRegister.Rsi] == 0 || ctx[CpuRegister.Rdx] == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        return ctx.TryWriteUInt64(ctx[CpuRegister.Rsi], 0) && ctx.TryWriteUInt64(ctx[CpuRegister.Rdx], 0)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(HttpErrorInvalidValue);
    }

    [SysAbiExport(Nid = "HRX1iyDoKR8", ExportName = "sceHttpSetAcceptEncodingGZIPEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetAcceptEncodingGzipEnabled(CpuContext ctx) => SetBooleanSetting(ctx, static (settings, value) => settings.AcceptEncodingGzip = value);

    [SysAbiExport(Nid = "qFg2SuyTJJY", ExportName = "sceHttpSetAuthEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetAuthEnabled(CpuContext ctx) => SetBooleanSetting(ctx, static (settings, value) => settings.AuthEnabled = value);

    [SysAbiExport(Nid = "jf4TB2nUO40", ExportName = "sceHttpSetAuthInfoCallback", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetAuthInfoCallback(CpuContext ctx) => ReturnForObject(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "T-mGo9f3Pu4", ExportName = "sceHttpSetAutoRedirect", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetAutoRedirect(CpuContext ctx) => SetBooleanSetting(ctx, static (settings, value) => settings.AutoRedirect = value);

    [SysAbiExport(Nid = "PDxS48xGQLs", ExportName = "sceHttpSetChunkedTransferEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetChunkedTransferEnabled(CpuContext ctx) => ReturnForObject(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "0S9tTH0uqTU", ExportName = "sceHttpSetConnectTimeOut", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetConnectTimeout(CpuContext ctx) => ReturnForObject(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "XNUoD2B9a6A", ExportName = "sceHttpSetCookieEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetCookieEnabled(CpuContext ctx) => SetBooleanSetting(ctx, static (settings, value) => settings.CookieEnabled = value);

    [SysAbiExport(Nid = "pM--+kIeW-8", ExportName = "sceHttpSetCookieMaxNum", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetCookieMaxNum(CpuContext ctx) => ReturnForContext(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "Kp6juCJUJGQ", ExportName = "sceHttpSetCookieMaxNumPerDomain", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetCookieMaxNumPerDomain(CpuContext ctx) => ReturnForContext(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "7Y4364GBras", ExportName = "sceHttpSetCookieMaxSize", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetCookieMaxSize(CpuContext ctx) => ReturnForContext(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "Kh6bS2HQKbo", ExportName = "sceHttpSetCookieRecvCallback", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetCookieRecvCallback(CpuContext ctx) => ReturnForObject(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "GnVDzYfy-KI", ExportName = "sceHttpSetCookieSendCallback", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetCookieSendCallback(CpuContext ctx) => ReturnForObject(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "pHc3bxUzivU", ExportName = "sceHttpSetCookieTotalMaxSize", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetCookieTotalMaxSize(CpuContext ctx) => ReturnForContext(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "8kzIXsRy1bY", ExportName = "sceHttpSetDefaultAcceptEncodingGZIPEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetDefaultAcceptEncodingGzipEnabled(CpuContext ctx)
    {
        if (!Contexts.ContainsKey(unchecked((int)ctx[CpuRegister.Rdi])))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        Volatile.Write(ref _defaultAcceptEncodingGzip, ctx[CpuRegister.Rsi] == 0 ? 0 : 1);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "22buO-UufJY", ExportName = "sceHttpSetDelayBuildRequestEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetDelayBuildRequestEnabled(CpuContext ctx) => ReturnForObject(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "-xm7kZQNpHI", ExportName = "sceHttpSetEpoll", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetEpoll(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var epollId = unchecked((int)ctx[CpuRegister.Rsi]);
        if (!IsHttpObject(id) || !Epolls.ContainsKey(epollId))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        EpollBindings[id] = new HttpEpollBinding(epollId, ctx[CpuRegister.Rdx]);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "LG1YW1Uhkgo", ExportName = "sceHttpSetEpollId", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetEpollId(CpuContext ctx) => HttpSetEpoll(ctx);

    [SysAbiExport(Nid = "pk0AuomQM1o", ExportName = "sceHttpSetHttp09Enabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetHttp09Enabled(CpuContext ctx) => ReturnForObject(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "i9mhafzkEi8", ExportName = "sceHttpSetInflateGZIPEnabled", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetInflateGzipEnabled(CpuContext ctx) => ReturnForObject(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "s2-NPIvz+iA", ExportName = "sceHttpSetNonblock", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetNonblock(CpuContext ctx) => SetBooleanSetting(ctx, static (settings, value) => settings.Nonblock = value);

    [SysAbiExport(Nid = "gZ9TpeFQ7Gk", ExportName = "sceHttpSetPolicyOption", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetPolicyOption(CpuContext ctx) => ReturnForObject(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "2NeZnMEP3-0", ExportName = "sceHttpSetPriorityOption", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetPriorityOption(CpuContext ctx) => ReturnForObject(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "i+quCZCL+D8", ExportName = "sceHttpSetProxy", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetProxy(CpuContext ctx)
    {
        if (!IsHttpObject(unchecked((int)ctx[CpuRegister.Rdi])))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        return TryReadUtf8Z(ctx, ctx[CpuRegister.R8], MaxStringLength, out _) ? ctx.SetReturn(0) : ctx.SetReturn(HttpErrorInvalidValue);
    }

    [SysAbiExport(Nid = "mMcB2XIDoV4", ExportName = "sceHttpSetRecvBlockSize", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetRecvBlockSize(CpuContext ctx) => ReturnForObject(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "yigr4V0-HTM", ExportName = "sceHttpSetRecvTimeOut", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetRecvTimeout(CpuContext ctx) => ReturnForObject(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "h9wmFZX4i-4", ExportName = "sceHttpSetRedirectCallback", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetRedirectCallback(CpuContext ctx) => ReturnForObject(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "PTiFIUxCpJc", ExportName = "sceHttpSetRequestContentLength", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetRequestContentLength(CpuContext ctx)
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

    [SysAbiExport(Nid = "vO4B-42ef-k", ExportName = "sceHttpSetRequestStatusCallback", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetRequestStatusCallback(CpuContext ctx) => ReturnForObject(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "K1d1LqZRQHQ", ExportName = "sceHttpSetResolveRetry", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetResolveRetry(CpuContext ctx) => IsHttpObject(unchecked((int)ctx[CpuRegister.Rdi])) && unchecked((int)ctx[CpuRegister.Rsi]) >= 0
        ? ctx.SetReturn(0)
        : ctx.SetReturn(IsHttpObject(unchecked((int)ctx[CpuRegister.Rdi])) ? HttpErrorInvalidValue : HttpErrorInvalidId);

    [SysAbiExport(Nid = "Tc-hAYDKtQc", ExportName = "sceHttpSetResolveTimeOut", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetResolveTimeout(CpuContext ctx) => ReturnForObject(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "a4VsZ4oqn68", ExportName = "sceHttpSetResponseHeaderMaxSize", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetResponseHeaderMaxSize(CpuContext ctx) => ReturnForObject(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "xegFfZKBVlw", ExportName = "sceHttpSetSendTimeOut", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetSendTimeout(CpuContext ctx) => ReturnForObject(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "POJ0azHZX3w", ExportName = "sceHttpSetSocketCreationCallback", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpSetSocketCreationCallback(CpuContext ctx) => ReturnForObject(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "V-noPEjSB8c", ExportName = "sceHttpTryGetNonblock", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpTryGetNonblock(CpuContext ctx) => HttpGetNonblock(ctx);

    [SysAbiExport(Nid = "fmOs6MzCRqk", ExportName = "sceHttpTrySetNonblock", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpTrySetNonblock(CpuContext ctx) => HttpSetNonblock(ctx);

    [SysAbiExport(Nid = "59tL1AQBb8U", ExportName = "sceHttpUnsetEpoll", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpUnsetEpoll(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Requests.ContainsKey(id))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        EpollBindings.TryRemove(id, out _);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "5LZA+KPISVA", ExportName = "sceHttpUriBuild", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpUriBuild(CpuContext ctx)
    {
        var output = ctx[CpuRegister.Rdi];
        var requiredOutput = ctx[CpuRegister.Rsi];
        var prepared = ctx[CpuRegister.Rdx];
        var element = ctx[CpuRegister.Rcx];
        var options = unchecked((uint)ctx[CpuRegister.R8]);
        if (element == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidUrl);
        }

        if (output == 0 && requiredOutput == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        if (!TryReadUriElement(ctx, element, out var uri))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        var builder = new StringBuilder();
        if ((options & 1) != 0 && uri.Scheme.Length != 0)
        {
            builder.Append(uri.Scheme).Append(':');
        }

        if (!uri.Opaque)
        {
            builder.Append("//");
        }

        if ((options & 0x10) != 0 && uri.Username.Length != 0)
        {
            builder.Append(uri.Username);
        }

        if ((options & 0x20) != 0 && uri.Password.Length != 0)
        {
            builder.Append(':').Append(uri.Password);
        }

        if (((options & 0x10) != 0 && uri.Username.Length != 0) || ((options & 0x20) != 0 && uri.Password.Length != 0))
        {
            builder.Append('@');
        }

        if ((options & 2) != 0)
        {
            builder.Append(uri.Hostname);
        }

        var defaultPort = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80;
        if ((options & 4) != 0 && uri.Port != 0 && uri.Port != defaultPort)
        {
            builder.Append(':').Append(uri.Port);
        }

        if ((options & 8) != 0)
        {
            builder.Append(uri.Path);
        }

        if ((options & 0x40) != 0)
        {
            builder.Append(uri.Query);
        }

        if ((options & 0x80) != 0)
        {
            builder.Append(uri.Fragment);
        }

        return WriteStringResult(ctx, output, requiredOutput, prepared, builder.ToString());
    }

    [SysAbiExport(Nid = "CR-l-yI-o7o", ExportName = "sceHttpUriCopy", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpUriCopy(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "YuOW3dDAKYc", ExportName = "sceHttpUriEscape", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpUriEscape(CpuContext ctx) => TransformUriString(ctx, true);

    [SysAbiExport(Nid = "3lgQ5Qk42ok", ExportName = "sceHttpUriMerge", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpUriMerge(CpuContext ctx)
    {
        if (ctx[CpuRegister.R9] != 0
            || !TryReadUtf8Z(ctx, ctx[CpuRegister.Rsi], MaxStringLength, out var baseUrl)
            || !TryReadUtf8Z(ctx, ctx[CpuRegister.Rdx], MaxStringLength, out var relative)
            || !Uri.TryCreate(new Uri(baseUrl, UriKind.Absolute), relative, out var merged))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        return WriteStringResult(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rcx], ctx[CpuRegister.R8], merged.AbsoluteUri);
    }

    [SysAbiExport(Nid = "IWalAn-guFs", ExportName = "sceHttpUriParse", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpUriParse(CpuContext ctx)
    {
        var elementAddress = ctx[CpuRegister.Rdi];
        var sourceAddress = ctx[CpuRegister.Rsi];
        var poolAddress = ctx[CpuRegister.Rdx];
        var requiredAddress = ctx[CpuRegister.Rcx];
        var prepared = ctx[CpuRegister.R8];
        if (!TryReadUtf8Z(ctx, sourceAddress, MaxStringLength, out var source) || sourceAddress == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidUrl);
        }

        if ((elementAddress == 0 || poolAddress == 0) && requiredAddress == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        if (!Uri.TryCreate(source, UriKind.RelativeOrAbsolute, out var parsed))
        {
            return ctx.SetReturn(HttpErrorInvalidUrl);
        }

        var absolute = parsed.IsAbsoluteUri;
        var scheme = absolute ? parsed.Scheme : string.Empty;
        var username = absolute && parsed.UserInfo.Length != 0 ? parsed.UserInfo.Split(':', 2)[0] : string.Empty;
        var password = absolute && parsed.UserInfo.Contains(':') ? parsed.UserInfo[(parsed.UserInfo.IndexOf(':') + 1)..] : string.Empty;
        var hostname = absolute ? parsed.Host : string.Empty;
        var path = absolute ? SweepPath(parsed.AbsolutePath) : SweepPath(source.Split(['?', '#'], 2)[0]);
        var query = absolute && parsed.Query.Length != 0 ? parsed.Query : ExtractDelimitedPart(source, '?', '#');
        var fragment = absolute && parsed.Fragment.Length != 0 ? parsed.Fragment : ExtractDelimitedPart(source, '#', '\0');
        var fields = new[] { scheme, username, password, hostname, path, query, fragment };
        var encoded = fields.Select(static field => Encoding.UTF8.GetBytes(field + "\0")).ToArray();
        var required = encoded.Aggregate(0UL, static (total, bytes) => total + unchecked((ulong)bytes.Length));
        if (requiredAddress != 0 && !ctx.TryWriteUInt64(requiredAddress, required))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        if (elementAddress == 0 || poolAddress == 0)
        {
            return ctx.SetReturn(0);
        }

        if (prepared < required)
        {
            return ctx.SetReturn(HttpErrorOutOfMemory);
        }

        if (!ClearMemory(ctx, elementAddress, 80))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        var cursor = poolAddress;
        var pointerOffsets = new ulong[] { 8, 16, 24, 32, 40, 48, 56 };
        for (var index = 0; index < encoded.Length; index++)
        {
            if (!ctx.Memory.TryWrite(cursor, encoded[index]) || !ctx.TryWriteUInt64(elementAddress + pointerOffsets[index], cursor))
            {
                return ctx.SetReturn(HttpErrorInvalidValue);
            }

            cursor += unchecked((ulong)encoded[index].Length);
        }

        if (!absolute && !ctx.Memory.TryWrite(elementAddress, [1]))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        var port = absolute && parsed.Port >= 0 ? unchecked((ushort)parsed.Port) : (ushort)0;
        Span<byte> portBytes = stackalloc byte[2] { unchecked((byte)port), unchecked((byte)(port >> 8)) };
        return ctx.Memory.TryWrite(elementAddress + 64, portBytes) ? ctx.SetReturn(0) : ctx.SetReturn(HttpErrorInvalidValue);
    }

    [SysAbiExport(Nid = "mUU363n4yc0", ExportName = "sceHttpUriSweepPath", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpUriSweepPath(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdx] == 0)
        {
            return ctx.SetReturn(0);
        }

        if (!TryReadBytes(ctx, ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx], out var bytes))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        var length = Array.IndexOf(bytes, (byte)0);
        if (length < 0)
        {
            length = bytes.Length;
        }

        var result = Encoding.UTF8.GetBytes(SweepPath(Encoding.UTF8.GetString(bytes, 0, length)) + "\0");
        return ctx[CpuRegister.Rdi] != 0 && ctx.Memory.TryWrite(ctx[CpuRegister.Rdi], result)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(HttpErrorInvalidValue);
    }

    [SysAbiExport(Nid = "thTS+57zoLM", ExportName = "sceHttpUriUnescape", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpUriUnescape(CpuContext ctx) => TransformUriString(ctx, false);

    [SysAbiExport(Nid = "qISjDHrxONc", ExportName = "sceHttpWaitRequest", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpWaitRequest(CpuContext ctx)
    {
        var epollId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Epolls.TryGetValue(epollId, out var epoll))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        if (epoll.AbortRequested)
        {
            return ctx.SetReturn(HttpErrorAborted);
        }

        if (ctx[CpuRegister.Rsi] == 0 || unchecked((int)ctx[CpuRegister.Rdx]) <= 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "mSQCxzWTwVI", ExportName = "sceHttpsDisableOption", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpsDisableOption(CpuContext ctx) => SetSslFlags(ctx, 0x20FF, false);

    [SysAbiExport(Nid = "zJYi5br6ZiQ", ExportName = "sceHttpsDisableOptionPrivate", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpsDisableOptionPrivate(CpuContext ctx) => SetSslFlags(ctx, 0x2DFF, false);

    [SysAbiExport(Nid = "f42K37mm5RM", ExportName = "sceHttpsEnableOption", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpsEnableOption(CpuContext ctx) => SetSslFlags(ctx, 0x20FF, true);

    [SysAbiExport(Nid = "I4+4hKttt1w", ExportName = "sceHttpsEnableOptionPrivate", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpsEnableOptionPrivate(CpuContext ctx) => SetSslFlags(ctx, 0x2DFF, true);

    [SysAbiExport(Nid = "7WcNoAI9Zcw", ExportName = "sceHttpsFreeCaList", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpsFreeCaList(CpuContext ctx) => ClearCaList(ctx);

    [SysAbiExport(Nid = "gcUjwU3fa0M", ExportName = "sceHttpsGetCaList", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpsGetCaList(CpuContext ctx) => ClearCaList(ctx);

    [SysAbiExport(Nid = "JBN6N-EY+3M", ExportName = "sceHttpsGetSslError", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpsGetSslError(CpuContext ctx)
    {
        if (!IsHttpObject(unchecked((int)ctx[CpuRegister.Rdi])))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        return ctx[CpuRegister.Rsi] != 0 && ctx[CpuRegister.Rdx] != 0
            && ctx.TryWriteInt32(ctx[CpuRegister.Rsi], 0) && ctx.TryWriteUInt32(ctx[CpuRegister.Rdx], 0)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(HttpErrorInvalidValue);
    }

    [SysAbiExport(Nid = "DK+GoXCNT04", ExportName = "sceHttpsLoadCert", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpsLoadCert(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Contexts.ContainsKey(contextId))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        LoadedCertificates[contextId] = 0;
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "jUjp+yqMNdQ", ExportName = "sceHttpsSetMinSslVersion", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpsSetMinSslVersion(CpuContext ctx) => ReturnForObject(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "htyBOoWeS58", ExportName = "sceHttpsSetSslCallback", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpsSetSslCallback(CpuContext ctx) => ReturnForObject(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "U5ExQGyyx9s", ExportName = "sceHttpsSetSslVersion", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpsSetSslVersion(CpuContext ctx) => ReturnForObject(ctx, unchecked((int)ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "zXqcE0fizz0", ExportName = "sceHttpsUnloadCert", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceHttp")]
    public static int HttpsUnloadCert(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Contexts.ContainsKey(contextId))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        LoadedCertificates.TryRemove(contextId, out _);
        return ctx.SetReturn(0);
    }

    private sealed record UriElementValue(
        bool Opaque,
        string Scheme,
        string Username,
        string Password,
        string Hostname,
        string Path,
        string Query,
        string Fragment,
        ushort Port);

    private static int CreateRequestWithMethodName(CpuContext ctx, bool absoluteUrl)
    {
        var connectionId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Connections.TryGetValue(connectionId, out var connection))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        if (!TryReadUtf8Z(ctx, ctx[CpuRegister.Rsi], 32, out var methodName)
            || string.IsNullOrWhiteSpace(methodName)
            || methodName.IndexOfAny(['\r', '\n', ' ']) >= 0
            || !TryReadUtf8Z(ctx, ctx[CpuRegister.Rdx], MaxStringLength, out var location))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        Uri uri;
        if (absoluteUrl)
        {
            if (!Uri.TryCreate(location, UriKind.Absolute, out uri!) || uri.Scheme is not ("http" or "https"))
            {
                return ctx.SetReturn(HttpErrorInvalidUrl);
            }
        }
        else if (!Uri.TryCreate(connection.BaseUri, location, out uri!))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
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

        return CreateRequest(ctx, connectionId, method, uri, ctx[CpuRegister.Rcx]);
    }

    private static bool IsHttpObject(int id) => Templates.ContainsKey(id) || Connections.ContainsKey(id) || Requests.ContainsKey(id);

    private static int ReturnForObject(CpuContext ctx, int id) => ctx.SetReturn(IsHttpObject(id) ? 0 : HttpErrorInvalidId);

    private static int ReturnForContext(CpuContext ctx, int id) => ctx.SetReturn(Contexts.ContainsKey(id) ? 0 : HttpErrorInvalidId);

    private static int GetBooleanSetting(CpuContext ctx, Func<HttpRuntimeSettings, int> getter)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsHttpObject(id))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        return ctx[CpuRegister.Rsi] != 0 && ctx.TryWriteInt32(ctx[CpuRegister.Rsi], getter(RuntimeSettings.GetOrAdd(id, static _ => new HttpRuntimeSettings())))
            ? ctx.SetReturn(0)
            : ctx.SetReturn(HttpErrorInvalidValue);
    }

    private static int SetBooleanSetting(CpuContext ctx, Action<HttpRuntimeSettings, int> setter)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsHttpObject(id))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        var value = unchecked((int)ctx[CpuRegister.Rsi]);
        if (value is not (0 or 1))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        setter(RuntimeSettings.GetOrAdd(id, static _ => new HttpRuntimeSettings()), value);
        return ctx.SetReturn(0);
    }

    private static int SetSslFlags(CpuContext ctx, uint validMask, bool enable)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var flags = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (!IsHttpObject(id))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        if ((flags & ~validMask) != 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        var settings = RuntimeSettings.GetOrAdd(id, static _ => new HttpRuntimeSettings());
        settings.SslFlags = enable ? settings.SslFlags | flags : settings.SslFlags & ~flags;
        return ctx.SetReturn(0);
    }

    private static int ClearCaList(CpuContext ctx)
    {
        if (!Contexts.ContainsKey(unchecked((int)ctx[CpuRegister.Rdi])))
        {
            return ctx.SetReturn(HttpErrorInvalidId);
        }

        return ctx[CpuRegister.Rsi] != 0 && ClearMemory(ctx, ctx[CpuRegister.Rsi], 16)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(HttpErrorInvalidValue);
    }

    private static int ClearOptionalOutput(CpuContext ctx, ulong address, int length) => address == 0 || ClearMemory(ctx, address, length)
        ? ctx.SetReturn(0)
        : ctx.SetReturn(HttpErrorInvalidValue);

    private static int WriteEmptyBlob(CpuContext ctx, ulong sizeAddress)
    {
        if (sizeAddress != 0 && !ctx.TryWriteUInt64(sizeAddress, 0))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        return ctx.SetReturn(0);
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

    private static int WriteStringResult(CpuContext ctx, ulong output, ulong requiredAddress, ulong prepared, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\0");
        if (requiredAddress != 0 && !ctx.TryWriteUInt64(requiredAddress, unchecked((ulong)bytes.Length)))
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        if (output == 0)
        {
            return ctx.SetReturn(0);
        }

        return prepared >= unchecked((ulong)bytes.Length) && ctx.Memory.TryWrite(output, bytes)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(HttpErrorOutOfMemory);
    }

    private static int TransformUriString(CpuContext ctx, bool escape)
    {
        if (!TryReadUtf8Z(ctx, ctx[CpuRegister.Rcx], MaxStringLength, out var input) || ctx[CpuRegister.Rcx] == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        string output;
        if (escape)
        {
            var builder = new StringBuilder();
            foreach (var value in Encoding.UTF8.GetBytes(input))
            {
                if ((value >= (byte)'A' && value <= (byte)'Z')
                    || (value >= (byte)'a' && value <= (byte)'z')
                    || (value >= (byte)'0' && value <= (byte)'9')
                    || value is (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~')
                {
                    builder.Append((char)value);
                }
                else
                {
                    builder.Append('%').Append(value.ToString("X2"));
                }
            }

            output = builder.ToString();
        }
        else
        {
            try
            {
                output = Uri.UnescapeDataString(input);
            }
            catch (UriFormatException)
            {
                return ctx.SetReturn(HttpErrorInvalidValue);
            }
        }

        return WriteStringResult(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx], output);
    }

    private static bool TryReadUriElement(CpuContext ctx, ulong address, out UriElementValue value)
    {
        value = null!;
        Span<byte> opaque = stackalloc byte[1];
        if (!ctx.Memory.TryRead(address, opaque)
            || !ctx.TryReadUInt64(address + 8, out var schemeAddress)
            || !ctx.TryReadUInt64(address + 16, out var usernameAddress)
            || !ctx.TryReadUInt64(address + 24, out var passwordAddress)
            || !ctx.TryReadUInt64(address + 32, out var hostnameAddress)
            || !ctx.TryReadUInt64(address + 40, out var pathAddress)
            || !ctx.TryReadUInt64(address + 48, out var queryAddress)
            || !ctx.TryReadUInt64(address + 56, out var fragmentAddress)
            || !TryReadUInt16(ctx, address + 64, out var port)
            || !TryReadUtf8Z(ctx, schemeAddress, MaxStringLength, out var scheme)
            || !TryReadUtf8Z(ctx, usernameAddress, MaxStringLength, out var username)
            || !TryReadUtf8Z(ctx, passwordAddress, MaxStringLength, out var password)
            || !TryReadUtf8Z(ctx, hostnameAddress, MaxStringLength, out var hostname)
            || !TryReadUtf8Z(ctx, pathAddress, MaxStringLength, out var path)
            || !TryReadUtf8Z(ctx, queryAddress, MaxStringLength, out var query)
            || !TryReadUtf8Z(ctx, fragmentAddress, MaxStringLength, out var fragment))
        {
            return false;
        }

        value = new UriElementValue(opaque[0] != 0, scheme, username, password, hostname, path, query, fragment, port);
        return true;
    }

    private static string SweepPath(string path)
    {
        if (!path.StartsWith("/", StringComparison.Ordinal))
        {
            return path;
        }

        var trailingSlash = path.EndsWith("/", StringComparison.Ordinal);
        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count != 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        var result = "/" + string.Join('/', segments);
        return trailingSlash && result.Length > 1 ? result + "/" : result;
    }

    private static string ExtractDelimitedPart(string source, char start, char end)
    {
        var startIndex = source.IndexOf(start);
        if (startIndex < 0)
        {
            return string.Empty;
        }

        var endIndex = end == '\0' ? -1 : source.IndexOf(end, startIndex + 1);
        return endIndex < 0 ? source[startIndex..] : source[startIndex..endIndex];
    }

    private static bool TryReadUtf8(CpuContext ctx, ulong address, ulong length, out string value)
    {
        value = string.Empty;
        if (length > MaxBodySize || (length != 0 && address == 0) || !TryReadBytes(ctx, address, length, out var bytes))
        {
            return false;
        }

        value = Encoding.UTF8.GetString(bytes);
        return true;
    }

    private static bool TryReadBytes(CpuContext ctx, ulong address, ulong length, out byte[] bytes)
    {
        bytes = [];
        if (length > int.MaxValue || (length != 0 && address == 0))
        {
            return false;
        }

        bytes = new byte[unchecked((int)length)];
        return bytes.Length == 0 || ctx.Memory.TryRead(address, bytes);
    }

    private static bool TryReadUInt16(CpuContext ctx, ulong address, out ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        if (!ctx.Memory.TryRead(address, bytes))
        {
            value = 0;
            return false;
        }

        value = unchecked((ushort)(bytes[0] | (bytes[1] << 8)));
        return true;
    }

    private static bool TryGetStackArgument(CpuContext ctx, int index, out ulong value) =>
        ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + sizeof(ulong) + unchecked((ulong)(index * sizeof(ulong))), out value);

    private static bool ClearMemory(CpuContext ctx, ulong address, int length) => ctx.Memory.TryWrite(address, new byte[length]);

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
