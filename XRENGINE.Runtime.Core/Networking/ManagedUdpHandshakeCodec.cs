using System.Buffers.Binary;
using System.Text;

namespace XREngine.Networking;

/// <summary>Bounded binary handshake payload codec; it deliberately excludes bearer secrets and session tokens.</summary>
public static class ManagedUdpHandshakeCodec
{
    public const int MaxPayloadBytes = 4096;

    public static byte[] WriteHello(PlayerJoinRequest request, ReadOnlySpan<byte> nonce)
    {
        if (nonce.Length != 32) throw new ArgumentOutOfRangeException(nameof(nonce));
        using var stream = new MemoryStream();
        stream.WriteByte(request.ResumeRequested ? (byte)1 : (byte)0);
        WriteGuid(stream, request.SessionId ?? Guid.Empty); WriteGuid(stream, request.WorkerGeneration ?? Guid.Empty);
        WriteInt64(stream, request.CredentialEpoch);
        WriteString(stream, request.ReservationId, 128); WriteString(stream, request.ClientId, 128); WriteString(stream, request.AccountId, 256);
        WriteString(stream, request.DisplayName, 256); WriteString(stream, request.BuildVersion, 128); WriteString(stream, request.WorldName, 256);
        WriteString(stream, request.ClientWorldAsset?.WorldId, 256); WriteString(stream, request.ClientWorldAsset?.RevisionId, 256); WriteString(stream, request.ClientWorldAsset?.ContentHash, 256);
        stream.Write(nonce);
        return Finish(stream);
    }

    public static bool TryReadHello(ReadOnlySpan<byte> bytes, out PlayerJoinRequest request, out byte[] nonce)
    {
        request = new(); nonce = [];
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable:false);
            bool resume = ReadByte(stream) != 0; Guid session=ReadGuid(stream), generation=ReadGuid(stream); long epoch=ReadInt64(stream);
            string reservation=ReadString(stream,128), client=ReadString(stream,128), account=ReadString(stream,256);
            string display=ReadString(stream,256), build=ReadString(stream,128), world=ReadString(stream,256);
            string id=ReadString(stream,256), revision=ReadString(stream,256), hash=ReadString(stream,256);
            nonce=ReadExact(stream,32); if(stream.Position!=stream.Length || session==Guid.Empty || generation==Guid.Empty || epoch<0 || string.IsNullOrEmpty(reservation)||string.IsNullOrEmpty(client)||string.IsNullOrEmpty(account)) return false;
            request=new PlayerJoinRequest { ResumeRequested=resume, SessionId=session, WorkerGeneration=generation, CredentialEpoch=epoch, ReservationId=reservation, ClientId=client, AccountId=account, DisplayName=display, BuildVersion=build, WorldName=world, ClientWorldAsset=new WorldAssetIdentity { WorldId=id, RevisionId=revision, ContentHash=hash } };
            return true;
        } catch { return false; }
    }
    public static byte[] WriteChallenge(ReadOnlySpan<byte> clientNonce, ReadOnlySpan<byte> serverNonce, Guid associationId, ReadOnlySpan<byte> helloHash, long expiryUnixSeconds, ReadOnlySpan<byte> cookie)
    { if(clientNonce.Length!=32||serverNonce.Length!=32||helloHash.Length!=32||cookie.Length>128)throw new ArgumentOutOfRangeException(); using var s=new MemoryStream();s.Write(clientNonce);s.Write(serverNonce);WriteGuid(s,associationId);s.Write(helloHash);WriteInt64(s,expiryUnixSeconds);WriteString(s,Convert.ToBase64String(cookie),256);return Finish(s); }
    public static bool TryReadChallenge(ReadOnlySpan<byte> bytes,out byte[] clientNonce,out byte[] serverNonce,out Guid associationId,out byte[] helloHash,out long expiry,out byte[] cookie)
    { clientNonce=[];serverNonce=[];associationId=Guid.Empty;helloHash=[];expiry=0;cookie=[];try{using var s=new MemoryStream(bytes.ToArray(),false);clientNonce=ReadExact(s,32);serverNonce=ReadExact(s,32);associationId=ReadGuid(s);helloHash=ReadExact(s,32);expiry=ReadInt64(s);cookie=Convert.FromBase64String(ReadString(s,256));return s.Position==s.Length&&cookie.Length<=128;}catch{return false;} }
    public static byte[] WriteCommit(ReadOnlySpan<byte> hello,ReadOnlySpan<byte> serverNonce,Guid associationId,long expiry,ReadOnlySpan<byte> cookie)
    { if(hello.Length>MaxPayloadBytes-200||serverNonce.Length!=32||cookie.Length>128)throw new ArgumentOutOfRangeException();using var s=new MemoryStream();Span<byte>l=stackalloc byte[2];BinaryPrimitives.WriteUInt16BigEndian(l,(ushort)hello.Length);s.Write(l);s.Write(hello);s.Write(serverNonce);WriteGuid(s,associationId);WriteInt64(s,expiry);WriteString(s,Convert.ToBase64String(cookie),256);return Finish(s); }
    public static bool TryReadCommit(ReadOnlySpan<byte> bytes,out byte[] hello,out byte[] serverNonce,out Guid associationId,out long expiry,out byte[] cookie)
    {hello=[];serverNonce=[];associationId=Guid.Empty;expiry=0;cookie=[];try{using var s=new MemoryStream(bytes.ToArray(),false);int n=BinaryPrimitives.ReadUInt16BigEndian(ReadExact(s,2));if(n>MaxPayloadBytes) return false;hello=ReadExact(s,n);serverNonce=ReadExact(s,32);associationId=ReadGuid(s);expiry=ReadInt64(s);cookie=Convert.FromBase64String(ReadString(s,256));return s.Position==s.Length&&cookie.Length<=128;}catch{return false;}}
    private static byte[] Finish(MemoryStream stream) { if(stream.Length>MaxPayloadBytes) throw new InvalidOperationException("Handshake exceeds bound."); return stream.ToArray(); }
    private static void WriteGuid(Stream s,Guid v)=>s.Write(v.ToByteArray()); private static Guid ReadGuid(Stream s)=>new(ReadExact(s,16));
    private static void WriteInt64(Stream s,long v){Span<byte>b=stackalloc byte[8];BinaryPrimitives.WriteInt64BigEndian(b,v);s.Write(b);} private static long ReadInt64(Stream s)=>BinaryPrimitives.ReadInt64BigEndian(ReadExact(s,8));
    private static void WriteString(Stream s,string? v,int max){byte[] b=Encoding.UTF8.GetBytes(v??string.Empty);if(b.Length>max)throw new ArgumentOutOfRangeException(nameof(v));Span<byte>l=stackalloc byte[2];BinaryPrimitives.WriteUInt16BigEndian(l,(ushort)b.Length);s.Write(l);s.Write(b);} private static string ReadString(Stream s,int max){int n=BinaryPrimitives.ReadUInt16BigEndian(ReadExact(s,2));if(n>max)throw new InvalidDataException();return Encoding.UTF8.GetString(ReadExact(s,n));}
    private static byte ReadByte(Stream s){int v=s.ReadByte();if(v<0)throw new EndOfStreamException();return(byte)v;} private static byte[] ReadExact(Stream s,int n){byte[] b=new byte[n];if(s.Read(b,0,n)!=n)throw new EndOfStreamException();return b;}
}
