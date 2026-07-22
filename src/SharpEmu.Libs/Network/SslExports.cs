// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

public static class SslExports
{
    private const int SslErrorInvalidId = unchecked((int)0x8095F006);
    private const int SslErrorOutOfSize = unchecked((int)0x8095F008);

    private static readonly ConcurrentDictionary<int, SslContext> _contexts = new();
    private static readonly ConcurrentDictionary<int, ConnectionState> _connections = new();
    private static readonly ConcurrentDictionary<int, SslConnectionState> _sslConnections = new();
    private static readonly ConcurrentDictionary<int, CertificateState> _certificates = new();
    private static int _nextContextId;
    private static int _nextConnectionId;
    private static int _nextSslConnectionId;
    private static int _nextCertificateId;

    private sealed record SslContext(ulong PoolSize);

    private sealed class ConnectionState(int contextId, int socketId)
    {
        public int ContextId { get; } = contextId;
        public int SocketId { get; } = socketId;
        public ulong Options { get; set; }
    }

    private sealed class SslConnectionState(int connectionId)
    {
        public int ConnectionId { get; } = connectionId;
        public bool Connected { get; set; }
        public ulong Options { get; set; }
        public uint Version { get; set; }
        public uint MinimumVersion { get; set; }
    }

    private sealed record CertificateState(int ContextId);

    internal static int ConnectionHandleCount => _connections.Count;

    internal static int SslConnectionHandleCount => _sslConnections.Count;

    internal static int CertificateHandleCount => _certificates.Count;

    internal static void ResetForTests()
    {
        _contexts.Clear();
        _connections.Clear();
        _sslConnections.Clear();
        _certificates.Clear();
        _nextContextId = 0;
        _nextConnectionId = 0;
        _nextSslConnectionId = 0;
        _nextCertificateId = 0;
    }

