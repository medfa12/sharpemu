// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

public static class NetExports
{
    private const int NetErrorBadFileDescriptor = unchecked((int)0x80410109);
    private const int NetErrorMemoryFault = unchecked((int)0x8041010E);
    private const int NetErrorInvalidArgument = unchecked((int)0x80410116);
    private const int NetErrorWouldBlock = unchecked((int)0x80410123);
    private const int NetErrorAddressFamilyNotSupported = unchecked((int)0x8041012F);
    private const int NetErrorNotInitialized = unchecked((int)0x804101C8);
    private const int NetResolverErrorNoHost = unchecked((int)0x804101E6);
    private const int MaxNameLength = 256;
    private const int MaxTransferSize = 16 * 1024 * 1024;
    private const int OrbisAddressFamilyInet = 2;
    private const int OrbisAddressFamilyInet6 = 28;
    private const int OrbisSocketTypeStream = 1;
    private const int OrbisSocketTypeDatagram = 2;
    private const int OrbisSocketTypeRaw = 3;
    private const int OrbisMessagePeek = 0x2;
    private const int OrbisMessageDontWait = 0x80;
    private const int OrbisSolSocket = 0xFFFF;
    private const int BoundedWaitMicroseconds = 100_000;

    private static readonly ConcurrentDictionary<int, NetPool> Pools = new();
    private static readonly ConcurrentDictionary<int, ResolverContext> Resolvers = new();
    private static readonly ConcurrentDictionary<int, SocketContext> Sockets = new();
    private static readonly ConcurrentDictionary<int, EpollContext> Epolls = new();
    private static int _nextPoolId;
    private static int _nextResolverId = 0x2000;
    private static int _nextSocketId = 0x100;
    private static int _nextEpollId = 0x3000;
    private static bool _initialized;

    private sealed record NetPool(string Name, int Size, int Flags);

    private sealed class ResolverContext(string name, int poolId, int flags)
    {
        public string Name { get; } = name;
        public int PoolId { get; } = poolId;
        public int Flags { get; } = flags;
        public int LastError { get; set; }
    }

    private sealed class SocketContext(Socket socket, string name)
    {
        public Socket Socket { get; } = socket;
        public string Name { get; } = name;
        public bool GuestNonBlocking { get; set; }
    }

    private sealed class EpollContext(string name)
    {
        public string Name { get; } = name;
        public Dictionary<int, EpollRegistration> Registrations { get; } = new();
        public object Gate { get; } = new();
    }

    private readonly record struct EpollRegistration(uint Events, ulong Data);

    internal static int SocketHandleCount => Sockets.Count;

    internal static int TranslateSocketError(SocketError error) => error switch
    {
        SocketError.AccessDenied => unchecked((int)0x8041010D),
        SocketError.Fault => NetErrorMemoryFault,
        SocketError.InvalidArgument => NetErrorInvalidArgument,
        SocketError.TooManyOpenSockets => unchecked((int)0x80410118),
        SocketError.WouldBlock => NetErrorWouldBlock,
        SocketError.IOPending => unchecked((int)0x80410124),
        SocketError.InProgress => unchecked((int)0x80410124),
        SocketError.AlreadyInProgress => unchecked((int)0x80410125),
        SocketError.NotSocket => NetErrorBadFileDescriptor,
        SocketError.DestinationAddressRequired => unchecked((int)0x80410127),
        SocketError.MessageSize => unchecked((int)0x80410128),
        SocketError.ProtocolType => unchecked((int)0x80410129),
        SocketError.ProtocolOption => unchecked((int)0x8041012A),
        SocketError.ProtocolNotSupported => unchecked((int)0x8041012B),
        SocketError.OperationNotSupported => unchecked((int)0x8041012D),
        SocketError.AddressFamilyNotSupported => NetErrorAddressFamilyNotSupported,
        SocketError.AddressAlreadyInUse => unchecked((int)0x80410130),
        SocketError.AddressNotAvailable => unchecked((int)0x80410131),
        SocketError.NetworkDown => unchecked((int)0x80410132),
        SocketError.NetworkUnreachable => unchecked((int)0x80410133),
        SocketError.NetworkReset => unchecked((int)0x80410134),
        SocketError.ConnectionAborted => unchecked((int)0x80410135),
        SocketError.ConnectionReset => unchecked((int)0x80410136),
        SocketError.NoBufferSpaceAvailable => unchecked((int)0x80410137),
        SocketError.IsConnected => unchecked((int)0x80410138),
        SocketError.NotConnected => unchecked((int)0x80410139),
        SocketError.Shutdown => unchecked((int)0x8041013A),
        SocketError.TimedOut => unchecked((int)0x8041013C),
        SocketError.ConnectionRefused => unchecked((int)0x8041013D),
        SocketError.HostDown => unchecked((int)0x80410140),
        SocketError.HostUnreachable => unchecked((int)0x80410141),
        SocketError.TryAgain => NetErrorWouldBlock,
        SocketError.HostNotFound => NetResolverErrorNoHost,
        _ => unchecked((int)0x8041015C),
    };

