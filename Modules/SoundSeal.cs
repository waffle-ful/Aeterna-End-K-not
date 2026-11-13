using System;
using System.IO;

namespace EndKnot.Modules;

// 同梱 BGM の EndKnot.Music.dll (MusicPack) の曲は、パックのビルドの時に ChaCha20 で暗号化して収めてある
// (パック側の tools/SoundSealTask.cs)。音源サイトの規約は、利用者が音のファイルをそのまま取り出せる形で
// 配ることを禁じているため。リソース名は暗号化の前と同じで、MusicPack.Open がここを通して開く。
// ChaCha20 は 64 バイトごとの番号で鍵の流れが決まるので、どの位置からでも解ける。
// 中身を丸ごと写さずにシークもできるストリームとして返す。
internal static class SoundSeal
{
    private const string Prefix = "EndKnot.Resources.Sounds.";

    // パック側の tools/SoundSealTask.cs の Key と同じ値にしておくこと
    private static readonly uint[] Key =
    [
        0x6b6e4b45u, 0x6f532d74u, 0x5364756eu, 0x316c6165u,
        0x9e3779b9u, 0x7f4a7c15u, 0xf39cc060u, 0x5cedc834u
    ];

    internal static bool IsSealed(string resourceName)
        => resourceName != null
           && resourceName.StartsWith(Prefix, StringComparison.Ordinal)
           && (resourceName.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
               || resourceName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
               || resourceName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));

    // パックから開いたリソースのストリームを、暗号化した音なら解きながら読むストリームで包む
    internal static Stream Wrap(Stream raw, string resourceName)
    {
        if (raw == null || !IsSealed(resourceName)) return raw;
        return new SealedStream(raw, NonceOf(resourceName));
    }

    // リソース名から 64 ビットの nonce を作る (FNV-1a)。ビルド側と同じ計算
    internal static ulong NonceOf(string resourceName)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in resourceName)
        {
            h ^= c;
            h *= 1099511628211UL;
        }
        return h;
    }

    // ChaCha20 の 1 ブロック (64 バイト) の鍵の流れを作る
    internal static void Block(ulong nonce, ulong counter, byte[] output)
    {
        Span<uint> s = stackalloc uint[16];
        s[0] = 0x61707865u; s[1] = 0x3320646eu; s[2] = 0x79622d32u; s[3] = 0x6b206574u;
        for (int i = 0; i < 8; i++) s[4 + i] = Key[i];
        s[12] = (uint)counter; s[13] = (uint)(counter >> 32);
        s[14] = (uint)nonce; s[15] = (uint)(nonce >> 32);

        Span<uint> x = stackalloc uint[16];
        s.CopyTo(x);
        for (int i = 0; i < 10; i++)
        {
            Quarter(x, 0, 4, 8, 12); Quarter(x, 1, 5, 9, 13); Quarter(x, 2, 6, 10, 14); Quarter(x, 3, 7, 11, 15);
            Quarter(x, 0, 5, 10, 15); Quarter(x, 1, 6, 11, 12); Quarter(x, 2, 7, 8, 13); Quarter(x, 3, 4, 9, 14);
        }
        for (int i = 0; i < 16; i++)
        {
            uint v = x[i] + s[i];
            output[i * 4] = (byte)v;
            output[i * 4 + 1] = (byte)(v >> 8);
            output[i * 4 + 2] = (byte)(v >> 16);
            output[i * 4 + 3] = (byte)(v >> 24);
        }
    }

    private static void Quarter(Span<uint> x, int a, int b, int c, int d)
    {
        x[a] += x[b]; x[d] = Rotl(x[d] ^ x[a], 16);
        x[c] += x[d]; x[b] = Rotl(x[b] ^ x[c], 12);
        x[a] += x[b]; x[d] = Rotl(x[d] ^ x[a], 8);
        x[c] += x[d]; x[b] = Rotl(x[b] ^ x[c], 7);
    }

    private static uint Rotl(uint v, int n) => (v << n) | (v >> (32 - n));

    // 暗号化した埋め込みリソースを、解きながら読む。読み取り専用で、元のストリームがシークできればシークもできる
    private sealed class SealedStream : Stream
    {
        private readonly Stream _inner;
        private readonly ulong _nonce;
        private readonly byte[] _ks = new byte[64];
        private ulong _ksBlock = ulong.MaxValue;

        internal SealedStream(Stream inner, ulong nonce)
        {
            _inner = inner;
            _nonce = nonce;
        }

        public override bool CanRead => true;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long pos = _inner.Position;
            int n = _inner.Read(buffer);
            for (int i = 0; i < n; i++)
            {
                long p = pos + i;
                ulong block = (ulong)p >> 6;
                if (block != _ksBlock)
                {
                    Block(_nonce, block, _ks);
                    _ksBlock = block;
                }
                buffer[i] ^= _ks[p & 63];
            }
            return n;
        }

        public override int ReadByte()
        {
            Span<byte> one = stackalloc byte[1];
            return Read(one) == 1 ? one[0] : -1;
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