    [SysAbiExport(
        Nid = "hdpVEUDFW3s",
        ExportName = "sceSslInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSsl")]
    public static int SslInit(CpuContext ctx)
    {
        var poolSize = ctx[CpuRegister.Rdi];
        if (poolSize == 0)
        {
            return ctx.SetReturn(SslErrorOutOfSize);
        }

        var id = Interlocked.Increment(ref _nextContextId);
        _contexts[id] = new SslContext(poolSize);

        TraceSsl("init", id, poolSize);
        ctx[CpuRegister.Rax] = unchecked((ulong)id);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "0K1yQ6Lv-Yc",
        ExportName = "sceSslTerm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSsl")]
    public static int SslTerm(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_contexts.TryRemove(id, out _))
        {
            return ctx.SetReturn(SslErrorInvalidId);
        }

        TraceSsl("term", id, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "viRXSHZYd0c",
        ExportName = "sceSslClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSsl")]
    public static int SslClose(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        TraceSsl("close", id, 0);
        return ctx.SetReturn(0);
    }

    private static int CreateConnection(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_contexts.ContainsKey(contextId))
        {
            return ctx.SetReturn(SslErrorInvalidId);
        }

        var id = Interlocked.Increment(ref _nextConnectionId);
        _connections[id] = new ConnectionState(contextId, unchecked((int)ctx[CpuRegister.Rsi]));
        return ctx.SetReturn(id);
    }

    private static int DeleteConnection(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_connections.TryRemove(id, out _))
        {
            return ctx.SetReturn(SslErrorInvalidId);
        }

        foreach (var pair in _sslConnections)
        {
            if (pair.Value.ConnectionId == id)
            {
                _sslConnections.TryRemove(pair.Key, out _);
            }
        }

        return ctx.SetReturn(0);
    }

    private static int CreateSslConnection(CpuContext ctx)
    {
        var connectionId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_connections.ContainsKey(connectionId))
        {
            return ctx.SetReturn(SslErrorInvalidId);
        }

        var id = Interlocked.Increment(ref _nextSslConnectionId);
        _sslConnections[id] = new SslConnectionState(connectionId);
        return ctx.SetReturn(id);
    }

    private static int DeleteSslConnection(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        return ctx.SetReturn(_sslConnections.TryRemove(id, out _) ? 0 : SslErrorInvalidId);
    }

    private static int ConnectSslConnection(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_sslConnections.TryGetValue(id, out var connection))
        {
            return ctx.SetReturn(SslErrorInvalidId);
        }

        connection.Connected = true;
        return ctx.SetReturn(0);
    }

    private static int SetConnectionOption(CpuContext ctx, bool enabled)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var option = ctx[CpuRegister.Rsi];
        if (_sslConnections.TryGetValue(id, out var sslConnection))
        {
            sslConnection.Options = enabled
                ? sslConnection.Options | option
                : sslConnection.Options & ~option;
            return ctx.SetReturn(0);
        }

        if (_connections.TryGetValue(id, out var connection))
        {
            connection.Options = enabled
                ? connection.Options | option
                : connection.Options & ~option;
            return ctx.SetReturn(0);
        }

        return ctx.SetReturn(SslErrorInvalidId);
    }

    private static int SetSslVersion(CpuContext ctx, bool minimum)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_sslConnections.TryGetValue(id, out var connection))
        {
            return ctx.SetReturn(SslErrorInvalidId);
        }

        if (minimum)
        {
            connection.MinimumVersion = unchecked((uint)ctx[CpuRegister.Rsi]);
        }
        else
        {
            connection.Version = unchecked((uint)ctx[CpuRegister.Rsi]);
        }

        return ctx.SetReturn(0);
    }

    private static int LoadCertificate(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_contexts.ContainsKey(contextId))
        {
            return ctx.SetReturn(SslErrorInvalidId);
        }

        var id = Interlocked.Increment(ref _nextCertificateId);
        _certificates[id] = new CertificateState(contextId);
        return ctx.SetReturn(id);
    }

    private static int UnloadCertificate(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        return ctx.SetReturn(_certificates.TryRemove(id, out _) ? 0 : SslErrorInvalidId);
    }

    private static int GetCaCerts(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_contexts.ContainsKey(contextId))
        {
            return ctx.SetReturn(SslErrorInvalidId);
        }

        var output = ctx[CpuRegister.Rsi];
        WriteUInt64IfPresent(ctx, output, 0);
        WriteUInt64IfPresent(ctx, output == 0 ? 0 : output + 8, 0);
        WriteUInt64IfPresent(ctx, output == 0 ? 0 : output + 16, 0);
        return ctx.SetReturn(0);
    }

    private static int GetCaList(CpuContext ctx)
    {
        var output = ctx[CpuRegister.Rsi];
        WriteUInt64IfPresent(ctx, output, 0);
        WriteUInt32IfPresent(ctx, output == 0 ? 0 : output + 8, 0);
        WriteUInt32IfPresent(ctx, output == 0 ? 0 : output + 12, 0);
        return ctx.SetReturn(0);
    }

    private static int GetMemoryPoolStats(CpuContext ctx)
    {
        var contextId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_contexts.TryGetValue(contextId, out var context))
        {
            return ctx.SetReturn(SslErrorInvalidId);
        }

        var output = ctx[CpuRegister.Rsi];
        WriteUInt64IfPresent(ctx, output, context.PoolSize);
        WriteUInt64IfPresent(ctx, output == 0 ? 0 : output + 8, 0);
        WriteUInt64IfPresent(ctx, output == 0 ? 0 : output + 16, 0);
        WriteUInt64IfPresent(ctx, output == 0 ? 0 : output + 24, 0);
        return ctx.SetReturn(0);
    }

    private static int GetSslError(CpuContext ctx)
    {
        WriteUInt32IfPresent(ctx, ctx[CpuRegister.Rsi], 0);
        WriteUInt32IfPresent(ctx, ctx[CpuRegister.Rdx], 0);
        return ctx.SetReturn(0);
    }

    private static int GetPendingByteCount(CpuContext ctx)
    {
        WriteUInt32IfPresent(ctx, ctx[CpuRegister.Rsi], 0);
        return ctx.SetReturn(0);
    }

    private static int GetPointerAndSize(CpuContext ctx)
    {
        WriteUInt64IfPresent(ctx, ctx[CpuRegister.Rsi], 0);
        WriteUInt64IfPresent(ctx, ctx[CpuRegister.Rdx], 0);
        return ctx.SetReturn(0);
    }

    private static int GetSinglePointer(CpuContext ctx)
    {
        WriteUInt64IfPresent(ctx, ctx[CpuRegister.Rsi], 0);
        return ctx.SetReturn(0);
    }

    private static int GetCertificateTime(CpuContext ctx)
    {
        var output = ctx[CpuRegister.Rsi];
        WriteUInt64IfPresent(ctx, output, 0);
        WriteUInt64IfPresent(ctx, output == 0 ? 0 : output + 8, 0);
        return ctx.SetReturn(0);
    }

    private static int GetNameEntryInfo(CpuContext ctx)
    {
        WriteUInt64IfPresent(ctx, ctx[CpuRegister.Rdx], 0);
        WriteUInt64IfPresent(ctx, ctx[CpuRegister.Rcx], 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(Nid = "Pgt0gg14ewU", ExportName = "CA_MGMT_allocCertDistinguishedName", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_allocCertDistinguishedName(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "wJ5jCpkCv-c", ExportName = "CA_MGMT_certDistinguishedNameCompare", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_certDistinguishedNameCompare(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "Vc2tb-mWu78", ExportName = "CA_MGMT_convertKeyBlobToPKCS8Key", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_convertKeyBlobToPKCS8Key(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "IizpdlgPdpU", ExportName = "CA_MGMT_convertKeyDER", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_convertKeyDER(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "Y-5sBnpVclY", ExportName = "CA_MGMT_convertKeyPEM", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_convertKeyPEM(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "jb6LuBv9weg", ExportName = "CA_MGMT_convertPKCS8KeyToKeyBlob", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_convertPKCS8KeyToKeyBlob(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "ExsvtKwhWoM", ExportName = "CA_MGMT_convertProtectedPKCS8KeyToKeyBlob", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_convertProtectedPKCS8KeyToKeyBlob(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "AvoadUUK03A", ExportName = "CA_MGMT_decodeCertificate", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_decodeCertificate(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "S0DCFBqmhQY", ExportName = "CA_MGMT_enumAltName", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_enumAltName(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "Xt+SprLPiVQ", ExportName = "CA_MGMT_enumCrl", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_enumCrl(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "4HzS6Vkd-uU", ExportName = "CA_MGMT_extractAllCertDistinguishedName", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_extractAllCertDistinguishedName(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "W80mmhRKtH8", ExportName = "CA_MGMT_extractBasicConstraint", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_extractBasicConstraint(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "7+F9pr5g26Q", ExportName = "CA_MGMT_extractCertASN1Name", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_extractCertASN1Name(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "KsvuhF--f6k", ExportName = "CA_MGMT_extractCertTimes", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_extractCertTimes(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "Md+HYkCBZB4", ExportName = "CA_MGMT_extractKeyBlobEx", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_extractKeyBlobEx(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "rFiChDgHkGQ", ExportName = "CA_MGMT_extractKeyBlobTypeEx", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_extractKeyBlobTypeEx(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "9bKYzKP6kYU", ExportName = "CA_MGMT_extractPublicKeyInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_extractPublicKeyInfo(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "xXCqbDBx6mA", ExportName = "CA_MGMT_extractSerialNum", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_extractSerialNum(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "xakUpzS9qv0", ExportName = "CA_MGMT_extractSignature", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_extractSignature(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "m7EXDQRv7NU", ExportName = "CA_MGMT_free", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_free(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "64t1HKepy1Q", ExportName = "CA_MGMT_freeCertDistinguishedName", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_freeCertDistinguishedName(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "d7AAqdK2IDo", ExportName = "CA_MGMT_freeCertDistinguishedNameOnStack", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_freeCertDistinguishedNameOnStack(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "PysF6pUcK-o", ExportName = "CA_MGMT_freeCertificate", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_freeCertificate(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "ipLIammTj2Q", ExportName = "CA_MGMT_freeKeyBlob", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_freeKeyBlob(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "C05CUtDViqU", ExportName = "CA_MGMT_freeSearchDetails", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_freeSearchDetails(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "tq511UiaNlE", ExportName = "CA_MGMT_getCertSignAlgoType", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_getCertSignAlgoType(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "1e46hRscIE8", ExportName = "CA_MGMT_keyBlobToDER", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_keyBlobToDER(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "5U2j47T1l70", ExportName = "CA_MGMT_keyBlobToPEM", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_keyBlobToPEM(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "+oCOy8+4at8", ExportName = "CA_MGMT_makeKeyBlobEx", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_makeKeyBlobEx(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "YMbRl6PNq5U", ExportName = "CA_MGMT_rawVerifyOID", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_rawVerifyOID(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "O+JTn8Dwan8", ExportName = "CA_MGMT_reorderChain", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_reorderChain(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "he6CvWiX3iM", ExportName = "CA_MGMT_returnCertificatePrints", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_returnCertificatePrints(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "w5ZBRGN1lzY", ExportName = "CA_MGMT_verifyCertWithKeyBlob", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_verifyCertWithKeyBlob(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "5e5rj-coUv8", ExportName = "CA_MGMT_verifySignature", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CA_MGMT_verifySignature(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "6nH53ruuckc", ExportName = "CERT_checkCertificateIssuer", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_checkCertificateIssuer(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "MB3EExhoaJQ", ExportName = "CERT_checkCertificateIssuer2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_checkCertificateIssuer2(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "sDUV9VsqJD8", ExportName = "CERT_checkCertificateIssuerSerialNumber", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_checkCertificateIssuerSerialNumber(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "FXCfp5CwcPk", ExportName = "CERT_CompSubjectAltNames", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_CompSubjectAltNames(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "szJ8gsZdoHE", ExportName = "CERT_CompSubjectAltNames2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_CompSubjectAltNames2(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "1aewkTBcGEY", ExportName = "CERT_CompSubjectAltNamesExact", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_CompSubjectAltNamesExact(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "gdWmmelQC1k", ExportName = "CERT_CompSubjectCommonName", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_CompSubjectCommonName(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "6Z-n6acrhTs", ExportName = "CERT_CompSubjectCommonName2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_CompSubjectCommonName2(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "p12OhhUCGEE", ExportName = "CERT_ComputeBufferHash", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_ComputeBufferHash(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "5G+Z9vXPWYU", ExportName = "CERT_decryptRSASignature", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_decryptRSASignature(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "WZCBPnvf0fw", ExportName = "CERT_decryptRSASignatureBuffer", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_decryptRSASignatureBuffer(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "AvjnXHAa7G0", ExportName = "CERT_enumerateAltName", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_enumerateAltName(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "goUd71Bv0lk", ExportName = "CERT_enumerateAltName2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_enumerateAltName2(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "tf3dP8kVauc", ExportName = "CERT_enumerateCRL", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_enumerateCRL(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "noRFMfbcI-g", ExportName = "CERT_enumerateCRL2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_enumerateCRL2(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "Xy4cdu44Xr0", ExportName = "CERT_enumerateCRLAux", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_enumerateCRLAux(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "2FPKT8OxHxo", ExportName = "CERT_extractAllDistinguishedNames", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_extractAllDistinguishedNames(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "xyd+kSAhtSw", ExportName = "CERT_extractDistinguishedNames", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_extractDistinguishedNames(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "BQIv6mcPFRM", ExportName = "CERT_extractDistinguishedNames2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_extractDistinguishedNames2(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "nxcdqUGDgW8", ExportName = "CERT_extractDistinguishedNamesFromName", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_extractDistinguishedNamesFromName(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "u82YRvIENeo", ExportName = "CERT_extractRSAKey", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_extractRSAKey(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "HBWarJFXoCM", ExportName = "CERT_extractSerialNum", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_extractSerialNum(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "8Lemumnt1-8", ExportName = "CERT_extractSerialNum2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_extractSerialNum2(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "JhanUiHOg-M", ExportName = "CERT_extractValidityTime", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_extractValidityTime(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "6ocfVwswH-E", ExportName = "CERT_extractValidityTime2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_extractValidityTime2(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "8FqgR3V7gHs", ExportName = "CERT_getCertExtension", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_getCertExtension(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "sRIARmcXPHE", ExportName = "CERT_getCertificateExtensions", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_getCertificateExtensions(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "ABAA2f3PM8k", ExportName = "CERT_getCertificateExtensions2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_getCertificateExtensions2(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "CATkBsr20tY", ExportName = "CERT_getCertificateIssuerSerialNumber", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_getCertificateIssuerSerialNumber(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "JpnKObUJsxQ", ExportName = "CERT_getCertificateIssuerSerialNumber2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_getCertificateIssuerSerialNumber2(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "jp75ki1UzRU", ExportName = "CERT_getCertificateKeyUsage", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_getCertificateKeyUsage(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "prSVrFdvQiU", ExportName = "CERT_getCertificateKeyUsage2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_getCertificateKeyUsage2(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "8+UPqcEgsYg", ExportName = "CERT_getCertificateSubject", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_getCertificateSubject(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "X-rqVhPnKJI", ExportName = "CERT_getCertificateSubject2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_getCertificateSubject2(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "Pt3o1t+hh1g", ExportName = "CERT_getCertSignAlgoType", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_getCertSignAlgoType(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "oNJNApmHV+M", ExportName = "CERT_GetCertTime", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_GetCertTime(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "GCPUCV9k1Mg", ExportName = "CERT_getNumberOfChild", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_getNumberOfChild(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "lCB1AE4xSkE", ExportName = "CERT_getRSASignatureAlgo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_getRSASignatureAlgo(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "+7U74Zy7gKg", ExportName = "CERT_getSignatureItem", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_getSignatureItem(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "hOABTkhp6NM", ExportName = "CERT_getSubjectCommonName", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_getSubjectCommonName(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "3CECWZfBTVg", ExportName = "CERT_getSubjectCommonName2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_getSubjectCommonName2(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "OP-VhFdtkmo", ExportName = "CERT_isRootCertificate", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_isRootCertificate(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "0iwGE4M4DU8", ExportName = "CERT_isRootCertificate2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_isRootCertificate2(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "pWg3+mTkoTI", ExportName = "CERT_rawVerifyOID", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_rawVerifyOID(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "HofoEUZ5mOM", ExportName = "CERT_rawVerifyOID2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_rawVerifyOID2(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "w2lGr-89zLc", ExportName = "CERT_setKeyFromSubjectPublicKeyInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_setKeyFromSubjectPublicKeyInfo(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "OeGeb9Njons", ExportName = "CERT_setKeyFromSubjectPublicKeyInfoCert", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_setKeyFromSubjectPublicKeyInfoCert(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "N+UDju8zxtE", ExportName = "CERT_STORE_addCertAuthority", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_STORE_addCertAuthority(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "pIZfvPaYmrs", ExportName = "CERT_STORE_addIdentity", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_STORE_addIdentity(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "D6QBgLq-nlc", ExportName = "CERT_STORE_addIdentityNakedKey", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_STORE_addIdentityNakedKey(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "uAHc6pgeFaQ", ExportName = "CERT_STORE_addIdentityPSK", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_STORE_addIdentityPSK(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "xdxuhUkYalI", ExportName = "CERT_STORE_addIdentityWithCertificateChain", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_STORE_addIdentityWithCertificateChain(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "OcZJcxANLfw", ExportName = "CERT_STORE_addTrustPoint", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_STORE_addTrustPoint(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "gu0eRZMqTu8", ExportName = "CERT_STORE_createStore", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_STORE_createStore(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "s1tJ1zBkky4", ExportName = "CERT_STORE_findCertBySubject", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_STORE_findCertBySubject(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "4aXDehFZLDA", ExportName = "CERT_STORE_findIdentityByTypeFirst", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_STORE_findIdentityByTypeFirst(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "K-g87UhrYQ8", ExportName = "CERT_STORE_findIdentityByTypeNext", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_STORE_findIdentityByTypeNext(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "ULOVCAVPJE4", ExportName = "CERT_STORE_findIdentityCertChainFirst", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_STORE_findIdentityCertChainFirst(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "uS9P+bSWOC0", ExportName = "CERT_STORE_findIdentityCertChainNext", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_STORE_findIdentityCertChainNext(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "k3RI-YRkW-M", ExportName = "CERT_STORE_findPskByIdentity", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_STORE_findPskByIdentity(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "AloU5nLupdU", ExportName = "CERT_STORE_releaseStore", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_STORE_releaseStore(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "gAHkf68L6+0", ExportName = "CERT_STORE_traversePskListHead", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_STORE_traversePskListHead(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "w2CtqF+x7og", ExportName = "CERT_STORE_traversePskListNext", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_STORE_traversePskListNext(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "GTSbNvpE1fQ", ExportName = "CERT_validateCertificate", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_validateCertificate(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "j6Wk8AtmVQM", ExportName = "CERT_validateCertificateWithConf", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_validateCertificateWithConf(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "wdl-XapuxKU", ExportName = "CERT_VerifyCertificatePolicies", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_VerifyCertificatePolicies(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "BQah1z-QS-w", ExportName = "CERT_VerifyCertificatePolicies2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_VerifyCertificatePolicies2(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "GPRMLcwyslw", ExportName = "CERT_verifySignature", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_verifySignature(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "CAgB8oEGwsY", ExportName = "CERT_VerifyValidityTime", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_VerifyValidityTime(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "3wferxuMV6Y", ExportName = "CERT_VerifyValidityTime2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_VerifyValidityTime2(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "UO2a3+5CCCs", ExportName = "CERT_VerifyValidityTimeWithConf", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CERT_VerifyValidityTimeWithConf(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "PRWr3-ytpdg", ExportName = "CRYPTO_initAsymmetricKey", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CRYPTO_initAsymmetricKey(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "cW7VCIMCh9A", ExportName = "CRYPTO_uninitAsymmetricKey", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int CRYPTO_uninitAsymmetricKey(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "u+brAYVFGUs", ExportName = "GC_createInstanceIDs", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int GC_createInstanceIDs(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "pOmcRglskbI", ExportName = "getCertSigAlgo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int getCertSigAlgo(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "uBqy-2-dQ-A", ExportName = "MOCANA_freeMocana", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int MOCANA_freeMocana(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "U3NHH12yORo", ExportName = "MOCANA_initMocana", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int MOCANA_initMocana(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "pBwtarKd7eg", ExportName = "RSA_verifySignature", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int RSA_verifySignature(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "1VM0h1JrUfA", ExportName = "sceSslCheckRecvPending", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslCheckRecvPending(CpuContext ctx) => GetPendingByteCount(ctx);

    [SysAbiExport(Nid = "zXvd6iNyfgc", ExportName = "sceSslConnect", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslConnect(CpuContext ctx) => ConnectSslConnection(ctx);

    [SysAbiExport(Nid = "P14ATpXc4J8", ExportName = "sceSslCreateSslConnection", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslCreateSslConnection(CpuContext ctx) => CreateSslConnection(ctx);

    [SysAbiExport(Nid = "hwrHV6Pprk4", ExportName = "sceSslDeleteSslConnection", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslDeleteSslConnection(CpuContext ctx) => DeleteSslConnection(ctx);

    [SysAbiExport(Nid = "iLKz4+ukLqk", ExportName = "sceSslDisableOption", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslDisableOption(CpuContext ctx) => SetConnectionOption(ctx, false);

    [SysAbiExport(Nid = "-WqxBRAUVM4", ExportName = "sceSslDisableOptionInternal", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslDisableOptionInternal(CpuContext ctx) => SetConnectionOption(ctx, false);

    [SysAbiExport(Nid = "w1+L-27nYas", ExportName = "sceSslDisableOptionInternalInsecure", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslDisableOptionInternalInsecure(CpuContext ctx) => SetConnectionOption(ctx, false);

    [SysAbiExport(Nid = "m-zPyAsIpco", ExportName = "sceSslEnableOption", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslEnableOption(CpuContext ctx) => SetConnectionOption(ctx, true);

    [SysAbiExport(Nid = "g-zCwUKstEQ", ExportName = "sceSslEnableOptionInternal", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslEnableOptionInternal(CpuContext ctx) => SetConnectionOption(ctx, true);

    [SysAbiExport(Nid = "qIvLs0gYxi0", ExportName = "sceSslFreeCaCerts", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslFreeCaCerts(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "+DzXseDVkeI", ExportName = "sceSslFreeCaList", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslFreeCaList(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "RwXD8grHZHM", ExportName = "sceSslFreeSslCertName", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslFreeSslCertName(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "TDfQqO-gMbY", ExportName = "sceSslGetCaCerts", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslGetCaCerts(CpuContext ctx) => GetCaCerts(ctx);

    [SysAbiExport(Nid = "qOn+wm28wmA", ExportName = "sceSslGetCaList", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslGetCaList(CpuContext ctx) => GetCaList(ctx);

    [SysAbiExport(Nid = "7whYpYfHP74", ExportName = "sceSslGetIssuerName", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslGetIssuerName(CpuContext ctx) => GetSinglePointer(ctx);

    [SysAbiExport(Nid = "-PoIzr3PEk0", ExportName = "sceSslGetMemoryPoolStats", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslGetMemoryPoolStats(CpuContext ctx) => GetMemoryPoolStats(ctx);

    [SysAbiExport(Nid = "R1ePzopYPYM", ExportName = "sceSslGetNameEntryCount", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslGetNameEntryCount(CpuContext ctx) => GetPendingByteCount(ctx);

    [SysAbiExport(Nid = "7RBSTKGrmDA", ExportName = "sceSslGetNameEntryInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslGetNameEntryInfo(CpuContext ctx) => GetNameEntryInfo(ctx);

    [SysAbiExport(Nid = "AzUipl-DpIw", ExportName = "sceSslGetNanoSSLModuleId", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslGetNanoSSLModuleId(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "xHpt6+2pGYk", ExportName = "sceSslGetNotAfter", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslGetNotAfter(CpuContext ctx) => GetCertificateTime(ctx);

    [SysAbiExport(Nid = "Eo0S65Jy28Q", ExportName = "sceSslGetNotBefore", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslGetNotBefore(CpuContext ctx) => GetCertificateTime(ctx);

    [SysAbiExport(Nid = "DOwXL+FQMEY", ExportName = "sceSslGetSerialNumber", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslGetSerialNumber(CpuContext ctx) => GetPointerAndSize(ctx);

    [SysAbiExport(Nid = "0XcZknp7-Wc", ExportName = "sceSslGetSslError", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslGetSslError(CpuContext ctx) => GetSslError(ctx);

    [SysAbiExport(Nid = "dQReuBX9sD8", ExportName = "sceSslGetSubjectName", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslGetSubjectName(CpuContext ctx) => GetSinglePointer(ctx);

    [SysAbiExport(Nid = "Ab7+DH+gYyM", ExportName = "sceSslLoadCert", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslLoadCert(CpuContext ctx) => LoadCertificate(ctx);

    [SysAbiExport(Nid = "3-643mGVFJo", ExportName = "sceSslLoadRootCACert", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslLoadRootCACert(CpuContext ctx) => LoadCertificate(ctx);

    [SysAbiExport(Nid = "hi0veU3L2pU", ExportName = "sceSslRecv", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslRecv(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "50R2xYaYZwE", ExportName = "sceSslReuseConnection", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslReuseConnection(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "p5bM5PPufFY", ExportName = "sceSslSend", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslSend(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "QWSxBzf6lAg", ExportName = "sceSslSetMinSslVersion", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslSetMinSslVersion(CpuContext ctx) => SetSslVersion(ctx, true);

    [SysAbiExport(Nid = "bKaEtQnoUuQ", ExportName = "sceSslSetSslVersion", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslSetSslVersion(CpuContext ctx) => SetSslVersion(ctx, false);

    [SysAbiExport(Nid = "E4a-ahM57QQ", ExportName = "sceSslSetVerifyCallback", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslSetVerifyCallback(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "lnHFrZV5zAY", ExportName = "sceSslShowMemoryStat", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslShowMemoryStat(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "UQ+3Qu7v3cA", ExportName = "sceSslUnloadCert", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslUnloadCert(CpuContext ctx) => UnloadCertificate(ctx);

    [SysAbiExport(Nid = "26lYor6xrR4", ExportName = "SSL_acceptConnection", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_acceptConnection(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "iHBiYOSciqY", ExportName = "SSL_acceptConnectionCommon", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_acceptConnectionCommon(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "budJurAYNHc", ExportName = "SSL_assignCertificateStore", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_assignCertificateStore(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "dCRcdgdoIEI", ExportName = "SSL_ASYNC_acceptConnection", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_ASYNC_acceptConnection(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "KI5jhdvg2S8", ExportName = "SSL_ASYNC_closeConnection", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_ASYNC_closeConnection(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "hk+NcQTQlqI", ExportName = "SSL_ASYNC_connect", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_ASYNC_connect(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "rKD5kXcvN0E", ExportName = "SSL_ASYNC_connectCommon", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_ASYNC_connectCommon(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "Fxq5MuWRkSw", ExportName = "SSL_ASYNC_getRecvBuffer", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_ASYNC_getRecvBuffer(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "vCpt1jyL6C4", ExportName = "SSL_ASYNC_getSendBuffer", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_ASYNC_getSendBuffer(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "wZp1hBtjV1I", ExportName = "SSL_ASYNC_init", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_ASYNC_init(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "P+O-4XCIODs", ExportName = "SSL_ASYNC_initServer", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_ASYNC_initServer(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "GfDzwBDRl3M", ExportName = "SSL_ASYNC_recvMessage", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_ASYNC_recvMessage(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "oM5w6Fb4TWM", ExportName = "SSL_ASYNC_recvMessage2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_ASYNC_recvMessage2(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "dim5NDlc7Vs", ExportName = "SSL_ASYNC_sendMessage", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_ASYNC_sendMessage(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "Qq0o-+hobOI", ExportName = "SSL_ASYNC_sendMessagePending", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_ASYNC_sendMessagePending(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "y+ZFCsZYNME", ExportName = "SSL_ASYNC_start", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_ASYNC_start(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "5g9cNS3IFCk", ExportName = "SSL_closeConnection", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_closeConnection(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "i9AvJK-l5Jk", ExportName = "SSL_connect", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_connect(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "mgs+n71u35Y", ExportName = "SSL_connectWithCfgParam", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_connectWithCfgParam(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "4hPwsDmVKZc", ExportName = "SSL_enableCiphers", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_enableCiphers(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "yUd2ukhZLJI", ExportName = "SSL_findConnectionInstance", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_findConnectionInstance(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "J7LWSdYo0Zg", ExportName = "SSL_getCipherInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_getCipherInfo(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "kRb0lquIrj0", ExportName = "SSL_getClientRandom", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_getClientRandom(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "sSD8SHia8Zc", ExportName = "SSL_getClientSessionInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_getClientSessionInfo(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "eT7n5lcEYCc", ExportName = "SSL_getCookie", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_getCookie(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "2Irwf6Oqt4E", ExportName = "SSL_getNextSessionId", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_getNextSessionId(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "s9qIeprVILk", ExportName = "SSL_getServerRandom", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_getServerRandom(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "NRoSvM1VPm8", ExportName = "SSL_getSessionCache", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_getSessionCache(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "dHosoPLXaMw", ExportName = "SSL_getSessionFlags", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_getSessionFlags(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "7QgvTqUGFlU", ExportName = "SSL_getSessionInfo", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_getSessionInfo(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "ufoBDuHGOlM", ExportName = "SSL_getSessionStatus", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_getSessionStatus(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "EAoybreRrGU", ExportName = "SSL_getSocketId", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_getSocketId(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "ElUzZAXIvY0", ExportName = "SSL_getSSLTLSVersion", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_getSSLTLSVersion(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "Wi9eDU54UCU", ExportName = "SSL_init", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_init(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "BSqmh5B4KTg", ExportName = "SSL_initiateRehandshake", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_initiateRehandshake(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "xIFe7m4wqX4", ExportName = "SSL_initServerCert", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_initServerCert(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "zlMZOG3VDYg", ExportName = "SSL_ioctl", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_ioctl(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "fje5RYUa+2g", ExportName = "SSL_isSessionSSL", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_isSessionSSL(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "IKENWUUd8bk", ExportName = "SSL_lockSessionCacheMutex", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_lockSessionCacheMutex(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "n6-12LafAeA", ExportName = "SSL_lookupAlert", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_lookupAlert(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "H4Z3ShBNjSA", ExportName = "SSL_negotiateConnection", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_negotiateConnection(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "9PTAJclcW50", ExportName = "SSL_recv", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_recv(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "NrZz0ZgQrao", ExportName = "SSL_recvPending", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_recvPending(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "SHInb+l58Bs", ExportName = "SSL_releaseTables", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_releaseTables(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "f0MBRCQeOEg", ExportName = "SSL_retrieveServerNameList", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_retrieveServerNameList(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "6J0PLGaYl0Y", ExportName = "SSL_rngFun", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_rngFun(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "MoaZ6-hDS-k", ExportName = "SSL_send", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_send(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "H02lfd0hCG0", ExportName = "SSL_sendAlert", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_sendAlert(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "nXlhepw9ztI", ExportName = "SSL_sendPending", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_sendPending(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "Bf0pzkQc6CU", ExportName = "SSL_setCookie", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_setCookie(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "dSP1n53RtVw", ExportName = "SSL_setServerCert", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_setServerCert(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "kNIvrkD-XJk", ExportName = "SSL_setServerNameList", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_setServerNameList(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "pbTq-nEsN1w", ExportName = "SSL_setSessionFlags", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_setSessionFlags(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "-UDxVMs9h9M", ExportName = "SSL_shutdown", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_shutdown(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "nH9FVvfZhCs", ExportName = "SSL_sslSettings", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_sslSettings(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "2Bd7UoCRhQ8", ExportName = "SSL_validateCertParam", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int SSL_validateCertParam(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "wcVuyTUr5ys", ExportName = "VLONG_freeVlongQueue", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int VLONG_freeVlongQueue(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "tuscfitnhEo", ExportName = "sceSslCreateConnection", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslCreateConnection(CpuContext ctx) => CreateConnection(ctx);

    [SysAbiExport(Nid = "HJ1n138CQ2g", ExportName = "sceSslDeleteConnection", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslDeleteConnection(CpuContext ctx) => DeleteConnection(ctx);

    [SysAbiExport(Nid = "PwsHbErG+e8", ExportName = "sceSslDisableVerifyOption", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslDisableVerifyOption(CpuContext ctx) => SetConnectionOption(ctx, false);

    [SysAbiExport(Nid = "po1X86mgHDU", ExportName = "sceSslEnableVerifyOption", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslEnableVerifyOption(CpuContext ctx) => SetConnectionOption(ctx, true);

    [SysAbiExport(Nid = "4O7+bRkRUe8", ExportName = "sceSslGetAlpnSelected", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslGetAlpnSelected(CpuContext ctx) => GetPointerAndSize(ctx);

    [SysAbiExport(Nid = "brRtwGBu4A8", ExportName = "sceSslGetFingerprint", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslGetFingerprint(CpuContext ctx) => GetPointerAndSize(ctx);

    [SysAbiExport(Nid = "-TbZc8pwPNc", ExportName = "sceSslGetPeerCert", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslGetPeerCert(CpuContext ctx) => GetSinglePointer(ctx);

    [SysAbiExport(Nid = "kLB5aGoUJXg", ExportName = "sceSslGetPem", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslGetPem(CpuContext ctx) => GetPointerAndSize(ctx);

    [SysAbiExport(Nid = "jltWpVKtetg", ExportName = "sceSslRead", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslRead(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "TL86glUrmUw", ExportName = "sceSslSetAlpn", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslSetAlpn(CpuContext ctx) => Stub(ctx);

    [SysAbiExport(Nid = "iNjkt9Poblw", ExportName = "sceSslWrite", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceSsl")]
    public static int sceSslWrite(CpuContext ctx) => Stub(ctx);


    private static int Stub(CpuContext ctx) => ctx.SetReturn(0);

    private static void WriteUInt32IfPresent(CpuContext ctx, ulong address, uint value)
    {
        if (address != 0)
        {
            ctx.TryWriteUInt32(address, value);
        }
    }

    private static void WriteUInt64IfPresent(CpuContext ctx, ulong address, ulong value)
    {
        if (address != 0)
        {
            ctx.TryWriteUInt64(address, value);
        }
    }

    private static void TraceSsl(string operation, int id, ulong arg0)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_SSL"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] ssl.{operation} id={id} arg0=0x{arg0:X16}");
    }
}
