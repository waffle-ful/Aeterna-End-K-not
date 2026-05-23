using System.Text;
using Hazel;
using UnityEngine;

namespace EndKnot;

public static class HazelExtensions
{
    // Hazel が PackedUInt32 として書き出すバイト長を 1 ずつ実機算出
    public static int GetPackedUIntSize(uint value)
    {
        int count = 0;

        do
        {
            value >>= 7;
            count++;
        }
        while (value != 0);

        return count;
    }

    // string を Hazel.Write(string) で書き出した時の実バイト数 (UTF-8 + PackedUInt32 prefix)
    public static int GetStringWriteSize(string text)
    {
        if (text == null) return GetPackedUIntSize(0);
        int byteCount = Encoding.UTF8.GetByteCount(text);
        return GetPackedUIntSize((uint)byteCount) + byteCount;
    }

    // -------------------------------------------------------------------------------------------------------------------------

    extension(MessageWriter writer)
    {
        public void Write(Vector2 vector)
        {
            NetHelpers.WriteVector2(vector, writer);
        }

        public void Write(Vector3 vector)
        {
            writer.Write(vector.x);
            writer.Write(vector.y);
            writer.Write(vector.z);
        }

        public void Write(Color color)
        {
            writer.Write(color.r);
            writer.Write(color.g);
            writer.Write(color.b);
            writer.Write(color.a);
        }
    }

    // -------------------------------------------------------------------------------------------------------------------------

    extension(MessageReader reader)
    {
        public Vector2 ReadVector2()
        {
            return NetHelpers.ReadVector2(reader);
        }

        public Vector3 ReadVector3()
        {
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            float z = reader.ReadSingle();
            return new(x, y, z);
        }

        public Color ReadColor()
        {
            float r = reader.ReadSingle();
            float g = reader.ReadSingle();
            float b = reader.ReadSingle();
            float a = reader.ReadSingle();
            return new(r, g, b, a);
        }
    }
}