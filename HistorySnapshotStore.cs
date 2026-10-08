using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;

namespace ESDP;

internal sealed class HistorySnapshot
{
    internal required string[] Chunks { get; init; }
    internal required string[] Strings { get; init; }
    internal required string Fingerprint { get; init; }
}

/// <summary>Independent snapshots share immutable compressed chunks; pruning does not
/// break a delta chain. Large image/RTF strings are stored once outside the JSON.</summary>
internal sealed class HistorySnapshotStore
{
    private const string Prefix = "@esdp-history:";
    private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);
    private ConditionalWeakTable<string, StringKey> _stringKeys = new();
    private sealed record StringKey(string Value);
    private HashSet<string>? _capturedStrings;
    private Dictionary<string, string>? _restoredStrings;
    private readonly JsonSerializerOptions _options;

    internal HistorySnapshotStore(JsonSerializerOptions options)
    {
        _options = new(options) { WriteIndented = false };
        _options.Converters.Insert(0, new PooledStringConverter(this));
    }

    internal long RetainedBytes => _blobs.Sum(pair => (long)pair.Value.Length + 256);

    internal HistorySnapshot Capture<T>(T value)
    {
        _capturedStrings = new(StringComparer.Ordinal);
        try
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, _options);
            var chunks = new List<string>();
            int start = 0;
            uint rolling = 0;
            // Content-defined boundaries recover after insertions/deletions, so a
            // small edit does not shift every remaining chunk in a large project.
            for (int index = 0; index < json.Length; index++)
            {
                rolling = (rolling << 1) + Gear(json[index]);
                int length = index - start + 1;
                if (length < 1024 || ((rolling & 2047) != 0 && length < 8192)) continue;
                chunks.Add(Intern(json.AsSpan(start, length)));
                start = index + 1;
                rolling = 0;
            }
            if (start < json.Length) chunks.Add(Intern(json.AsSpan(start)));
            return new HistorySnapshot
            {
                Chunks = chunks.ToArray(),
                Strings = _capturedStrings.ToArray(),
                Fingerprint = Convert.ToHexString(SHA256.HashData(json))
            };
        }
        finally { _capturedStrings = null; }
    }

    internal T Restore<T>(HistorySnapshot snapshot)
    {
        using var json = new MemoryStream();
        foreach (string key in snapshot.Chunks)
        {
            using var compressed = new MemoryStream(_blobs[key], writable: false);
            using var inflater = new BrotliStream(compressed, CompressionMode.Decompress);
            inflater.CopyTo(json);
        }
        json.Position = 0;
        _restoredStrings = new(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<T>(json, _options)
                   ?? throw new InvalidDataException("실행 취소 기록이 비어 있습니다.");
        }
        finally { _restoredStrings = null; }
    }

    internal void Collect(IEnumerable<HistorySnapshot> snapshots)
    {
        var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (HistorySnapshot snapshot in snapshots)
        {
            retained.UnionWith(snapshot.Chunks);
            retained.UnionWith(snapshot.Strings);
        }
        foreach (string key in _blobs.Keys.Where(key => !retained.Contains(key)).ToArray())
            _blobs.Remove(key);
    }

    internal void Clear()
    {
        _blobs.Clear();
        _stringKeys = new();
    }

    private string Intern(ReadOnlySpan<byte> data)
    {
        string key = Convert.ToHexString(SHA256.HashData(data));
        if (!_blobs.ContainsKey(key))
        {
            using var output = new MemoryStream();
            using (var compressor = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
                compressor.Write(data);
            _blobs.Add(key, output.ToArray());
        }
        return key;
    }

    private string InternString(string value)
    {
        if (!_stringKeys.TryGetValue(value, out StringKey? cached) ||
            !_blobs.ContainsKey(cached.Value))
        {
            string key = Intern(Encoding.UTF8.GetBytes(value));
            _stringKeys.Remove(value);
            cached = new(key);
            _stringKeys.Add(value, cached);
        }
        _capturedStrings!.Add(cached.Value);
        return cached.Value;
    }

    private string ReadString(string key)
    {
        if (_restoredStrings!.TryGetValue(key, out string? cached)) return cached;
        using var input = new MemoryStream(_blobs[key], writable: false);
        using var inflater = new BrotliStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(inflater, Encoding.UTF8);
        string value = reader.ReadToEnd();
        _restoredStrings.Add(key, value);
        return value;
    }

    private static uint Gear(byte value)
    {
        uint x = (uint)value + 1;
        x ^= x << 13;
        x *= 0x9e3779b1;
        return x ^ (x >> 16);
    }

    private sealed class PooledStringConverter(HistorySnapshotStore store) : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            string? value = reader.GetString();
            if (value?.StartsWith(Prefix + "blob:", StringComparison.Ordinal) == true)
                return store.ReadString(value[(Prefix.Length + 5)..]);
            if (value?.StartsWith(Prefix + "literal:", StringComparison.Ordinal) == true)
                return value[(Prefix.Length + 8)..];
            return value;
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            if (value.Length >= 512)
                writer.WriteStringValue(Prefix + "blob:" + store.InternString(value));
            else if (value.StartsWith(Prefix, StringComparison.Ordinal))
                writer.WriteStringValue(Prefix + "literal:" + value);
            else
                writer.WriteStringValue(value);
        }
    }
}
