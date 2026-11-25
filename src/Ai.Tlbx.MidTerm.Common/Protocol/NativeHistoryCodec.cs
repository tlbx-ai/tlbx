using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Ai.Tlbx.MidTerm.Common.Protocol;

// Native values, not JSON text: source-generated metadata supplies the existing
// contract, while per-packet string/date tables remove repeated names and values.
public static class NativeHistoryCodec
{
    private const uint Magic = 0x31484341; // ACH1
    private const int MaxBytes = 64 * 1024 * 1024;
    private const int MaxDepth = 64;
    private const int MaxCollection = 1_000_000;
    private static readonly ConcurrentDictionary<JsonTypeInfo, Model> Models = new();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private enum Token : byte
    {
        Null, False, True, Int32, Int64, Double, Decimal,
        String, StringReference, Date, DateReference, Array, Object, Shape, ShapeReference, Absent, JsonNumber
    }

    private sealed record Field(JsonPropertyInfo Property, JsonTypeInfo Info);
    private sealed class Model(JsonTypeInfo info)
    {
        public Field[] Properties { get; } = info.Properties.Where(p => p.Get is not null)
            .Select(p => new Field(p, info.Options.GetTypeInfo(p.PropertyType))).ToArray();
        public Dictionary<string, Field> Names { get; } = info.Properties
            .Where(p => p.Set is not null).ToDictionary(p => p.Name, p => new Field(p, info.Options.GetTypeInfo(p.PropertyType)), StringComparer.Ordinal);
        public JsonTypeInfo? Element { get; } = info.Kind == JsonTypeInfoKind.Enumerable
            ? info.Options.GetTypeInfo(info.Type.GetGenericArguments()[0]) : null;
    }

    public static byte[] Encode<T>(T value, JsonTypeInfo<T> info)
    {
        using var writer = new Writer();
        writer.UInt32(Magic);
        writer.Value(value, info, 0);
        return writer.Written.ToArray();
    }

    public static T Decode<T>(ReadOnlySpan<byte> packet, JsonTypeInfo<T> info)
    {
        if (packet.Length > MaxBytes) throw new InvalidDataException("History packet exceeds its byte limit.");
        var reader = new Reader(packet);
        if (reader.UInt32() != Magic) throw new InvalidDataException("Unknown history binary format.");
        var result = reader.Value(info, 0, null);
        if (!reader.Done) throw new InvalidDataException("Trailing history binary data.");
        return result is T typed ? typed : throw new InvalidDataException("Missing history root.");
    }