    internal static void ResetForTests()
    {
        DisposeSockets();
        Pools.Clear();
        Resolvers.Clear();
        Epolls.Clear();
        _initialized = false;
    }

    [SysAbiExport(
        Nid = "Nlev7Lg8k3A",
        ExportName = "sceNetInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetInit(CpuContext ctx)
    {
        _initialized = true;
        TraceNet("init", 0, 0, 0, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "cTGkc6-TBlI",
        ExportName = "sceNetTerm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetTerm(CpuContext ctx)
    {
        _initialized = false;
        Pools.Clear();
        Resolvers.Clear();
        Epolls.Clear();
        DisposeSockets();
        TraceNet("term", 0, 0, 0, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "dgJBaeJnGpo",
        ExportName = "sceNetPoolCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetPoolCreate(CpuContext ctx)
    {
        var nameAddress = ctx[CpuRegister.Rdi];
        var size = unchecked((int)ctx[CpuRegister.Rsi]);
        var flags = unchecked((int)ctx[CpuRegister.Rdx]);

        if (size <= 0 || !TryReadUtf8Z(ctx, nameAddress, MaxNameLength, out var name))
        {
            return ctx.SetReturn(NetErrorInvalidArgument);
        }

        var id = Interlocked.Increment(ref _nextPoolId);
        Pools[id] = new NetPool(name, size, flags);
        TraceNet("pool.create", id, unchecked((ulong)size), unchecked((ulong)flags), _initialized ? 1UL : 0UL);
        ctx[CpuRegister.Rax] = unchecked((ulong)id);
        return id;
    }

    [SysAbiExport(
        Nid = "K7RlrTkI-mw",
        ExportName = "sceNetPoolDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetPoolDestroy(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Pools.TryRemove(id, out _))
        {
            return ctx.SetReturn(NetErrorBadFileDescriptor);
        }

        TraceNet("pool.destroy", id, 0, 0, _initialized ? 1UL : 0UL);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "9T2pDF2Ryqg", ExportName = "sceNetHtonl", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetHtonl(CpuContext ctx) => ReturnUInt32(ctx, BinaryPrimitives.ReverseEndianness(unchecked((uint)ctx[CpuRegister.Rdi])));

    [SysAbiExport(Nid = "iWQWrwiSt8A", ExportName = "sceNetHtons", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetHtons(CpuContext ctx) => ReturnUInt32(ctx, BinaryPrimitives.ReverseEndianness(unchecked((ushort)ctx[CpuRegister.Rdi])));

    [SysAbiExport(Nid = "pQGpHYopAIY", ExportName = "sceNetNtohl", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetNtohl(CpuContext ctx) => ReturnUInt32(ctx, BinaryPrimitives.ReverseEndianness(unchecked((uint)ctx[CpuRegister.Rdi])));

    [SysAbiExport(Nid = "Rbvt+5Y2iEw", ExportName = "sceNetNtohs", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetNtohs(CpuContext ctx) => ReturnUInt32(ctx, BinaryPrimitives.ReverseEndianness(unchecked((ushort)ctx[CpuRegister.Rdi])));

    [SysAbiExport(Nid = "Q4qBuN-c0ZM", ExportName = "sceNetSocket", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetSocket(CpuContext ctx)
    {
        if (!_initialized)
        {
            return ctx.SetReturn(NetErrorNotInitialized);
        }

        if (!TryReadUtf8Z(ctx, ctx[CpuRegister.Rdi], MaxNameLength, out var name)
            || !TryMapAddressFamily(unchecked((int)ctx[CpuRegister.Rsi]), out var family)
            || !TryMapSocketType(unchecked((int)ctx[CpuRegister.Rdx]), out var type)
            || !TryMapProtocol(unchecked((int)ctx[CpuRegister.Rcx]), out var protocol))
        {
            return ctx.SetReturn(NetErrorInvalidArgument);
        }

        try
        {
            var socket = new Socket(family, type, protocol) { Blocking = false };
            var id = AddSocket(socket, name);
            return ReturnInt32(ctx, id);
        }
        catch (SocketException ex)
        {
            return ctx.SetReturn(TranslateSocketError(ex.SocketErrorCode));
        }
    }

    [SysAbiExport(Nid = "bErx49PgxyY", ExportName = "sceNetBind", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetBind(CpuContext ctx) => WithSocket(ctx, socket =>
    {
        if (!TryReadEndPoint(ctx, ctx[CpuRegister.Rsi], unchecked((uint)ctx[CpuRegister.Rdx]), out var endpoint))
        {
            return NetErrorInvalidArgument;
        }

        socket.Socket.Bind(endpoint);
        return 0;
    });

    [SysAbiExport(Nid = "kOj1HiAGE54", ExportName = "sceNetListen", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetListen(CpuContext ctx) => WithSocket(ctx, socket =>
    {
        socket.Socket.Listen(Math.Max(0, unchecked((int)ctx[CpuRegister.Rsi])));
        return 0;
    });

    [SysAbiExport(Nid = "PIWqhn9oSxc", ExportName = "sceNetAccept", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetAccept(CpuContext ctx) => WithSocket(ctx, socket =>
    {
        if (!socket.Socket.Poll(GetWaitMicroseconds(socket, 0), SelectMode.SelectRead))
        {
            return NetErrorWouldBlock;
        }

        var accepted = socket.Socket.Accept();
        accepted.Blocking = false;
        var id = AddSocket(accepted, $"{socket.Name}.accepted");
        if (!TryWriteOptionalEndPoint(ctx, ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx], accepted.RemoteEndPoint))
        {
            Sockets.TryRemove(id, out _);
            accepted.Dispose();
            return NetErrorMemoryFault;
        }

        return id;
    });

    [SysAbiExport(Nid = "OXXX4mUk3uk", ExportName = "sceNetConnect", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConnect(CpuContext ctx) => WithSocket(ctx, socket =>
    {
        if (!TryReadEndPoint(ctx, ctx[CpuRegister.Rsi], unchecked((uint)ctx[CpuRegister.Rdx]), out var endpoint))
        {
            return NetErrorInvalidArgument;
        }

        socket.Socket.Connect(endpoint);
        return 0;
    });

    [SysAbiExport(Nid = "beRjXBn-z+o", ExportName = "sceNetSend", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetSend(CpuContext ctx) => SendCore(ctx, false);

    [SysAbiExport(Nid = "gvD1greCu0A", ExportName = "sceNetSendto", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetSendTo(CpuContext ctx) => SendCore(ctx, true);

    [SysAbiExport(Nid = "9wO9XrMsNhc", ExportName = "sceNetRecv", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetRecv(CpuContext ctx) => ReceiveCore(ctx, false);

    [SysAbiExport(Nid = "304ooNZxWDY", ExportName = "sceNetRecvfrom", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetRecvFrom(CpuContext ctx) => ReceiveCore(ctx, true);

    [SysAbiExport(Nid = "2mKX2Spso7I", ExportName = "sceNetSetsockopt", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetSetSockOpt(CpuContext ctx) => WithSocket(ctx, socket => SetSocketOption(
        ctx,
        socket,
        unchecked((int)ctx[CpuRegister.Rsi]),
        unchecked((int)ctx[CpuRegister.Rdx]),
        ctx[CpuRegister.Rcx],
        unchecked((uint)ctx[CpuRegister.R8])));

    [SysAbiExport(Nid = "xphrZusl78E", ExportName = "sceNetGetsockopt", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetGetSockOpt(CpuContext ctx) => WithSocket(ctx, socket => GetSocketOption(
        ctx,
        socket,
        unchecked((int)ctx[CpuRegister.Rsi]),
        unchecked((int)ctx[CpuRegister.Rdx]),
        ctx[CpuRegister.Rcx],
        ctx[CpuRegister.R8]));

    [SysAbiExport(Nid = "TCkRD0DWNLg", ExportName = "sceNetGetpeername", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetGetPeerName(CpuContext ctx) => WithSocket(ctx, socket =>
        TryWriteOptionalEndPoint(ctx, ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx], socket.Socket.RemoteEndPoint) ? 0 : NetErrorMemoryFault);

    [SysAbiExport(Nid = "hoOAofhhRvE", ExportName = "sceNetGetsockname", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetGetSockName(CpuContext ctx) => WithSocket(ctx, socket =>
        TryWriteOptionalEndPoint(ctx, ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx], socket.Socket.LocalEndPoint) ? 0 : NetErrorMemoryFault);

    [SysAbiExport(Nid = "TSM6whtekok", ExportName = "sceNetShutdown", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetShutdown(CpuContext ctx) => WithSocket(ctx, socket =>
    {
        var how = unchecked((int)ctx[CpuRegister.Rsi]);
        if ((uint)how > 2)
        {
            return NetErrorInvalidArgument;
        }

        socket.Socket.Shutdown((SocketShutdown)how);
        return 0;
    });

    [SysAbiExport(Nid = "45ggEzakPJQ", ExportName = "sceNetSocketClose", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetSocketClose(CpuContext ctx)
    {
        if (!_initialized)
        {
            return ctx.SetReturn(NetErrorNotInitialized);
        }

        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Sockets.TryRemove(id, out var socket))
        {
            return ctx.SetReturn(NetErrorBadFileDescriptor);
        }

        socket.Socket.Dispose();
        foreach (var epoll in Epolls.Values)
        {
            lock (epoll.Gate)
            {
                epoll.Registrations.Remove(id);
            }
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "SF47kB2MNTo", ExportName = "sceNetEpollCreate", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetEpollCreate(CpuContext ctx)
    {
        if (!_initialized)
        {
            return ctx.SetReturn(NetErrorNotInitialized);
        }

        if (ctx[CpuRegister.Rsi] != 0 || !TryReadUtf8Z(ctx, ctx[CpuRegister.Rdi], MaxNameLength, out var name))
        {
            return ctx.SetReturn(NetErrorInvalidArgument);
        }

        var id = Interlocked.Increment(ref _nextEpollId);
        Epolls[id] = new EpollContext(name);
        return ReturnInt32(ctx, id);
    }

    [SysAbiExport(Nid = "ZVw46bsasAk", ExportName = "sceNetEpollControl", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetEpollControl(CpuContext ctx)
    {
        if (!Epolls.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var epoll))
        {
            return ctx.SetReturn(NetErrorBadFileDescriptor);
        }

        var operation = unchecked((int)ctx[CpuRegister.Rsi]);
        var socketId = unchecked((int)ctx[CpuRegister.Rdx]);
        if (!Sockets.ContainsKey(socketId))
        {
            return ctx.SetReturn(NetErrorBadFileDescriptor);
        }

        lock (epoll.Gate)
        {
            if (operation == 3)
            {
                if (ctx[CpuRegister.Rcx] != 0)
                {
                    return ctx.SetReturn(NetErrorInvalidArgument);
                }

                return epoll.Registrations.Remove(socketId)
                    ? ctx.SetReturn(0)
                    : ctx.SetReturn(NetErrorBadFileDescriptor);
            }

            if (operation is not (1 or 2) || ctx[CpuRegister.Rcx] == 0
                || !ctx.TryReadUInt32(ctx[CpuRegister.Rcx], out var events)
                || !ctx.TryReadUInt64(ctx[CpuRegister.Rcx] + 16, out var data))
            {
                return ctx.SetReturn(NetErrorInvalidArgument);
            }

            if (operation == 1 && epoll.Registrations.ContainsKey(socketId))
            {
                return ctx.SetReturn(unchecked((int)0x80410111));
            }

            if (operation == 2 && !epoll.Registrations.ContainsKey(socketId))
            {
                return ctx.SetReturn(NetErrorBadFileDescriptor);
            }

            epoll.Registrations[socketId] = new EpollRegistration(events, data);
            return ctx.SetReturn(0);
        }
    }

    [SysAbiExport(Nid = "drjIbDbA7UQ", ExportName = "sceNetEpollWait", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetEpollWait(CpuContext ctx)
    {
        if (!Epolls.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var epoll))
        {
            return ctx.SetReturn(NetErrorBadFileDescriptor);
        }

        var eventsAddress = ctx[CpuRegister.Rsi];
        var maxEvents = unchecked((int)ctx[CpuRegister.Rdx]);
        var requestedTimeout = unchecked((int)ctx[CpuRegister.Rcx]);
        if (eventsAddress == 0 || maxEvents <= 0)
        {
            return ctx.SetReturn(NetErrorInvalidArgument);
        }

        KeyValuePair<int, EpollRegistration>[] registrations;
        lock (epoll.Gate)
        {
            registrations = epoll.Registrations.ToArray();
        }

        var read = new List<Socket>();
        var write = new List<Socket>();
        var error = new List<Socket>();
        var socketIds = new Dictionary<Socket, int>();
        foreach (var registration in registrations)
        {
            if (!Sockets.TryGetValue(registration.Key, out var socket))
            {
                continue;
            }

            socketIds[socket.Socket] = registration.Key;
            if ((registration.Value.Events & 0x1) != 0)
            {
                read.Add(socket.Socket);
            }

            if ((registration.Value.Events & 0x2) != 0)
            {
                write.Add(socket.Socket);
            }

            error.Add(socket.Socket);
        }

        if (socketIds.Count == 0)
        {
            var emptyWait = requestedTimeout < 0
                ? BoundedWaitMicroseconds
                : Math.Min(Math.Max(requestedTimeout, 0), BoundedWaitMicroseconds);
            if (emptyWait != 0)
            {
                Thread.Sleep(TimeSpan.FromMicroseconds(emptyWait));
            }

            return ctx.SetReturn(0);
        }

        try
        {
            var timeout = requestedTimeout < 0
                ? BoundedWaitMicroseconds
                : Math.Min(requestedTimeout, BoundedWaitMicroseconds);
            Socket.Select(read, write, error, timeout);
        }
        catch (SocketException ex)
        {
            return ctx.SetReturn(TranslateSocketError(ex.SocketErrorCode));
        }

        var readyEvents = new Dictionary<int, uint>();
        AddReadyEvents(read, socketIds, readyEvents, 0x1);
        AddReadyEvents(write, socketIds, readyEvents, 0x2);
        AddReadyEvents(error, socketIds, readyEvents, 0x8);

        var written = 0;
        foreach (var registration in registrations)
        {
            if (written == maxEvents || !readyEvents.TryGetValue(registration.Key, out var events))
            {
                continue;
            }

            var result = new byte[24];
            BinaryPrimitives.WriteUInt32LittleEndian(result, events);
            BinaryPrimitives.WriteUInt64LittleEndian(result[8..], unchecked((ulong)registration.Key));
            BinaryPrimitives.WriteUInt64LittleEndian(result[16..], registration.Value.Data);
            if (!ctx.Memory.TryWrite(eventsAddress + unchecked((ulong)(written * 24)), result))
            {
                return ctx.SetReturn(NetErrorMemoryFault);
            }

            written++;
        }

        return ctx.SetReturn(written);
    }

    [SysAbiExport(Nid = "Inp1lfL+Jdw", ExportName = "sceNetEpollDestroy", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetEpollDestroy(CpuContext ctx) => Epolls.TryRemove(unchecked((int)ctx[CpuRegister.Rdi]), out _)
        ? ctx.SetReturn(0)
        : ctx.SetReturn(NetErrorBadFileDescriptor);

    [SysAbiExport(Nid = "9vA2aW+CHuA", ExportName = "sceNetInetNtop", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetInetNtop(CpuContext ctx)
    {
        var family = unchecked((int)ctx[CpuRegister.Rdi]);
        var sourceAddress = ctx[CpuRegister.Rsi];
        var destinationAddress = ctx[CpuRegister.Rdx];
        var destinationSize = unchecked((uint)ctx[CpuRegister.Rcx]);
        var byteCount = family switch
        {
            OrbisAddressFamilyInet => 4,
            OrbisAddressFamilyInet6 => 16,
            _ => 0,
        };
        if (byteCount == 0)
        {
            return ctx.SetReturn(0);
        }

        var addressBytes = new byte[byteCount];
        if (sourceAddress == 0 || destinationAddress == 0 || !ctx.Memory.TryRead(sourceAddress, addressBytes))
        {
            return ctx.SetReturn(0);
        }

        var text = new IPAddress(addressBytes).ToString();
        var encoded = Encoding.ASCII.GetBytes(text + '\0');
        if (encoded.Length > destinationSize)
        {
            return ctx.SetReturn(0);
        }

        if (!ctx.Memory.TryWrite(destinationAddress, encoded))
        {
            return ctx.SetReturn(0);
        }

        ctx[CpuRegister.Rax] = destinationAddress;
        return unchecked((int)destinationAddress);
    }

    [SysAbiExport(Nid = "8Kcp5d-q1Uo", ExportName = "sceNetInetPton", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetInetPton(CpuContext ctx)
    {
        var family = unchecked((int)ctx[CpuRegister.Rdi]);
        if (family is not (OrbisAddressFamilyInet or OrbisAddressFamilyInet6))
        {
            return ctx.SetReturn(-1);
        }

        if (!TryReadUtf8Z(ctx, ctx[CpuRegister.Rsi], MaxNameLength, out var source)
            || ctx[CpuRegister.Rdx] == 0)
        {
            return ctx.SetReturn(-1);
        }

        if (!IPAddress.TryParse(source, out var address)
            || (family == OrbisAddressFamilyInet && address.AddressFamily != AddressFamily.InterNetwork)
            || (family == OrbisAddressFamilyInet6 && address.AddressFamily != AddressFamily.InterNetworkV6))
        {
            return ctx.SetReturn(0);
        }

        return ctx.Memory.TryWrite(ctx[CpuRegister.Rdx], address.GetAddressBytes())
            ? ctx.SetReturn(1)
            : ctx.SetReturn(-1);
    }

    [SysAbiExport(Nid = "C4UgDHHPvdw", ExportName = "sceNetResolverCreate", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetResolverCreate(CpuContext ctx)
    {
        var flags = unchecked((int)ctx[CpuRegister.Rdx]);
        if (flags != 0 || !TryReadUtf8Z(ctx, ctx[CpuRegister.Rdi], MaxNameLength, out var name))
        {
            return ctx.SetReturn(NetErrorInvalidArgument);
        }

        var id = Interlocked.Increment(ref _nextResolverId);
        Resolvers[id] = new ResolverContext(name, unchecked((int)ctx[CpuRegister.Rsi]), flags);
        return ReturnInt32(ctx, id);
    }

    [SysAbiExport(Nid = "kJlYH5uMAWI", ExportName = "sceNetResolverDestroy", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetResolverDestroy(CpuContext ctx) => Resolvers.TryRemove(unchecked((int)ctx[CpuRegister.Rdi]), out _)
        ? ctx.SetReturn(0)
        : ctx.SetReturn(NetErrorBadFileDescriptor);

    [SysAbiExport(Nid = "J5i3hiLJMPk", ExportName = "sceNetResolverGetError", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetResolverGetError(CpuContext ctx)
    {
        if (!Resolvers.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var resolver))
        {
            return ctx.SetReturn(NetErrorBadFileDescriptor);
        }

        return ctx[CpuRegister.Rsi] != 0 && ctx.TryWriteInt32(ctx[CpuRegister.Rsi], resolver.LastError)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(NetErrorMemoryFault);
    }

    [SysAbiExport(Nid = "Nd91WaWmG2w", ExportName = "sceNetResolverStartNtoa", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetResolverStartNtoa(CpuContext ctx)
    {
        if (!Resolvers.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var resolver))
        {
            return ctx.SetReturn(NetErrorBadFileDescriptor);
        }

        if (!TryReadUtf8Z(ctx, ctx[CpuRegister.Rsi], MaxNameLength, out var hostname) || ctx[CpuRegister.Rdx] == 0)
        {
            return ctx.SetReturn(NetErrorInvalidArgument);
        }

        try
        {
            var task = Dns.GetHostAddressesAsync(hostname);
            if (!task.Wait(TimeSpan.FromMilliseconds(250)))
            {
                resolver.LastError = unchecked((int)0x804101E2);
                return ctx.SetReturn(resolver.LastError);
            }

            var address = task.Result.FirstOrDefault(static item => item.AddressFamily == AddressFamily.InterNetwork);
            if (address is null)
            {
                resolver.LastError = NetResolverErrorNoHost;
                return ctx.SetReturn(resolver.LastError);
            }

            if (!ctx.Memory.TryWrite(ctx[CpuRegister.Rdx], address.GetAddressBytes()))
            {
                return ctx.SetReturn(NetErrorMemoryFault);
            }

            resolver.LastError = 0;
            return ctx.SetReturn(0);
        }
        catch (AggregateException ex) when (ex.InnerException is SocketException socketException)
        {
            resolver.LastError = TranslateSocketError(socketException.SocketErrorCode);
            return ctx.SetReturn(resolver.LastError);
        }
        catch (SocketException ex)
        {
            resolver.LastError = TranslateSocketError(ex.SocketErrorCode);
            return ctx.SetReturn(resolver.LastError);
        }
    }

    private static int SendCore(CpuContext ctx, bool withDestination) => WithSocket(ctx, socket =>
    {
        var length = ctx[CpuRegister.Rdx];
        if (!TryGetTransferLength(length, out var count) || (count != 0 && ctx[CpuRegister.Rsi] == 0))
        {
            return NetErrorInvalidArgument;
        }

        var buffer = new byte[count];
        if (count != 0 && !ctx.Memory.TryRead(ctx[CpuRegister.Rsi], buffer))
        {
            return NetErrorMemoryFault;
        }

        var flags = MapMessageFlags(unchecked((int)ctx[CpuRegister.Rcx]));
        if (!socket.Socket.Poll(GetWaitMicroseconds(socket, unchecked((int)ctx[CpuRegister.Rcx])), SelectMode.SelectWrite))
        {
            return NetErrorWouldBlock;
        }

        if (!withDestination)
        {
            return socket.Socket.Send(buffer, flags);
        }

        if (!TryReadEndPoint(ctx, ctx[CpuRegister.R8], unchecked((uint)ctx[CpuRegister.R9]), out var endpoint))
        {
            return NetErrorInvalidArgument;
        }

        return socket.Socket.SendTo(buffer, flags, endpoint);
    });

    private static int ReceiveCore(CpuContext ctx, bool withSource) => WithSocket(ctx, socket =>
    {
        var length = ctx[CpuRegister.Rdx];
        if (!TryGetTransferLength(length, out var count) || (count != 0 && ctx[CpuRegister.Rsi] == 0))
        {
            return NetErrorInvalidArgument;
        }

        var guestFlags = unchecked((int)ctx[CpuRegister.Rcx]);
        if (!socket.Socket.Poll(GetWaitMicroseconds(socket, guestFlags), SelectMode.SelectRead))
        {
            return NetErrorWouldBlock;
        }

        var buffer = new byte[count];
        int received;
        EndPoint? source = null;
        if (withSource)
        {
            source = socket.Socket.AddressFamily == AddressFamily.InterNetwork
                ? new IPEndPoint(IPAddress.Any, 0)
                : new IPEndPoint(IPAddress.IPv6Any, 0);
            received = socket.Socket.ReceiveFrom(buffer, MapMessageFlags(guestFlags), ref source);
        }
        else
        {
            received = socket.Socket.Receive(buffer, MapMessageFlags(guestFlags));
        }

        if (received != 0 && !ctx.Memory.TryWrite(ctx[CpuRegister.Rsi], buffer.AsSpan(0, received)))
        {
            return NetErrorMemoryFault;
        }

        if (withSource && !TryWriteOptionalEndPoint(ctx, ctx[CpuRegister.R8], ctx[CpuRegister.R9], source))
        {
            return NetErrorMemoryFault;
        }

        return received;
    });

    private static int WithSocket(CpuContext ctx, Func<SocketContext, int> operation)
    {
        if (!_initialized)
        {
            return ctx.SetReturn(NetErrorNotInitialized);
        }

        if (!Sockets.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var socket))
        {
            return ctx.SetReturn(NetErrorBadFileDescriptor);
        }

        try
        {
            return ctx.SetReturn(operation(socket));
        }
        catch (SocketException ex)
        {
            return ctx.SetReturn(TranslateSocketError(ex.SocketErrorCode));
        }
        catch (ObjectDisposedException)
        {
            return ctx.SetReturn(NetErrorBadFileDescriptor);
        }
        catch (InvalidOperationException)
        {
            return ctx.SetReturn(NetErrorInvalidArgument);
        }
    }

    private static int AddSocket(Socket socket, string name)
    {
        var id = Interlocked.Increment(ref _nextSocketId);
        Sockets[id] = new SocketContext(socket, name);
        return id;
    }

    private static void AddReadyEvents(
        IEnumerable<Socket> sockets,
        IReadOnlyDictionary<Socket, int> socketIds,
        IDictionary<int, uint> readyEvents,
        uint flag)
    {
        foreach (var socket in sockets)
        {
            var id = socketIds[socket];
            readyEvents.TryGetValue(id, out var existing);
            readyEvents[id] = existing | flag;
        }
    }

    private static void DisposeSockets()
    {
        foreach (var pair in Sockets)
        {
            if (Sockets.TryRemove(pair.Key, out var context))
            {
                context.Socket.Dispose();
            }
        }
    }

    private static int SetSocketOption(CpuContext ctx, SocketContext socket, int level, int option, ulong valueAddress, uint valueLength)
    {
        if (valueAddress == 0 || valueLength < sizeof(int) || !ctx.TryReadInt32(valueAddress, out var value))
        {
            return NetErrorInvalidArgument;
        }

        if (level == OrbisSolSocket && option == 0x1200)
        {
            socket.GuestNonBlocking = value != 0;
            return 0;
        }

        if (!TryMapSocketOption(level, option, out var mappedLevel, out var mappedOption))
        {
            return unchecked((int)0x8041012A);
        }

        socket.Socket.SetSocketOption(mappedLevel, mappedOption, value);
        return 0;
    }

    private static int GetSocketOption(CpuContext ctx, SocketContext socket, int level, int option, ulong valueAddress, ulong lengthAddress)
    {
        if (valueAddress == 0 || lengthAddress == 0 || !ctx.TryReadUInt32(lengthAddress, out var length) || length < sizeof(int))
        {
            return NetErrorInvalidArgument;
        }

        int value;
        if (level == OrbisSolSocket && option == 0x1200)
        {
            value = socket.GuestNonBlocking ? 1 : 0;
        }
        else
        {
            if (!TryMapSocketOption(level, option, out var mappedLevel, out var mappedOption))
            {
                return unchecked((int)0x8041012A);
            }

            value = Convert.ToInt32(socket.Socket.GetSocketOption(mappedLevel, mappedOption));
            if (option == 0x1008)
            {
                value = socket.Socket.SocketType switch
                {
                    SocketType.Stream => OrbisSocketTypeStream,
                    SocketType.Dgram => OrbisSocketTypeDatagram,
                    SocketType.Raw => OrbisSocketTypeRaw,
                    _ => value,
                };
            }
        }

        return ctx.TryWriteInt32(valueAddress, value) && ctx.TryWriteUInt32(lengthAddress, sizeof(int))
            ? 0
            : NetErrorMemoryFault;
    }

    private static bool TryMapSocketOption(int level, int option, out SocketOptionLevel mappedLevel, out SocketOptionName mappedOption)
    {
        mappedLevel = level switch
        {
            OrbisSolSocket => SocketOptionLevel.Socket,
            0 => SocketOptionLevel.IP,
            6 => SocketOptionLevel.Tcp,
            41 => SocketOptionLevel.IPv6,
            _ => (SocketOptionLevel)(-1),
        };
        mappedOption = (level, option) switch
        {
            (OrbisSolSocket, 0x0004) => SocketOptionName.ReuseAddress,
            (OrbisSolSocket, 0x0008) => SocketOptionName.KeepAlive,
            (OrbisSolSocket, 0x0020) => SocketOptionName.Broadcast,
            (OrbisSolSocket, 0x1001) => SocketOptionName.SendBuffer,
            (OrbisSolSocket, 0x1002) => SocketOptionName.ReceiveBuffer,
            (OrbisSolSocket, 0x1007) => SocketOptionName.Error,
            (OrbisSolSocket, 0x1008) => SocketOptionName.Type,
            (0, 2) => SocketOptionName.HeaderIncluded,
            (0, 3) => SocketOptionName.TypeOfService,
            (0, 4) => SocketOptionName.IpTimeToLive,
            (0, 10) => SocketOptionName.MulticastTimeToLive,
            (0, 11) => SocketOptionName.MulticastLoopback,
            (6, 1) => SocketOptionName.NoDelay,
            _ => (SocketOptionName)(-1),
        };
        return (int)mappedLevel >= 0 && (int)mappedOption >= 0;
    }

    private static bool TryReadEndPoint(CpuContext ctx, ulong address, uint length, out IPEndPoint endpoint)
    {
        endpoint = new IPEndPoint(IPAddress.None, 0);
        if (address == 0 || length < 2 || !ctx.TryReadByte(address + 1, out var family))
        {
            return false;
        }

        var requiredLength = family switch
        {
            OrbisAddressFamilyInet => 16,
            OrbisAddressFamilyInet6 => 28,
            _ => 0,
        };
        if (requiredLength == 0 || length < requiredLength)
        {
            return false;
        }

        var bytes = new byte[requiredLength];
        if (!ctx.Memory.TryRead(address, bytes))
        {
            return false;
        }

        var port = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(2, 2));
        if (family == OrbisAddressFamilyInet)
        {
            endpoint = new IPEndPoint(new IPAddress(bytes.AsSpan(4, 4)), port);
            return true;
        }

        var scope = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24, 4));
        endpoint = new IPEndPoint(new IPAddress(bytes.AsSpan(8, 16), scope), port);
        return true;
    }

    private static bool TryWriteOptionalEndPoint(CpuContext ctx, ulong address, ulong lengthAddress, EndPoint? endpoint)
    {
        if (address == 0 && lengthAddress == 0)
        {
            return true;
        }

        if (address == 0 || lengthAddress == 0 || endpoint is not IPEndPoint ipEndpoint
            || !ctx.TryReadUInt32(lengthAddress, out var available))
        {
            return false;
        }

        var bytes = SerializeEndPoint(ipEndpoint);
        var copyLength = Math.Min(available, (uint)bytes.Length);
        return ctx.Memory.TryWrite(address, bytes.AsSpan(0, checked((int)copyLength)))
            && ctx.TryWriteUInt32(lengthAddress, unchecked((uint)bytes.Length));
    }

    private static byte[] SerializeEndPoint(IPEndPoint endpoint)
    {
        var addressBytes = endpoint.Address.GetAddressBytes();
        if (endpoint.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = new byte[16];
            bytes[0] = 16;
            bytes[1] = OrbisAddressFamilyInet;
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2, 2), unchecked((ushort)endpoint.Port));
            addressBytes.CopyTo(bytes, 4);
            return bytes;
        }

        var bytes6 = new byte[28];
        bytes6[0] = 28;
        bytes6[1] = OrbisAddressFamilyInet6;
        BinaryPrimitives.WriteUInt16BigEndian(bytes6.AsSpan(2, 2), unchecked((ushort)endpoint.Port));
        addressBytes.CopyTo(bytes6, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes6.AsSpan(24, 4), unchecked((uint)endpoint.Address.ScopeId));
        return bytes6;
    }

    private static bool TryMapAddressFamily(int value, out AddressFamily family)
    {
        family = value switch
        {
            OrbisAddressFamilyInet => AddressFamily.InterNetwork,
            OrbisAddressFamilyInet6 => AddressFamily.InterNetworkV6,
            _ => AddressFamily.Unknown,
        };
        return family != AddressFamily.Unknown;
    }

    private static bool TryMapSocketType(int value, out SocketType type)
    {
        type = value switch
        {
            OrbisSocketTypeStream => SocketType.Stream,
            OrbisSocketTypeDatagram => SocketType.Dgram,
            OrbisSocketTypeRaw => SocketType.Raw,
            _ => SocketType.Unknown,
        };
        return type != SocketType.Unknown;
    }

    private static bool TryMapProtocol(int value, out ProtocolType protocol)
    {
        protocol = value switch
        {
            0 => ProtocolType.Unspecified,
            1 => ProtocolType.Icmp,
            6 => ProtocolType.Tcp,
            17 => ProtocolType.Udp,
            41 => ProtocolType.IPv6,
            _ => ProtocolType.Unknown,
        };
        return protocol != ProtocolType.Unknown;
    }

    private static SocketFlags MapMessageFlags(int flags)
    {
        var mapped = SocketFlags.None;
        if ((flags & OrbisMessagePeek) != 0)
        {
            mapped |= SocketFlags.Peek;
        }

        return mapped;
    }

    private static int GetWaitMicroseconds(SocketContext socket, int flags) =>
        socket.GuestNonBlocking || (flags & OrbisMessageDontWait) != 0 ? 0 : BoundedWaitMicroseconds;

    private static bool TryGetTransferLength(ulong length, out int count)
    {
        if (length > MaxTransferSize)
        {
            count = 0;
            return false;
        }

        count = unchecked((int)length);
        return true;
    }

    private static int ReturnInt32(CpuContext ctx, int value)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)(long)value);
        return value;
    }

    private static int ReturnUInt32(CpuContext ctx, uint value)
    {
        ctx[CpuRegister.Rax] = value;
        return unchecked((int)value);
    }

    private static bool TryReadUtf8Z(CpuContext ctx, ulong address, int maxLength, out string value)
    {
        value = string.Empty;
        if (address == 0)
        {
            return true;
        }

        Span<byte> one = stackalloc byte[1];
        var bytes = new byte[maxLength];
        var count = 0;
        for (; count < maxLength; count++)
        {
            if (!ctx.Memory.TryRead(address + (ulong)count, one))
            {
                return false;
            }

            if (one[0] == 0)
            {
                value = Encoding.UTF8.GetString(bytes, 0, count);
                return true;
            }

            bytes[count] = one[0];
        }

        return false;
    }

    private static void TraceNet(string operation, int id, ulong arg0, ulong arg1, ulong arg2)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NET"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] net.{operation} id={id} arg0=0x{arg0:X16} arg1=0x{arg1:X16} arg2=0x{arg2:X16}");
    }
}
