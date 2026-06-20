using System;
using System.Text;

namespace MapRenderer.Core.Mvt
{
    /// <summary>
    /// Minimal protobuf wire-format reader over a byte buffer (clean-room; only the subset the
    /// Mapbox Vector Tile schema needs: varint, zigzag, length-delimited, field skip).
    /// </summary>
    public struct ProtobufReader
    {
        private readonly byte[] _b;
        private int _pos;
        private readonly int _end;

        public ProtobufReader(byte[] buffer, int start, int end)
        {
            _b = buffer;
            _pos = start;
            _end = end;
        }

        public ProtobufReader(byte[] buffer) : this(buffer, 0, buffer.Length) { }

        public bool HasMore => _pos < _end;

        /// <summary>A reader over a sub-range of the same buffer (for nested messages).</summary>
        public ProtobufReader Slice(int start, int end) => new ProtobufReader(_b, start, end);

        public ulong ReadVarint()
        {
            ulong result = 0;
            int shift = 0;
            while (true)
            {
                if (_pos >= _end) throw new InvalidOperationException("varint overruns buffer");
                byte b = _b[_pos++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift > 63) throw new InvalidOperationException("varint too long");
            }
            return result;
        }

        public uint ReadTag() => (uint)ReadVarint();
        public static int FieldNumber(uint tag) => (int)(tag >> 3);
        public static int WireType(uint tag) => (int)(tag & 7);

        public uint ReadUInt32() => (uint)ReadVarint();

        /// <summary>Reads a wire-type-1 (64-bit) field as a double (protobuf fixed64, little-endian).</summary>
        public double ReadDouble()
        {
            if (_pos + 8 > _end) throw new InvalidOperationException("double overruns buffer");
            // Protobuf fixed64 is little-endian IEEE 754.
            long bits;
            if (BitConverter.IsLittleEndian)
            {
                bits = (long)_b[_pos]
                     | ((long)_b[_pos + 1] << 8)
                     | ((long)_b[_pos + 2] << 16)
                     | ((long)_b[_pos + 3] << 24)
                     | ((long)_b[_pos + 4] << 32)
                     | ((long)_b[_pos + 5] << 40)
                     | ((long)_b[_pos + 6] << 48)
                     | ((long)_b[_pos + 7] << 56);
            }
            else
            {
                bits = ((long)_b[_pos] << 56)
                     | ((long)_b[_pos + 1] << 48)
                     | ((long)_b[_pos + 2] << 40)
                     | ((long)_b[_pos + 3] << 32)
                     | ((long)_b[_pos + 4] << 24)
                     | ((long)_b[_pos + 5] << 16)
                     | ((long)_b[_pos + 6] << 8)
                     | (long)_b[_pos + 7];
            }
            _pos += 8;
            return BitConverter.Int64BitsToDouble(bits);
        }

        /// <summary>Reads a wire-type-5 (32-bit) field as a float (protobuf fixed32, little-endian).</summary>
        public float ReadFloat()
        {
            if (_pos + 4 > _end) throw new InvalidOperationException("float overruns buffer");
            // Protobuf fixed32 is little-endian IEEE 754.
            int bits = (int)_b[_pos]
                     | ((int)_b[_pos + 1] << 8)
                     | ((int)_b[_pos + 2] << 16)
                     | ((int)_b[_pos + 3] << 24);
            _pos += 4;
            return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
        }

        /// <summary>
        /// Reads a varint-encoded sint64 value (zigzag: (n >> 1) ^ -(n &amp; 1)).
        /// Used for the MVT Value sint_value field (wire type 0).
        /// </summary>
        public long ReadSInt64()
        {
            ulong u = ReadVarint();
            return (long)(u >> 1) ^ -(long)(u & 1);
        }

        /// <summary>Reads a length-delimited field, returning [start,end) and advancing past it.</summary>
        public (int start, int end) ReadLengthDelimited()
        {
            int len = (int)ReadVarint();
            int s = _pos;
            int e = s + len;
            if (e > _end || len < 0) throw new InvalidOperationException("length-delimited overruns buffer");
            _pos = e;
            return (s, e);
        }

        public string ReadString()
        {
            var (s, e) = ReadLengthDelimited();
            return Encoding.UTF8.GetString(_b, s, e - s);
        }

        /// <summary>Skips a field of the given wire type.</summary>
        public void SkipField(int wireType)
        {
            switch (wireType)
            {
                case 0: ReadVarint(); break;                                   // varint
                case 1: _pos += 8; break;                                      // 64-bit
                case 2: { int len = (int)ReadVarint(); _pos += len; break; }   // length-delimited
                case 5: _pos += 4; break;                                      // 32-bit
                default: throw new InvalidOperationException($"unsupported wire type {wireType}");
            }
            if (_pos > _end) throw new InvalidOperationException("skip overruns buffer");
        }
    }
}