    private sealed class Writer : IDisposable
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        private int _written;
        private readonly Dictionary<Model, uint> _shapes = new();
        private readonly Dictionary<string, uint> _strings = new(StringComparer.Ordinal);
        private readonly Dictionary<(long Ticks, long Offset), uint> _dates = new();
        public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _written);
        public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
        private void Advance(int count) => _written += count;

        private Span<byte> Reserve(int count)
        {
            if (count < 0 || count > MaxBytes - _written)
                throw new InvalidDataException("History packet exceeds its byte limit.");
            if (count > _buffer.Length - _written)
            {
                var next = ArrayPool<byte>.Shared.Rent(Math.Max(_written + count, Math.Min(MaxBytes, _buffer.Length * 2)));
                _buffer.AsSpan(0, _written).CopyTo(next);
                ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
                _buffer = next;
            }
            return _buffer.AsSpan(_written, count);
        }

        private void Byte(byte value) { Reserve(1)[0] = value; Advance(1); }
        public void UInt32(uint value) { BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value); Advance(4); }
        private void UInt64(ulong value) { BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), value); Advance(8); }
        private void Tag(Token value) => Byte((byte)value);
        private void Var(ulong value)
        {
            while (value >= 128) { Byte((byte)(value | 128)); value >>= 7; }
            Byte((byte)value);
        }
        private void Signed(long value) => Var(unchecked((ulong)((value << 1) ^ (value >> 63))));

        private void String(string value)
        {
            if (_strings.TryGetValue(value, out var index)) { Tag(Token.StringReference); Var(index); return; }
            _strings.Add(value, (uint)_strings.Count);
            Tag(Token.String);
            var count = Encoding.UTF8.GetByteCount(value);
            Var((uint)count);
            var written = Encoding.UTF8.GetBytes(value, Reserve(count));
            Advance(written);
        }

        private void Date(DateTimeOffset value)
        {
            var key = (value.Ticks, value.Offset.Ticks);
            if (_dates.TryGetValue(key, out var index)) { Tag(Token.DateReference); Var(index); return; }
            // DateTimeOffset equality ignores offset; the wire must preserve it.
            // A normalized UTC key is insufficient for differently displayed dates.
            Tag(Token.Date);
            _dates.Add(key, (uint)_dates.Count);
            Signed(value.Ticks - DateTime.UnixEpoch.Ticks);
            Signed((long)value.Offset.TotalMinutes);
        }

        public void Value(object? value, JsonTypeInfo info, int depth)
        {
            if (depth > MaxDepth) throw new InvalidDataException("History nesting limit exceeded.");
            switch (value)
            {
                case null: Tag(Token.Null); return;
                case string text: String(text); return;
                case bool flag: Tag(flag ? Token.True : Token.False); return;
                case int number: Tag(Token.Int32); Signed(number); return;
                case long number: Tag(Token.Int64); Signed(number); return;
                case double number:
                    if (!double.IsFinite(number)) throw new InvalidDataException("Non-finite history number.");
                    Tag(Token.Double); UInt64(unchecked((ulong)BitConverter.DoubleToInt64Bits(number))); return;
                case decimal number:
                    Tag(Token.Decimal); Span<int> bits = stackalloc int[4]; decimal.GetBits(number, bits);
                    foreach (var part in bits) UInt32(unchecked((uint)part)); return;
                case DateTimeOffset date: Date(date); return;
                case JsonElement json: Json(json, depth); return;
                case IList list:
                    if (list.Count > MaxCollection) throw new InvalidDataException("History collection limit exceeded.");
                    var element = Models.GetOrAdd(info, static i => new Model(i)).Element
                        ?? throw new InvalidDataException("Missing history element contract.");
                    Tag(Token.Array); Var((uint)list.Count);
                    foreach (var item in list) Value(item, element, depth + 1);
                    return;
            }
            if (info.Kind != JsonTypeInfoKind.Object) throw new InvalidDataException($"Unsupported history value {info.Type}.");
            var model = Models.GetOrAdd(info, static i => new Model(i));
            if (_shapes.TryGetValue(model, out var shape))
            {
                Tag(Token.ShapeReference); Var(shape);
            }
            else
            {
                _shapes.Add(model, (uint)_shapes.Count);
                Tag(Token.Shape); Var((uint)model.Properties.Length);
                foreach (var entry in model.Properties) String(entry.Property.Name);
            }
            foreach (var entry in model.Properties)
            {
                var property = entry.Property;
                var field = property.Get!(value);
                if (property.ShouldSerialize is { } should && !should(value, field)) Tag(Token.Absent);
                else Value(field, entry.Info, depth + 1);
            }
        }

        [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP004:Don't ignore created IDisposable", Justification = "JsonElement array/object enumerators are disposed by foreach lowering.")]
        private void Json(JsonElement value, int depth)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Null: Tag(Token.Null); break;
                case JsonValueKind.False: Tag(Token.False); break;
                case JsonValueKind.True: Tag(Token.True); break;
                case JsonValueKind.String: String(value.GetString() ?? string.Empty); break;
                case JsonValueKind.Number:
                    // Opaque provider configuration may exceed decimal precision,
                    // underflow decimal, or contain -0. Keep its exact JSON number.
                    Tag(Token.JsonNumber); String(value.GetRawText());
                    break;
                case JsonValueKind.Array:
                    Tag(Token.Array); Var((uint)value.GetArrayLength());
                    foreach (var item in value.EnumerateArray()) { CheckDepth(depth + 1); Json(item, depth + 1); }
                    break;
                case JsonValueKind.Object:
                    Tag(Token.Object); var count = 0; foreach (var _ in value.EnumerateObject()) count++;
                    Var((uint)count);
                    foreach (var item in value.EnumerateObject()) { String(item.Name); CheckDepth(depth + 1); Json(item.Value, depth + 1); }
                    break;
                default: throw new InvalidDataException("Undefined history JSON value.");
            }
        }
    }

    private static void CheckDepth(int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException("History nesting limit exceeded.");
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;
        private readonly List<string> _strings = [];
        private readonly List<(JsonTypeInfo Info, Field[] Fields)> _shapes = [];
        private readonly List<DateTimeOffset> _dates = [];
        public readonly bool Done => _position == _data.Length;
        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > _data.Length - _position) throw new InvalidDataException("Truncated history packet.");
            var result = _data.Slice(_position, count); _position += count; return result;
        }
        private byte Byte() => Take(1)[0];
        public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        private ulong UInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        private ulong Var()
        {
            ulong value = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                var part = Byte();
                if (shift == 63 && part > 1) throw new InvalidDataException("Overflowing history integer.");
                value |= (ulong)(part & 127) << shift;
                if (part < 128) return value;
            }
            throw new InvalidDataException("Invalid history integer.");
        }
        private long Signed() { var value = Var(); return unchecked((long)(value >> 1) ^ -((long)value & 1)); }
        private int Count()
        {
            var count = Var();
            if (count > MaxCollection || count > (ulong)(_data.Length - _position))
                throw new InvalidDataException("Invalid history collection length.");
            return (int)count;
        }
        private string String(Token tag)
        {
            if (tag == Token.StringReference)
            {
                var index = Var();
                return index < (ulong)_strings.Count ? _strings[(int)index]
                    : throw new InvalidDataException("Invalid history string reference.");
            }
            if (tag != Token.String) throw new InvalidDataException("History property name is not a string.");
            var length = Var();
            if (length > (ulong)(_data.Length - _position)) throw new InvalidDataException("Invalid history string length.");
            var value = StrictUtf8.GetString(Take((int)length)); _strings.Add(value); return value;
        }
        private DateTimeOffset Date(Token tag)
        {
            if (tag == Token.DateReference)
            {
                var index = Var();
                return index < (ulong)_dates.Count ? _dates[(int)index]
                    : throw new InvalidDataException("Invalid history date reference.");
            }
            var ticks = Signed(); var offset = Signed();
            if (offset is < -840 or > 840) throw new InvalidDataException("Invalid history time offset.");
            var value = new DateTimeOffset(checked(ticks + DateTime.UnixEpoch.Ticks), TimeSpan.FromMinutes(offset));
            _dates.Add(value); return value;
        }
        private decimal Decimal()
        {
            var lo = (int)UInt32(); var mid = (int)UInt32(); var hi = (int)UInt32(); var flags = UInt32();
            var scale = (byte)(flags >> 16);
            if ((flags & 0x7F00FFFF) != 0 || scale > 28) throw new InvalidDataException("Invalid history decimal.");
            return new decimal(lo, mid, hi, (flags & 0x80000000) != 0, scale);
        }

        public object? Value(JsonTypeInfo info, int depth, object? existing)
        {
            CheckDepth(depth);
            if (Nullable.GetUnderlyingType(info.Type) == typeof(JsonElement) && _position < _data.Length && _data[_position] == 0) { _position++; return null; }
            if (info.Type == typeof(JsonElement) || Nullable.GetUnderlyingType(info.Type) == typeof(JsonElement)) return Json(depth);
            var tag = (Token)Byte();
            switch (tag)
            {
                case Token.Null: return null;
                case Token.False: return false;
                case Token.True: return true;
                case Token.Int32: return checked((int)Signed());
                case Token.Int64: return Signed();
                case Token.Double: return BitConverter.Int64BitsToDouble(unchecked((long)UInt64()));
                case Token.Decimal: return Decimal();
                case Token.String: case Token.StringReference: return String(tag);
                case Token.Date: case Token.DateReference: return Date(tag);
                case Token.Shape: case Token.ShapeReference:
                {
                    Field[] fields;
                    if (tag == Token.Shape)
                    {
                        var count = Count();
                        var model = Models.GetOrAdd(info, static i => new Model(i));
                        fields = new Field[count];
                        var seen = new HashSet<string>(StringComparer.Ordinal);
                        for (var i = 0; i < count; i++)
                        {
                            var name = String((Token)Byte());
                            if (!seen.Add(name) || !model.Names.TryGetValue(name, out var field))
                                throw new InvalidDataException("Unknown or repeated native history field.");
                            fields[i] = field;
                        }
                        _shapes.Add((info, fields));
                    }
                    else
                    {
                        var index = Var();
                        if (index >= (ulong)_shapes.Count || _shapes[(int)index].Info.Type != info.Type)
                            throw new InvalidDataException("Invalid native history shape reference.");
                        fields = _shapes[(int)index].Fields;
                    }
                    var value = existing ?? info.CreateObject?.Invoke()
                        ?? throw new InvalidDataException("Missing native history constructor.");
                    foreach (var field in fields)
                    {
                        if (_position < _data.Length && _data[_position] == (byte)Token.Absent) { _position++; continue; }
                        field.Property.Set!(value, Value(field.Info, depth + 1,
                            field.Info.Kind is JsonTypeInfoKind.Object or JsonTypeInfoKind.Enumerable ? field.Property.Get?.Invoke(value) : null));
                    }
                    return value;
                }
                case Token.Array:
                {
                    var count = Count(); var model = Models.GetOrAdd(info, static i => new Model(i));
                    var element = model.Element ?? throw new InvalidDataException("Missing history element contract.");
                    var list = existing as IList ?? info.CreateObject?.Invoke() as IList
                        ?? throw new InvalidDataException("Missing native history list constructor.");
                    list.Clear();
                    for (var i = 0; i < count; i++) list.Add(Value(element, depth + 1, null));
                    return list;
                }
                case Token.Object:
                {
                    var count = Count(); var model = Models.GetOrAdd(info, static i => new Model(i));
                    var value = existing ?? info.CreateObject?.Invoke()
                        ?? throw new InvalidDataException("Missing native history constructor.");
                    for (var i = 0; i < count; i++)
                    {
                        var name = String((Token)Byte());
                        if (!model.Names.TryGetValue(name, out var entry)) throw new InvalidDataException("Unknown native history field.");
                        var property = entry.Property;
                        var fieldInfo = entry.Info;
                        var field = Value(fieldInfo, depth + 1,
                            fieldInfo.Kind is JsonTypeInfoKind.Object or JsonTypeInfoKind.Enumerable ? property.Get?.Invoke(value) : null);
                        property.Set!(value, field);
                    }
                    return value;
                }
                default: throw new InvalidDataException("Unknown history binary token.");
            }
        }

        private JsonElement Json(int depth)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer)) { JsonValue(writer, depth); writer.Flush(); }
            using var doc = JsonDocument.Parse(buffer.WrittenMemory);
            return doc.RootElement.Clone();
        }
        private void JsonValue(Utf8JsonWriter writer, int depth)
        {
            CheckDepth(depth);
            var tag = (Token)Byte();
            switch (tag)
            {
                case Token.Null: writer.WriteNullValue(); break;
                case Token.False: writer.WriteBooleanValue(false); break;
                case Token.True: writer.WriteBooleanValue(true); break;
                case Token.Int32: case Token.Int64: writer.WriteNumberValue(Signed()); break;
                case Token.Double: writer.WriteNumberValue(BitConverter.Int64BitsToDouble(unchecked((long)UInt64()))); break;
                case Token.Decimal: writer.WriteNumberValue(Decimal()); break;
                case Token.JsonNumber: writer.WriteRawValue(String((Token)Byte())); break;
                case Token.String: case Token.StringReference: writer.WriteStringValue(String(tag)); break;
                case Token.Array:
                    var count = Count(); writer.WriteStartArray();
                    for (var i = 0; i < count; i++) JsonValue(writer, depth + 1);
                    writer.WriteEndArray(); break;
                case Token.Object:
                    var properties = Count(); writer.WriteStartObject();
                    for (var i = 0; i < properties; i++) { writer.WritePropertyName(String((Token)Byte())); JsonValue(writer, depth + 1); }
                    writer.WriteEndObject(); break;
                default: throw new InvalidDataException("Invalid history JSON token.");
            }
        }
    }
}
