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
    private static readonly ConcurrentDictionary<int, byte> DumpHandles = new();
    private static readonly ConcurrentDictionary<int, byte> EventCallbackHandles = new();
    private static readonly ConcurrentDictionary<int, byte> ResolverConnectHandles = new();
    private static readonly ConcurrentDictionary<int, byte> SyncHandles = new();
    private static readonly ConcurrentDictionary<ICpuMemory, ulong> ErrnoAddresses = new();
    private static int _nextPoolId;
    private static int _nextResolverId = 0x2000;
    private static int _nextSocketId = 0x100;
    private static int _nextEpollId = 0x3000;
    private static int _nextAuxiliaryId = 0x4000;
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

    [SysAbiExport(Nid = "ZRAJo-A-ukc", ExportName = "in6addr_any", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int In6AddrAny(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "XCuA-GqjA-k", ExportName = "in6addr_loopback", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int In6AddrLoopback(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "BTUvkWzrP68", ExportName = "sceNetAddrConfig6GetInfo", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetAddrConfig6GetInfo(CpuContext ctx) => ClearOptionalBuffer(ctx, ctx[CpuRegister.Rsi], 64);

    [SysAbiExport(Nid = "3qG7UJy2Fq8", ExportName = "sceNetAddrConfig6Start", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetAddrConfig6Start(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "P+0ePpDfUAQ", ExportName = "sceNetAddrConfig6Stop", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetAddrConfig6Stop(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "PcdLABhYga4", ExportName = "sceNetAllocateAllRouteInfo", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetAllocateAllRouteInfo(CpuContext ctx) => ClearOptionalPointer(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(Nid = "xHq87H78dho", ExportName = "sceNetBandwidthControlGetDataTraffic", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetBandwidthControlGetDataTraffic(CpuContext ctx) => ClearOptionalBuffer(ctx, ctx[CpuRegister.Rdx], 16);

    [SysAbiExport(Nid = "c8IRpl4L74I", ExportName = "sceNetBandwidthControlGetDefaultParam", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetBandwidthControlGetDefaultParam(CpuContext ctx) => ClearOptionalBuffer(ctx, ctx[CpuRegister.Rdi], 32);

    [SysAbiExport(Nid = "b9Ft65tqvLk", ExportName = "sceNetBandwidthControlGetIfParam", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetBandwidthControlGetIfParam(CpuContext ctx) => ClearOptionalBuffer(ctx, ctx[CpuRegister.Rsi], 32);

    [SysAbiExport(Nid = "PDkapOwggRw", ExportName = "sceNetBandwidthControlGetPolicy", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetBandwidthControlGetPolicy(CpuContext ctx) => ClearOptionalBuffer(ctx, ctx[CpuRegister.Rdi], 32);

    [SysAbiExport(Nid = "P4zZXE7bpsA", ExportName = "sceNetBandwidthControlSetDefaultParam", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetBandwidthControlSetDefaultParam(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "g4DKkzV2qC4", ExportName = "sceNetBandwidthControlSetIfParam", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetBandwidthControlSetIfParam(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "7Z1hhsEmkQU", ExportName = "sceNetBandwidthControlSetPolicy", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetBandwidthControlSetPolicy(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "eyLyLJrdEOU", ExportName = "sceNetClearDnsCache", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetClearDnsCache(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "Ea2NaVMQNO8", ExportName = "sceNetConfigAddArp", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigAddArp(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "0g0qIuPN3ZQ", ExportName = "sceNetConfigAddArpWithInterface", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigAddArpWithInterface(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "ge7g15Sqhks", ExportName = "sceNetConfigAddIfaddr", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigAddIfaddr(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "FDHr4Iz7dQU", ExportName = "sceNetConfigAddMRoute", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigAddMRoute(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "Cyjl1yzi1qY", ExportName = "sceNetConfigAddRoute", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigAddRoute(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "Bu+L5r1lKRg", ExportName = "sceNetConfigAddRoute6", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigAddRoute6(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "wIGold7Lro0", ExportName = "sceNetConfigAddRouteWithInterface", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigAddRouteWithInterface(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "MzA1YrRE6rA", ExportName = "sceNetConfigCleanUpAllInterfaces", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigCleanUpAllInterfaces(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "HJt+4x-CnY0", ExportName = "sceNetConfigDelArp", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigDelArp(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "xTcttXJ3Utg", ExportName = "sceNetConfigDelArpWithInterface", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigDelArpWithInterface(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "RuVwHEW6dM4", ExportName = "sceNetConfigDelDefaultRoute", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigDelDefaultRoute(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "UMlVCy7RX1s", ExportName = "sceNetConfigDelDefaultRoute6", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigDelDefaultRoute6(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "0239JNsI6PE", ExportName = "sceNetConfigDelIfaddr", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigDelIfaddr(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "hvCXMwd45oc", ExportName = "sceNetConfigDelIfaddr6", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigDelIfaddr6(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "5Yl1uuh5i-A", ExportName = "sceNetConfigDelMRoute", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigDelMRoute(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "QO7+2E3cD-U", ExportName = "sceNetConfigDelRoute", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigDelRoute(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "4wDGvfhmkmk", ExportName = "sceNetConfigDelRoute6", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigDelRoute6(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "3WzWV86AJ3w", ExportName = "sceNetConfigDownInterface", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigDownInterface(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "mOUkgTaSkJU", ExportName = "sceNetConfigEtherGetLinkMode", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigEtherGetLinkMode(CpuContext ctx) => WriteRequiredUInt32(ctx, ctx[CpuRegister.Rdi], 1);

    [SysAbiExport(Nid = "pF3Vy1iZ5bs", ExportName = "sceNetConfigEtherPostPlugInOutEvent", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigEtherPostPlugInOutEvent(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "QltDK6wWqF0", ExportName = "sceNetConfigEtherSetLinkMode", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigEtherSetLinkMode(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "18KNgSvYx+Y", ExportName = "sceNetConfigFlushRoute", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigFlushRoute(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "lFJb+BlPK1c", ExportName = "sceNetConfigGetDefaultRoute", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigGetDefaultRoute(CpuContext ctx) => WriteIpv4Sockaddr(ctx, ctx[CpuRegister.Rdi], [192, 168, 0, 1]);

    [SysAbiExport(Nid = "mCLdiNIKtW0", ExportName = "sceNetConfigGetDefaultRoute6", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigGetDefaultRoute6(CpuContext ctx) => ClearOptionalBuffer(ctx, ctx[CpuRegister.Rdi], 28);

    [SysAbiExport(Nid = "ejwa0hWWhDs", ExportName = "sceNetConfigGetIfaddr", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigGetIfaddr(CpuContext ctx) => WriteIpv4Sockaddr(ctx, ctx[CpuRegister.Rsi], [192, 168, 0, 2]);

    [SysAbiExport(Nid = "FU6NK4RHQVE", ExportName = "sceNetConfigGetIfaddr6", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigGetIfaddr6(CpuContext ctx) => ClearOptionalBuffer(ctx, ctx[CpuRegister.Rsi], 28);

    [SysAbiExport(Nid = "vbZLomImmEE", ExportName = "sceNetConfigRoutingShowRoutingConfig", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigRoutingShowRoutingConfig(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "a6sS6iSE0IA", ExportName = "sceNetConfigRoutingShowtCtlVar", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigRoutingShowtCtlVar(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "eszLdtIMfQE", ExportName = "sceNetConfigRoutingStart", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigRoutingStart(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "toi8xxcSfJ0", ExportName = "sceNetConfigRoutingStop", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigRoutingStop(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "EAl7xvi7nXg", ExportName = "sceNetConfigSetDefaultRoute", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigSetDefaultRoute(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "4zLOHbt3UFk", ExportName = "sceNetConfigSetDefaultRoute6", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigSetDefaultRoute6(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "yaVAdLDxUj0", ExportName = "sceNetConfigSetDefaultScope", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigSetDefaultScope(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "0sesmAYH3Lk", ExportName = "sceNetConfigSetIfFlags", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigSetIfFlags(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "uNTluLfYgS8", ExportName = "sceNetConfigSetIfLinkLocalAddr6", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigSetIfLinkLocalAddr6(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "8Kh+1eidI3c", ExportName = "sceNetConfigSetIfaddr", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigSetIfaddr(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "QJbV3vfBQ8Q", ExportName = "sceNetConfigSetIfaddr6", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigSetIfaddr6(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "POrSEl8zySw", ExportName = "sceNetConfigSetIfaddr6WithFlags", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigSetIfaddr6WithFlags(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "s31rYkpIMMQ", ExportName = "sceNetConfigSetIfmtu", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigSetIfmtu(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "tvdzQkm+UaY", ExportName = "sceNetConfigUnsetIfFlags", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigUnsetIfFlags(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "oGEBX0eXGFs", ExportName = "sceNetConfigUpInterface", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigUpInterface(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "6HNbayHPL7c", ExportName = "sceNetConfigUpInterfaceWithFlags", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigUpInterfaceWithFlags(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "6A6EweB3Dto", ExportName = "sceNetConfigWlanAdhocClearWakeOnWlan", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigWlanAdhocClearWakeOnWlan(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "ZLdJyQJUMkM", ExportName = "sceNetConfigWlanAdhocCreate", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigWlanAdhocCreate(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "Yr3UeApLWTY", ExportName = "sceNetConfigWlanAdhocGetWakeOnWlanInfo", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigWlanAdhocGetWakeOnWlanInfo(CpuContext ctx) => ClearOptionalBuffer(ctx, ctx[CpuRegister.Rdi], 32);

    [SysAbiExport(Nid = "Xma8yHmV+TQ", ExportName = "sceNetConfigWlanAdhocJoin", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigWlanAdhocJoin(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "K4o48GTNbSc", ExportName = "sceNetConfigWlanAdhocLeave", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigWlanAdhocLeave(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "ZvKgNrrLCCQ", ExportName = "sceNetConfigWlanAdhocPspEmuClearWakeOnWlan", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigWlanAdhocPspEmuClearWakeOnWlan(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "1j4DZ5dXbeQ", ExportName = "sceNetConfigWlanAdhocPspEmuGetWakeOnWlanInfo", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigWlanAdhocPspEmuGetWakeOnWlanInfo(CpuContext ctx) => ClearOptionalBuffer(ctx, ctx[CpuRegister.Rdi], 32);

    [SysAbiExport(Nid = "C-+JPjaEhdA", ExportName = "sceNetConfigWlanAdhocPspEmuSetWakeOnWlan", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigWlanAdhocPspEmuSetWakeOnWlan(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "7xYdUWg1WdY", ExportName = "sceNetConfigWlanAdhocScanJoin", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigWlanAdhocScanJoin(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "Q7ee2Uav5f8", ExportName = "sceNetConfigWlanAdhocSetExtInfoElement", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigWlanAdhocSetExtInfoElement(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "xaOTiuxIQNY", ExportName = "sceNetConfigWlanAdhocSetWakeOnWlan", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigWlanAdhocSetWakeOnWlan(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "QlRJWya+dtE", ExportName = "sceNetConfigWlanApStart", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigWlanApStart(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "6uYcvVjH7Ms", ExportName = "sceNetConfigWlanApStop", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigWlanApStop(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "MDbg-oAj8Aw", ExportName = "sceNetConfigWlanBackgroundScanQuery", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigWlanBackgroundScanQuery(CpuContext ctx) => ClearOptionalBuffer(ctx, ctx[CpuRegister.Rdi], 32);

    [SysAbiExport(Nid = "cMA8f6jI6s0", ExportName = "sceNetConfigWlanBackgroundScanStart", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigWlanBackgroundScanStart(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "3T5aIe-7L84", ExportName = "sceNetConfigWlanBackgroundScanStop", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigWlanBackgroundScanStop(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "+3KMyS93TOs", ExportName = "sceNetConfigWlanDiagGetDeviceInfo", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigWlanDiagGetDeviceInfo(CpuContext ctx) => ClearOptionalBuffer(ctx, ctx[CpuRegister.Rdi], 64);

    [SysAbiExport(Nid = "9oiOWQ5FMws", ExportName = "sceNetConfigWlanDiagSetAntenna", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigWlanDiagSetAntenna(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "fHr45B97n0U", ExportName = "sceNetConfigWlanDiagSetTxFixedRate", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigWlanDiagSetTxFixedRate(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "PNDDxnqqtk4", ExportName = "sceNetConfigWlanGetDeviceConfig", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigWlanGetDeviceConfig(CpuContext ctx) => ClearOptionalBuffer(ctx, ctx[CpuRegister.Rdi], 64);

    [SysAbiExport(Nid = "Pkx0lwWVzmQ", ExportName = "sceNetConfigWlanInfraGetRssiInfo", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigWlanInfraGetRssiInfo(CpuContext ctx) => ClearOptionalBuffer(ctx, ctx[CpuRegister.Rdi], 16);

    [SysAbiExport(Nid = "IkBCxG+o4Nk", ExportName = "sceNetConfigWlanInfraLeave", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigWlanInfraLeave(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "273-I-zD8+8", ExportName = "sceNetConfigWlanInfraScanJoin", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigWlanInfraScanJoin(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "-Mi5hNiWC4c", ExportName = "sceNetConfigWlanScan", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigWlanScan(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "U1q6DrPbY6k", ExportName = "sceNetConfigWlanSetDeviceConfig", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetConfigWlanSetDeviceConfig(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "lDTIbqNs0ps", ExportName = "sceNetControl", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetControl(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "KhQxhlEslo0", ExportName = "sceNetDhcpGetAutoipInfo", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetDhcpGetAutoipInfo(CpuContext ctx) => ClearOptionalBuffer(ctx, ctx[CpuRegister.Rsi], 64);

    [SysAbiExport(Nid = "ix4LWXd12F0", ExportName = "sceNetDhcpGetInfo", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetDhcpGetInfo(CpuContext ctx) => ClearOptionalBuffer(ctx, ctx[CpuRegister.Rsi], 128);

    [SysAbiExport(Nid = "DrZuCQDnm3w", ExportName = "sceNetDhcpGetInfoEx", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetDhcpGetInfoEx(CpuContext ctx) => ClearOptionalBuffer(ctx, ctx[CpuRegister.Rsi], 256);

    [SysAbiExport(Nid = "Wzv6dngR-DQ", ExportName = "sceNetDhcpStart", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetDhcpStart(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "6AN7OlSMWk0", ExportName = "sceNetDhcpStop", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetDhcpStop(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "Q6T-zIblNqk", ExportName = "sceNetDhcpdStart", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetDhcpdStart(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "xwWm8jzrpeM", ExportName = "sceNetDhcpdStop", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetDhcpdStop(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "+ezgWao0wo8", ExportName = "sceNetDumpAbort", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetDumpAbort(CpuContext ctx) => ValidateAuxiliaryHandle(ctx, DumpHandles);

    [SysAbiExport(Nid = "bghgkeLKq1Q", ExportName = "sceNetDumpCreate", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetDumpCreate(CpuContext ctx) => CreateAuxiliaryHandle(ctx, DumpHandles);

    [SysAbiExport(Nid = "xZ54Il-u1vs", ExportName = "sceNetDumpDestroy", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetDumpDestroy(CpuContext ctx) => DestroyAuxiliaryHandle(ctx, DumpHandles);

    [SysAbiExport(Nid = "YWTpt45PxbI", ExportName = "sceNetDumpRead", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetDumpRead(CpuContext ctx)
    {
        if (!DumpHandles.ContainsKey(unchecked((int)ctx[CpuRegister.Rdi])))
        {
            return ctx.SetReturn(NetErrorBadFileDescriptor);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "TwjkDIPdZ1Q", ExportName = "sceNetDuplicateIpStart", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetDuplicateIpStart(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "QCbvCx9HL30", ExportName = "sceNetDuplicateIpStop", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetDuplicateIpStop(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "w21YgGGNtBk", ExportName = "sceNetEpollAbort", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetEpollAbort(CpuContext ctx) => Epolls.ContainsKey(unchecked((int)ctx[CpuRegister.Rdi]))
        ? ctx.SetReturn(0)
        : ctx.SetReturn(NetErrorBadFileDescriptor);

    [SysAbiExport(Nid = "HQOwnfMGipQ", ExportName = "sceNetErrnoLoc", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetErrnoLoc(CpuContext ctx)
    {
        if (!ErrnoAddresses.TryGetValue(ctx.Memory, out var address))
        {
            if (ctx.Memory is not IGuestMemoryAllocator allocator ||
                !allocator.TryAllocateGuestMemory(sizeof(int), sizeof(int), out address) ||
                !ctx.TryWriteInt32(address, 0))
            {
                return ctx.SetReturn(0);
            }

            address = ErrnoAddresses.GetOrAdd(ctx.Memory, address);
        }

        ctx[CpuRegister.Rax] = address;
        return unchecked((int)address);
    }

    [SysAbiExport(Nid = "v6M4txecCuo", ExportName = "sceNetEtherNtostr", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetEtherNtostr(CpuContext ctx)
    {
        Span<byte> mac = stackalloc byte[6];
        if (ctx[CpuRegister.Rdi] == 0 || ctx[CpuRegister.Rsi] == 0 || !ctx.Memory.TryRead(ctx[CpuRegister.Rdi], mac))
        {
            return ctx.SetReturn(NetErrorInvalidArgument);
        }

        var value = string.Join(':', mac.ToArray().Select(static item => item.ToString("x2"))) + '\0';
        return ctx.Memory.TryWrite(ctx[CpuRegister.Rsi], Encoding.ASCII.GetBytes(value))
            ? ctx.SetReturn(0)
            : ctx.SetReturn(NetErrorMemoryFault);
    }

    [SysAbiExport(Nid = "b-bFZvNV59I", ExportName = "sceNetEtherStrton", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetEtherStrton(CpuContext ctx)
    {
        if (!TryReadUtf8Z(ctx, ctx[CpuRegister.Rdi], 18, out var text) || ctx[CpuRegister.Rsi] == 0)
        {
            return ctx.SetReturn(NetErrorInvalidArgument);
        }

        var parts = text.Split(':');
        var bytes = new byte[6];
        if (parts.Length != bytes.Length)
        {
            return ctx.SetReturn(NetErrorInvalidArgument);
        }

        for (var index = 0; index < parts.Length; index++)
        {
            if (!byte.TryParse(parts[index], System.Globalization.NumberStyles.HexNumber, null, out bytes[index]))
            {
                return ctx.SetReturn(NetErrorInvalidArgument);
            }
        }

        return ctx.Memory.TryWrite(ctx[CpuRegister.Rsi], bytes)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(NetErrorMemoryFault);
    }

    [SysAbiExport(Nid = "cWGGXoeZUzA", ExportName = "sceNetEventCallbackCreate", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetEventCallbackCreate(CpuContext ctx) => CreateAuxiliaryHandle(ctx, EventCallbackHandles);

    [SysAbiExport(Nid = "jzP0MoZpYnI", ExportName = "sceNetEventCallbackDestroy", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetEventCallbackDestroy(CpuContext ctx) => DestroyAuxiliaryHandle(ctx, EventCallbackHandles);

    [SysAbiExport(Nid = "tB3BB8AsrjU", ExportName = "sceNetEventCallbackGetError", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetEventCallbackGetError(CpuContext ctx)
    {
        if (!EventCallbackHandles.ContainsKey(unchecked((int)ctx[CpuRegister.Rdi])))
        {
            return ctx.SetReturn(NetErrorBadFileDescriptor);
        }

        return WriteRequiredInt32(ctx, ctx[CpuRegister.Rsi], 0);
    }

    [SysAbiExport(Nid = "5isaotjMWlA", ExportName = "sceNetEventCallbackWaitCB", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetEventCallbackWaitCB(CpuContext ctx) => ValidateAuxiliaryHandle(ctx, EventCallbackHandles);

    [SysAbiExport(Nid = "2ee14ktE1lw", ExportName = "sceNetFreeAllRouteInfo", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetFreeAllRouteInfo(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "q8j9OSdnN1Y", ExportName = "sceNetGetArpInfo", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetGetArpInfo(CpuContext ctx) => ClearCountedResult(ctx);

    [SysAbiExport(Nid = "wmoIm94hqik", ExportName = "sceNetGetDns6Info", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetGetDns6Info(CpuContext ctx) => ClearOptionalBuffer(ctx, ctx[CpuRegister.Rdi], 32);

    [SysAbiExport(Nid = "nCL0NyZsd5A", ExportName = "sceNetGetDnsInfo", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetGetDnsInfo(CpuContext ctx)
    {
        Span<byte> dns = stackalloc byte[8];
        dns[0] = 1;
        dns[1] = 1;
        dns[2] = 1;
        dns[3] = 1;
        dns[4] = 1;
        dns[5] = 1;
        dns[6] = 1;
        dns[7] = 1;
        return WriteOptionalBuffer(ctx, ctx[CpuRegister.Rdi], dns);
    }

    [SysAbiExport(Nid = "HoV-GJyx7YY", ExportName = "sceNetGetIfList", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetGetIfList(CpuContext ctx) => ClearCountedResult(ctx);

    [SysAbiExport(Nid = "ahiOMqoYYMc", ExportName = "sceNetGetIfListOnce", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetGetIfListOnce(CpuContext ctx) => ClearCountedResult(ctx);

    [SysAbiExport(Nid = "0MT2l3uIX7c", ExportName = "sceNetGetIfName", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetGetIfName(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rsi] == 0)
        {
            return ctx.SetReturn(NetErrorInvalidArgument);
        }

        return ctx.Memory.TryWrite(ctx[CpuRegister.Rsi], Encoding.ASCII.GetBytes("eth0\0"))
            ? ctx.SetReturn(0)
            : ctx.SetReturn(NetErrorMemoryFault);
    }

    [SysAbiExport(Nid = "5lrSEHdqyos", ExportName = "sceNetGetIfnameNumList", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetGetIfnameNumList(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdi] != 0 && !ctx.TryWriteUInt32(ctx[CpuRegister.Rdi], 1))
        {
            return ctx.SetReturn(NetErrorMemoryFault);
        }

        return ctx[CpuRegister.Rsi] == 0 || ctx.TryWriteUInt32(ctx[CpuRegister.Rsi], 1)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(NetErrorMemoryFault);
    }

    [SysAbiExport(Nid = "6Oc0bLsIYe0", ExportName = "sceNetGetMacAddress", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetGetMacAddress(CpuContext ctx) => WriteRequiredBuffer(ctx, ctx[CpuRegister.Rdi], [0x02, 0x53, 0x48, 0x41, 0x52, 0x50]);

    [SysAbiExport(Nid = "rMyh97BU5pY", ExportName = "sceNetGetMemoryPoolStats", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetGetMemoryPoolStats(CpuContext ctx) => ClearOptionalBuffer(ctx, ctx[CpuRegister.Rdi], 64);

    [SysAbiExport(Nid = "+S-2-jlpaBo", ExportName = "sceNetGetNameToIndex", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetGetNameToIndex(CpuContext ctx)
    {
        if (!TryReadUtf8Z(ctx, ctx[CpuRegister.Rdi], 32, out var name))
        {
            return ctx.SetReturn(NetErrorInvalidArgument);
        }

        return ReturnInt32(ctx, name is "eth0" or "en0" ? 1 : 0);
    }

    [SysAbiExport(Nid = "G3O2j9f5z00", ExportName = "sceNetGetRandom", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetGetRandom(CpuContext ctx)
    {
        if (!TryGetTransferLength(ctx[CpuRegister.Rsi], out var count) || (count != 0 && ctx[CpuRegister.Rdi] == 0))
        {
            return ctx.SetReturn(NetErrorInvalidArgument);
        }

        var bytes = new byte[count];
        Random.Shared.NextBytes(bytes);
        return count == 0 || ctx.Memory.TryWrite(ctx[CpuRegister.Rdi], bytes)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(NetErrorMemoryFault);
    }

    [SysAbiExport(Nid = "6Nx1hIQL9h8", ExportName = "sceNetGetRouteInfo", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetGetRouteInfo(CpuContext ctx) => ClearCountedResult(ctx);

    [SysAbiExport(Nid = "hLuXdjHnhiI", ExportName = "sceNetGetSockInfo", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetGetSockInfo(CpuContext ctx) => GetSocketInfoCore(ctx, 64);

    [SysAbiExport(Nid = "Cidi9Y65mP8", ExportName = "sceNetGetSockInfo6", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetGetSockInfo6(CpuContext ctx) => GetSocketInfoCore(ctx, 96);

    [SysAbiExport(Nid = "GA5ZDaLtUBE", ExportName = "sceNetGetStatisticsInfo", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetGetStatisticsInfo(CpuContext ctx) => ClearOptionalBuffer(ctx, ctx[CpuRegister.Rdi], 256);

    [SysAbiExport(Nid = "9mIcUExH34w", ExportName = "sceNetGetStatisticsInfoInternal", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetGetStatisticsInfoInternal(CpuContext ctx) => ClearOptionalBuffer(ctx, ctx[CpuRegister.Rdi], 256);

    [SysAbiExport(Nid = "p2vxsE2U3RQ", ExportName = "sceNetGetSystemTime", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetGetSystemTime(CpuContext ctx) => ReturnUInt32(ctx, unchecked((uint)Environment.TickCount64));

    [SysAbiExport(Nid = "3CHi1K1wsCQ", ExportName = "sceNetHtonll", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetHtonll(CpuContext ctx) => ReturnUInt64(ctx, BinaryPrimitives.ReverseEndianness(ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "Eh+Vqkrrc00", ExportName = "sceNetInetNtopWithScopeId", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetInetNtopWithScopeId(CpuContext ctx) => NetInetNtop(ctx);

    [SysAbiExport(Nid = "Xn2TA2QhxHc", ExportName = "sceNetInetPtonEx", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetInetPtonEx(CpuContext ctx) => NetInetPton(ctx);

    [SysAbiExport(Nid = "b+LixqREH6A", ExportName = "sceNetInetPtonWithScopeId", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetInetPtonWithScopeId(CpuContext ctx)
    {
        var result = NetInetPton(ctx);
        if (result == 1 && ctx[CpuRegister.Rcx] != 0 && !ctx.TryWriteUInt32(ctx[CpuRegister.Rcx], 0))
        {
            return ctx.SetReturn(-1);
        }

        return result;
    }

    [SysAbiExport(Nid = "cYW1ISGlOmo", ExportName = "sceNetInfoDumpStart", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetInfoDumpStart(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "XfV-XBCuhDo", ExportName = "sceNetInfoDumpStop", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetInfoDumpStop(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "6MojQ8uFHEI", ExportName = "sceNetInitParam", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetInitParam(CpuContext ctx)
    {
        _initialized = true;
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "ghqRRVQxqKo", ExportName = "sceNetIoctl", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetIoctl(CpuContext ctx) => Sockets.ContainsKey(unchecked((int)ctx[CpuRegister.Rdi]))
        ? ctx.SetReturn(0)
        : ctx.SetReturn(NetErrorBadFileDescriptor);

    [SysAbiExport(Nid = "HKIa-WH0AZ4", ExportName = "sceNetMemoryAllocate", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetMemoryAllocate(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdi] == 0 || ctx.Memory is not IGuestMemoryAllocator allocator ||
            !allocator.TryAllocateGuestMemory(ctx[CpuRegister.Rdi], 16, out var address))
        {
            return ctx.SetReturn(0);
        }

        ctx[CpuRegister.Rax] = address;
        return unchecked((int)address);
    }

    [SysAbiExport(Nid = "221fvqVs+sQ", ExportName = "sceNetMemoryFree", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetMemoryFree(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "tOrRi-v3AOM", ExportName = "sceNetNtohll", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetNtohll(CpuContext ctx) => ReturnUInt64(ctx, BinaryPrimitives.ReverseEndianness(ctx[CpuRegister.Rdi]));

    [SysAbiExport(Nid = "QGOqGPnk5a4", ExportName = "sceNetPppoeStart", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetPppoeStart(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "FIV95WE1EuE", ExportName = "sceNetPppoeStop", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetPppoeStop(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "wvuUDv0jrMI", ExportName = "sceNetRecvmsg", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetRecvmsg(CpuContext ctx) => MessageCore(ctx, false);

    [SysAbiExport(Nid = "AzqoBha7js4", ExportName = "sceNetResolverAbort", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetResolverAbort(CpuContext ctx) => Resolvers.ContainsKey(unchecked((int)ctx[CpuRegister.Rdi]))
        ? ctx.SetReturn(0)
        : ctx.SetReturn(NetErrorBadFileDescriptor);

    [SysAbiExport(Nid = "JQk8ck8vnPY", ExportName = "sceNetResolverConnect", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetResolverConnect(CpuContext ctx) => ValidateAuxiliaryHandle(ctx, ResolverConnectHandles);

    [SysAbiExport(Nid = "bonnMiDoOZg", ExportName = "sceNetResolverConnectAbort", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetResolverConnectAbort(CpuContext ctx) => ValidateAuxiliaryHandle(ctx, ResolverConnectHandles);

    [SysAbiExport(Nid = "V5q6gvEJpw4", ExportName = "sceNetResolverConnectCreate", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetResolverConnectCreate(CpuContext ctx) => CreateAuxiliaryHandle(ctx, ResolverConnectHandles);

    [SysAbiExport(Nid = "QFPjG6rqeZg", ExportName = "sceNetResolverConnectDestroy", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetResolverConnectDestroy(CpuContext ctx) => DestroyAuxiliaryHandle(ctx, ResolverConnectHandles);

    [SysAbiExport(Nid = "Apb4YDxKsRI", ExportName = "sceNetResolverStartAton", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetResolverStartAton(CpuContext ctx) => ResolverAddressToName(ctx, 4);

    [SysAbiExport(Nid = "zvzWA5IZMsg", ExportName = "sceNetResolverStartAton6", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetResolverStartAton6(CpuContext ctx) => ResolverAddressToName(ctx, 16);

    [SysAbiExport(Nid = "zl35YNs9jnI", ExportName = "sceNetResolverStartNtoa6", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetResolverStartNtoa6(CpuContext ctx) => ResolverNameToAddress(ctx, AddressFamily.InterNetworkV6);

    [SysAbiExport(Nid = "RCCY01Xd+58", ExportName = "sceNetResolverStartNtoaMultipleRecords", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetResolverStartNtoaMultipleRecords(CpuContext ctx) => ResolverMultipleRecords(ctx);

    [SysAbiExport(Nid = "sT4nBQKUPqM", ExportName = "sceNetResolverStartNtoaMultipleRecordsEx", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetResolverStartNtoaMultipleRecordsEx(CpuContext ctx) => ResolverMultipleRecords(ctx);

    [SysAbiExport(Nid = "2eKbgcboJso", ExportName = "sceNetSendmsg", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetSendmsg(CpuContext ctx) => MessageCore(ctx, true);

    [SysAbiExport(Nid = "15Ywg-ZsSl0", ExportName = "sceNetSetDns6Info", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetSetDns6Info(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "E3oH1qsdqCA", ExportName = "sceNetSetDns6InfoToKernel", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetSetDns6InfoToKernel(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "B-M6KjO8-+w", ExportName = "sceNetSetDnsInfo", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetSetDnsInfo(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "8s+T0bJeyLQ", ExportName = "sceNetSetDnsInfoToKernel", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetSetDnsInfoToKernel(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "k1V1djYpk7k", ExportName = "sceNetShowIfconfig", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetShowIfconfig(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "j6pkkO2zJtg", ExportName = "sceNetShowIfconfigForBuffer", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetShowIfconfigForBuffer(CpuContext ctx) => WriteEmptyText(ctx);

    [SysAbiExport(Nid = "E8dTcvQw3hg", ExportName = "sceNetShowIfconfigWithMemory", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetShowIfconfigWithMemory(CpuContext ctx) => ClearOptionalPointer(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(Nid = "WxislcDAW5I", ExportName = "sceNetShowNetstat", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetShowNetstat(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "rX30iWQqqzg", ExportName = "sceNetShowNetstatEx", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetShowNetstatEx(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "vjwKTGa21f0", ExportName = "sceNetShowNetstatExForBuffer", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetShowNetstatExForBuffer(CpuContext ctx) => WriteEmptyText(ctx);

    [SysAbiExport(Nid = "mqoB+LN0pW8", ExportName = "sceNetShowNetstatForBuffer", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetShowNetstatForBuffer(CpuContext ctx) => WriteEmptyText(ctx);

    [SysAbiExport(Nid = "H5WHYRfDkR0", ExportName = "sceNetShowNetstatWithMemory", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetShowNetstatWithMemory(CpuContext ctx) => ClearOptionalPointer(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(Nid = "tk0p0JmiBkM", ExportName = "sceNetShowPolicy", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetShowPolicy(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "dbrSNEuZfXI", ExportName = "sceNetShowPolicyWithMemory", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetShowPolicyWithMemory(CpuContext ctx) => ClearOptionalPointer(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(Nid = "cEMX1VcPpQ8", ExportName = "sceNetShowRoute", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetShowRoute(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "fCa7-ihdRdc", ExportName = "sceNetShowRoute6", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetShowRoute6(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "nTJqXsbSS1I", ExportName = "sceNetShowRoute6ForBuffer", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetShowRoute6ForBuffer(CpuContext ctx) => WriteEmptyText(ctx);

    [SysAbiExport(Nid = "TCZyE2YI1uM", ExportName = "sceNetShowRoute6WithMemory", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetShowRoute6WithMemory(CpuContext ctx) => ClearOptionalPointer(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(Nid = "n-IAZb7QB1Y", ExportName = "sceNetShowRouteForBuffer", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetShowRouteForBuffer(CpuContext ctx) => WriteEmptyText(ctx);

    [SysAbiExport(Nid = "0-XSSp1kEFM", ExportName = "sceNetShowRouteWithMemory", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetShowRouteWithMemory(CpuContext ctx) => ClearOptionalPointer(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(Nid = "zJGf8xjFnQE", ExportName = "sceNetSocketAbort", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetSocketAbort(CpuContext ctx) => WithSocket(ctx, socket =>
    {
        try
        {
            socket.Socket.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
        }

        return 0;
    });

    [SysAbiExport(Nid = "6AJE2jKg-c0", ExportName = "sceNetSyncCreate", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetSyncCreate(CpuContext ctx) => CreateAuxiliaryHandle(ctx, SyncHandles);

    [SysAbiExport(Nid = "atGfzCaXMak", ExportName = "sceNetSyncDestroy", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetSyncDestroy(CpuContext ctx) => DestroyAuxiliaryHandle(ctx, SyncHandles);

    [SysAbiExport(Nid = "sAleh-BoxLA", ExportName = "sceNetSyncGet", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetSyncGet(CpuContext ctx) => ValidateAuxiliaryHandle(ctx, SyncHandles);

    [SysAbiExport(Nid = "Z-8Jda650Vk", ExportName = "sceNetSyncSignal", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetSyncSignal(CpuContext ctx) => ValidateAuxiliaryHandle(ctx, SyncHandles);

    [SysAbiExport(Nid = "NP5gxDeYhIM", ExportName = "sceNetSyncWait", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetSyncWait(CpuContext ctx) => ValidateAuxiliaryHandle(ctx, SyncHandles);

    [SysAbiExport(Nid = "3zRdT3O2Kxo", ExportName = "sceNetSysctl", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetSysctl(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "j-Op3ibRJaQ", ExportName = "sceNetThreadCreate", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetThreadCreate(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "KirVfZbqniw", ExportName = "sceNetThreadExit", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetThreadExit(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "pRbEzaV30qI", ExportName = "sceNetThreadJoin", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetThreadJoin(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "bjrzRLFali0", ExportName = "sceNetUsleep", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetUsleep(CpuContext ctx)
    {
        var microseconds = Math.Min(ctx[CpuRegister.Rdi], unchecked((ulong)BoundedWaitMicroseconds));
        if (microseconds != 0)
        {
            Thread.Sleep(TimeSpan.FromMicroseconds(microseconds));
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "VZgoeBxPXUQ", ExportName = "sce_net_dummy", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetDummy(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "GAtITrgxKDE", ExportName = "sce_net_in6addr_any", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetIn6AddrAny(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "84MgU4MMTLQ", ExportName = "sce_net_in6addr_linklocal_allnodes", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetIn6AddrLinkLocalAllNodes(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "2uSWyOKYc1M", ExportName = "sce_net_in6addr_linklocal_allrouters", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetIn6AddrLinkLocalAllRouters(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "P3AeWBvPrkg", ExportName = "sce_net_in6addr_loopback", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetIn6AddrLoopback(CpuContext ctx) => NetOk(ctx);

    [SysAbiExport(Nid = "PgNI+j4zxzM", ExportName = "sce_net_in6addr_nodelocal_allnodes", Target = Generation.Gen5, LibraryName = "libSceNet")]
    public static int NetIn6AddrNodeLocalAllNodes(CpuContext ctx) => NetOk(ctx);

    private static int NetOk(CpuContext ctx) => ctx.SetReturn(0);

    private static int CreateAuxiliaryHandle(CpuContext ctx, ConcurrentDictionary<int, byte> handles)
    {
        var handle = Interlocked.Increment(ref _nextAuxiliaryId);
        handles[handle] = 0;
        return ReturnInt32(ctx, handle);
    }

    private static int DestroyAuxiliaryHandle(CpuContext ctx, ConcurrentDictionary<int, byte> handles) =>
        handles.TryRemove(unchecked((int)ctx[CpuRegister.Rdi]), out _)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(NetErrorBadFileDescriptor);

    private static int ValidateAuxiliaryHandle(CpuContext ctx, ConcurrentDictionary<int, byte> handles) =>
        handles.ContainsKey(unchecked((int)ctx[CpuRegister.Rdi]))
            ? ctx.SetReturn(0)
            : ctx.SetReturn(NetErrorBadFileDescriptor);

    private static int ClearOptionalBuffer(CpuContext ctx, ulong address, int size) =>
        address == 0 ? ctx.SetReturn(0) : WriteOptionalBuffer(ctx, address, new byte[size]);

    private static int ClearOptionalPointer(CpuContext ctx, ulong address) =>
        address == 0 || ctx.TryWriteUInt64(address, 0)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(NetErrorMemoryFault);

    private static int WriteOptionalBuffer(CpuContext ctx, ulong address, ReadOnlySpan<byte> bytes) =>
        address == 0 || ctx.Memory.TryWrite(address, bytes)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(NetErrorMemoryFault);

    private static int WriteRequiredBuffer(CpuContext ctx, ulong address, ReadOnlySpan<byte> bytes)
    {
        if (address == 0)
        {
            return ctx.SetReturn(NetErrorInvalidArgument);
        }

        return ctx.Memory.TryWrite(address, bytes)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(NetErrorMemoryFault);
    }

    private static int WriteRequiredUInt32(CpuContext ctx, ulong address, uint value)
    {
        if (address == 0)
        {
            return ctx.SetReturn(NetErrorInvalidArgument);
        }

        return ctx.TryWriteUInt32(address, value)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(NetErrorMemoryFault);
    }

    private static int WriteRequiredInt32(CpuContext ctx, ulong address, int value)
    {
        if (address == 0)
        {
            return ctx.SetReturn(NetErrorInvalidArgument);
        }

        return ctx.TryWriteInt32(address, value)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(NetErrorMemoryFault);
    }

    private static int WriteIpv4Sockaddr(CpuContext ctx, ulong address, ReadOnlySpan<byte> ipv4)
    {
        if (address == 0)
        {
            return ctx.SetReturn(0);
        }

        Span<byte> socketAddress = stackalloc byte[16];
        socketAddress[0] = 16;
        socketAddress[1] = OrbisAddressFamilyInet;
        ipv4.CopyTo(socketAddress[4..8]);
        return ctx.Memory.TryWrite(address, socketAddress)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(NetErrorMemoryFault);
    }

    private static int ClearCountedResult(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rsi] != 0 && !ctx.TryWriteUInt32(ctx[CpuRegister.Rsi], 0))
        {
            return ctx.SetReturn(NetErrorMemoryFault);
        }

        return ClearOptionalBuffer(ctx, ctx[CpuRegister.Rdi], 32);
    }

    private static int GetSocketInfoCore(CpuContext ctx, int size)
    {
        if (!Sockets.ContainsKey(unchecked((int)ctx[CpuRegister.Rdi])))
        {
            return ctx.SetReturn(NetErrorBadFileDescriptor);
        }

        return ClearOptionalBuffer(ctx, ctx[CpuRegister.Rsi], size);
    }

    private static int WriteEmptyText(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdi] == 0)
        {
            return ctx.SetReturn(0);
        }

        return ctx.Memory.TryWrite(ctx[CpuRegister.Rdi], new byte[1])
            ? ctx.SetReturn(0)
            : ctx.SetReturn(NetErrorMemoryFault);
    }

    private static int ReturnUInt64(CpuContext ctx, ulong value)
    {
        ctx[CpuRegister.Rax] = value;
        return unchecked((int)value);
    }

    private static int ResolverAddressToName(CpuContext ctx, int addressLength)
    {
        if (!Resolvers.TryGetValue(unchecked((int)ctx[CpuRegister.Rdi]), out var resolver))
        {
            return ctx.SetReturn(NetErrorBadFileDescriptor);
        }

        var bytes = new byte[addressLength];
        var capacity = ctx[CpuRegister.Rcx];
        if (ctx[CpuRegister.Rsi] == 0 || ctx[CpuRegister.Rdx] == 0 || capacity == 0 ||
            !ctx.Memory.TryRead(ctx[CpuRegister.Rsi], bytes))
        {
            return ctx.SetReturn(NetErrorInvalidArgument);
        }

        var encoded = Encoding.ASCII.GetBytes(new IPAddress(bytes) + "\0");
        if ((ulong)encoded.Length > capacity)
        {
            resolver.LastError = unchecked((int)0x8041011C);
            return ctx.SetReturn(resolver.LastError);
        }

        if (!ctx.Memory.TryWrite(ctx[CpuRegister.Rdx], encoded))
        {
            return ctx.SetReturn(NetErrorMemoryFault);
        }

        resolver.LastError = 0;
        return ctx.SetReturn(0);
    }

    private static int ResolverNameToAddress(CpuContext ctx, AddressFamily family)
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

            var address = task.Result.FirstOrDefault(item => item.AddressFamily == family);
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

    private static int ResolverMultipleRecords(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdx];
        if (infoAddress == 0)
        {
            return ctx.SetReturn(NetErrorInvalidArgument);
        }

        var originalOutput = ctx[CpuRegister.Rdx];
        ctx[CpuRegister.Rdx] = originalOutput;
        var result = ResolverNameToAddress(ctx, AddressFamily.InterNetwork);
        if (result != 0)
        {
            return result;
        }

        if (!ctx.TryWriteUInt32(infoAddress + 16, OrbisAddressFamilyInet) ||
            !ctx.TryWriteUInt32(infoAddress + 20, 1) ||
            !ctx.TryWriteUInt32(infoAddress + 24, 1))
        {
            return ctx.SetReturn(NetErrorMemoryFault);
        }

        return ctx.SetReturn(0);
    }

    private static int MessageCore(CpuContext ctx, bool send)
    {
        return WithSocket(ctx, socket =>
        {
            var messageAddress = ctx[CpuRegister.Rsi];
            if (messageAddress == 0 || !ctx.TryReadUInt64(messageAddress + 16, out var vectorsAddress) ||
                !ctx.TryReadUInt32(messageAddress + 24, out var vectorCount) || vectorCount > 1024)
            {
                return NetErrorInvalidArgument;
            }

            var vectors = new (ulong Address, int Length)[vectorCount];
            var totalLength = 0;
            for (var index = 0; index < vectors.Length; index++)
            {
                var vectorAddress = vectorsAddress + unchecked((ulong)(index * 16));
                if (!ctx.TryReadUInt64(vectorAddress, out var dataAddress) ||
                    !ctx.TryReadUInt64(vectorAddress + 8, out var dataLength) ||
                    !TryGetTransferLength(dataLength, out var length) || totalLength > MaxTransferSize - length)
                {
                    return NetErrorInvalidArgument;
                }

                vectors[index] = (dataAddress, length);
                totalLength += length;
            }

            var buffer = new byte[totalLength];
            if (send)
            {
                var offset = 0;
                foreach (var vector in vectors)
                {
                    if (vector.Length != 0 && (vector.Address == 0 ||
                        !ctx.Memory.TryRead(vector.Address, buffer.AsSpan(offset, vector.Length))))
                    {
                        return NetErrorMemoryFault;
                    }

                    offset += vector.Length;
                }

                return socket.Socket.Send(buffer, MapMessageFlags(unchecked((int)ctx[CpuRegister.Rdx])));
            }

            if (!socket.Socket.Poll(GetWaitMicroseconds(socket, unchecked((int)ctx[CpuRegister.Rdx])), SelectMode.SelectRead))
            {
                return NetErrorWouldBlock;
            }

            var received = socket.Socket.Receive(buffer, MapMessageFlags(unchecked((int)ctx[CpuRegister.Rdx])));
            var sourceOffset = 0;
            foreach (var vector in vectors)
            {
                var copyLength = Math.Min(vector.Length, received - sourceOffset);
                if (copyLength <= 0)
                {
                    break;
                }

                if (vector.Address == 0 || !ctx.Memory.TryWrite(vector.Address, buffer.AsSpan(sourceOffset, copyLength)))
                {
                    return NetErrorMemoryFault;
                }

                sourceOffset += copyLength;
            }

            return received;
        });
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
