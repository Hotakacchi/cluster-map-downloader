using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace ClusterMapDownloader;

/// <summary>
/// Honest structural failure of a GLB tidy run (bad magic, corrupt chunk,
/// impossible layout). The CLI surfaces it as a tool error and the input file
/// is left untouched.
/// </summary>
public sealed class GlbTidyException : Exception
{
    public GlbTidyException(string message) : base(message) { }
}

/// <summary>
/// Outcome of one <see cref="GlbTidier.TidyFile"/> run over one GLB file.
/// The CLI serializes this snake_case for the conversion manifest.
/// </summary>
public sealed record GlbTidyResult
{
    /// <summary>File that was tidied (path exactly as passed in).</summary>
    public required string File { get; init; }

    /// <summary>glTF primitives that received freshly generated normals.</summary>
    public int NormalsGeneratedPrimitives { get; init; }

    /// <summary>Total triangles covered by those new normals.</summary>
    public long NormalsGeneratedTriangles { get; init; }

    /// <summary>Total vertices that got a generated NORMAL attribute.</summary>
    public int NormalsGeneratedVertices { get; init; }

    /// <summary>Primitives left untouched because their data shape is unsupported.</summary>
    public int SkippedUnsupportedPrimitives { get; init; }

    /// <summary>File size in bytes before the run.</summary>
    public long BytesBefore { get; init; }

    /// <summary>File size in bytes after the run (equal to before when unchanged).</summary>
    public long BytesAfter { get; init; }

    /// <summary>True when the file was rewritten; a run with nothing to fix writes nothing.</summary>
    public bool Changed => NormalsGeneratedPrimitives > 0;
}

/// <summary>
/// In-place repair pass for glTF 2.0 binary (GLB) scene exports.
///
/// AssetRipper's generic export writes some Unity meshes WITHOUT a NORMAL
/// attribute. The glTF spec leaves normal computation to consumers, but many
/// runtimes (three.js GLTFLoader among them) do not generate normals either -
/// those meshes render unlit/black. This tool computes smooth per-vertex
/// normals from POSITION + indices (area-weighted accumulation, degenerate
/// vertices get an up vector) and appends them to the GLB: new float32 VEC3
/// bufferViews + accessors, 4-byte aligned, BIN/BUFFERS byteLength updated.
///
/// Deliberately conservative:
///  - triangles primitives only (MODE 4); anything else is counted in
///    SkippedUnsupportedPrimitives and left exactly as exported,
///  - only FLOAT POSITIONs are supported (AssetRipper's own output),
///  - meshes that already have normals are never touched,
///  - a run with nothing to fix does not rewrite the file at all, so a second
///    run is byte-identical (idempotent),
///  - structural failures throw GlbTidyException and leave the file intact;
///    the replacement file is written atomically (temp file + move).
/// </summary>
public static class GlbTidier
{
    private const uint GlbMagic = 0x46546C67u;       // "glTF"
    private const uint JsonChunkType = 0x4E4F534Au;  // "JSON"
    private const uint BinChunkType = 0x004E4942u;   // "BIN" + NUL

    private const int ComponentUnsignedByte = 5121;
    private const int ComponentUnsignedShort = 5123;
    private const int ComponentUnsignedInt = 5125;
    private const int ComponentFloat = 5126;

    private const int ModeTriangles = 4;

    public static GlbTidyResult TidyFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new GlbTidyException("GLB path is empty.");
        }

        var bytes = File.ReadAllBytes(path);
        var before = (long)bytes.Length;
        var (root, originalBin) = ParseGlb(bytes);

        var primitivesFixed = 0;
        var trianglesFixed = 0L;
        var verticesFixed = 0;
        var skippedUnsupported = 0;

        var bufferViews = EnsureJsonArray(root, "bufferViews");
        var accessors = EnsureJsonArray(root, "accessors");
        var meshes = root["meshes"] as JsonArray;

        var outputBin = new MemoryStream(originalBin.Length + 65536);
        outputBin.Write(originalBin, 0, originalBin.Length);

        if (meshes is not null)
        {
            for (var meshIndex = 0; meshIndex < meshes.Count; meshIndex++)
            {
                if (meshes[meshIndex] is not JsonObject mesh ||
                    mesh["primitives"] is not JsonArray primitives)
                {
                    continue;
                }

                foreach (var primitiveNode in primitives)
                {
                    if (primitiveNode is not JsonObject primitive)
                    {
                        continue;
                    }

                    if (primitive["attributes"] is not JsonObject attributes ||
                        attributes.ContainsKey("NORMAL"))
                    {
                        continue;
                    }

                    var mode = primitive["mode"]?.GetValue<int>() ?? ModeTriangles;
                    if (mode != ModeTriangles)
                    {
                        skippedUnsupported++;
                        continue;
                    }

                    var positionAccessorIndex = ReadIndexInt(attributes["POSITION"]);
                    var indicesAccessorIndex = ReadIndexInt(primitive["indices"]);
                    if (positionAccessorIndex < 0)
                    {
                        skippedUnsupported++;
                        continue;
                    }

                    if (positionAccessorIndex < 0 || positionAccessorIndex >= accessors.Count ||
                        (indicesAccessorIndex >= 0 && indicesAccessorIndex >= accessors.Count))
                    {
                        skippedUnsupported++;
                        continue;
                    }

                    var positionAccessor = accessors[positionAccessorIndex] as JsonObject;
                    JsonObject? indicesAccessor = indicesAccessorIndex >= 0
                        ? accessors[indicesAccessorIndex] as JsonObject
                        : null;

                    if (positionAccessor is null ||
                        (indicesAccessorIndex >= 0 && indicesAccessor is null))
                    {
                        skippedUnsupported++;
                        continue;
                    }

                    if (!TryComputeNormalBlob(
                            originalBin,
                            bufferViews,
                            positionAccessor,
                            indicesAccessor,
                            out var fix,
                            out var reason))
                    {
                        skippedUnsupported++;
                        continue;
                    }

                    var viewOffset = AppendAligned(outputBin, fix.Blob);
                    bufferViews.Add(new JsonObject
                    {
                        ["buffer"] = 0,
                        ["byteOffset"] = viewOffset,
                        ["byteLength"] = fix.Blob.Length,
                    });
                    accessors.Add(new JsonObject
                    {
                        ["bufferView"] = bufferViews.Count - 1,
                        ["componentType"] = ComponentFloat,
                        ["count"] = fix.VertexCount,
                        ["type"] = "VEC3",
                        ["min"] = new JsonArray(fix.Min[0], fix.Min[1], fix.Min[2]),
                        ["max"] = new JsonArray(fix.Max[0], fix.Max[1], fix.Max[2]),
                    });
                    attributes["NORMAL"] = accessors.Count - 1;

                    primitivesFixed++;
                    trianglesFixed += fix.Triangles;
                    verticesFixed += fix.VertexCount;
                }
            }
        }

        if (primitivesFixed == 0)
        {
            return new GlbTidyResult
            {
                File = path,
                SkippedUnsupportedPrimitives = skippedUnsupported,
                BytesBefore = before,
                BytesAfter = before,
            };
        }

        var finalBin = outputBin.ToArray();
        var buffers = EnsureJsonArray(root, "buffers");
        if (buffers.Count == 0)
        {
            buffers.Add(new JsonObject { ["byteLength"] = finalBin.Length });
        }
        else if (buffers[0] is JsonObject firstBuffer)
        {
            firstBuffer["byteLength"] = finalBin.Length;
        }
        else
        {
            buffers[0] = new JsonObject { ["byteLength"] = finalBin.Length };
        }

        WriteGlb(root, finalBin, path, out var bytesAfter);

        return new GlbTidyResult
        {
            File = path,
            NormalsGeneratedPrimitives = primitivesFixed,
            NormalsGeneratedTriangles = trianglesFixed,
            NormalsGeneratedVertices = verticesFixed,
            SkippedUnsupportedPrimitives = skippedUnsupported,
            BytesBefore = before,
            BytesAfter = bytesAfter,
        };
    }

    private sealed record NormalFix(byte[] Blob, int VertexCount, long Triangles, float[] Min, float[] Max);

    /// <summary>Walks the GLB container; returns the JSON root plus the (optional) BIN data.</summary>
    private static (JsonObject Root, byte[] Bin) ParseGlb(byte[] bytes)
    {
        if (bytes.Length < 12 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) != GlbMagic)
        {
            throw new GlbTidyException("Not a GLB file: the leading bytes are not the \"glTF\" magic.");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        if (version != 2)
        {
            throw new GlbTidyException($"Unsupported glTF container version {version} (expected 2).");
        }

        var totalLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        if ((long)totalLength != bytes.Length)
        {
            throw new GlbTidyException(
                $"GLB header length ({totalLength}) does not match the file size ({bytes.Length}); refusing to guess.");
        }

        JsonObject? root = null;
        byte[]? bin = null;
        var index = 12;
        while (index < bytes.Length)
        {
            if (index + 8 > bytes.Length)
            {
                throw new GlbTidyException($"Truncated chunk header at byte {index}.");
            }

            var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index, 4));
            var chunkType = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index + 4, 4));
            index += 8;
            if (bytes.Length - index < (long)chunkLength)
            {
                throw new GlbTidyException(
                    $"Chunk 0x{chunkType:X8} claims {chunkLength} bytes but the file ends sooner.");
            }

            if (chunkType == JsonChunkType)
            {
                if (root is not null)
                {
                    throw new GlbTidyException("Two JSON chunks in one GLB.");
                }

                var jsonText = Encoding.UTF8.GetString(bytes, index, (int)chunkLength);
                if (jsonText.Length > 0 && jsonText[0] == '\uFEFF')
                {
                    jsonText = jsonText[1..];
                }

                root = JsonNode.Parse(jsonText) as JsonObject
                    ?? throw new GlbTidyException("The GLB JSON chunk is not a JSON object.");
            }
            else if (chunkType == BinChunkType)
            {
                if (bin is not null)
                {
                    throw new GlbTidyException("Two BIN chunks in one GLB.");
                }

                bin = new byte[chunkLength];
                Buffer.BlockCopy(bytes, index, bin, 0, (int)chunkLength);
            }

            // Vendor/extension chunks are legal; walk over them untouched.
            index += (int)chunkLength;
        }

        if (root is null)
        {
            throw new GlbTidyException("No JSON chunk found in the GLB.");
        }

        return (root, bin ?? Array.Empty<byte>());
    }

    /// <summary>
    /// Computes smooth per-vertex normals for one primitive from POSITION
    /// (+ indices). Returns false with an honest reason when the data shape is
    /// unsupported; the caller then leaves that primitive exactly as exported.
    /// </summary>
    private static bool TryComputeNormalBlob(
        byte[] bin,
        JsonArray bufferViews,
        JsonObject positionAccessor,
        JsonObject? indicesAccessor,
        out NormalFix fix,
        out string reason)
    {
        fix = new NormalFix(Array.Empty<byte>(), 0, 0, Array.Empty<float>(), Array.Empty<float>());
        reason = "";

        if (ReadString(positionAccessor["type"]) != "VEC3" ||
            ReadIndexInt(positionAccessor["componentType"]) != ComponentFloat)
        {
            reason = "POSITION accessor is not FLOAT VEC3";
            return false;
        }

        var vertexCount = ReadIndexInt(positionAccessor["count"]);
        if (vertexCount <= 0)
        {
            reason = "POSITION accessor has no vertices";
            return false;
        }

        var viewIndex = ReadIndexInt(positionAccessor["bufferView"]);
        if (viewIndex < 0 || viewIndex >= bufferViews.Count ||
            bufferViews[viewIndex] is not JsonObject view)
        {
            reason = "POSITION bufferView not found";
            return false;
        }

        if (ReadIndexInt(view["buffer"]) > 0)
        {
            reason = "POSITION buffer is not the first buffer (single-buffer GLBs only)";
            return false;
        }

        var viewOffset = ReadOffset(view["byteOffset"]);
        var viewLength = ReadRequiredNonNegative(view["byteLength"]);
        var byteStride = ReadStride(view["byteStride"]);
        var dataOffset = ReadOffset(positionAccessor["byteOffset"]);
        if (viewOffset < 0 || viewLength <= 0 || byteStride < 0 || dataOffset < 0 ||
            viewLength > bin.Length - viewOffset)
        {
            reason = "POSITION bufferView claims more bytes than the BIN chunk holds";
            return false;
        }

        var elementStride = byteStride == 0 ? 12 : byteStride;
        var dataStart = viewOffset + dataOffset;
        if (dataStart < 0 || (long)(vertexCount - 1) * elementStride + dataStart + 12 > bin.Length)
        {
            reason = "POSITION data runs past the BIN chunk";
            return false;
        }

        var positions = new float[vertexCount * 3];
        for (var vertex = 0; vertex < vertexCount; vertex++)
        {
            var at = (int)(dataStart + vertex * elementStride);
            var x = BinaryPrimitives.ReadSingleLittleEndian(bin.AsSpan(at, 4));
            var y = BinaryPrimitives.ReadSingleLittleEndian(bin.AsSpan(at + 4, 4));
            var z = BinaryPrimitives.ReadSingleLittleEndian(bin.AsSpan(at + 8, 4));
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            {
                reason = "POSITION data contains non-finite floats";
                return false;
            }

            positions[3 * vertex] = x;
            positions[3 * vertex + 1] = y;
            positions[3 * vertex + 2] = z;
        }

        var indexCount = 0;
        int[]? indices = null;
        if (indicesAccessor is not null)
        {
            if (ReadString(indicesAccessor["type"]) != "SCALAR")
            {
                reason = "indices accessor is not SCALAR";
                return false;
            }

            var indexComponent = ReadIndexInt(indicesAccessor["componentType"]);
            if (indexComponent is not (ComponentUnsignedByte or ComponentUnsignedShort or ComponentUnsignedInt))
            {
                reason = "indices componentType is not an unsigned type";
                return false;
            }

            indexCount = ReadIndexInt(indicesAccessor["count"]);
            if (indexCount <= 0 || indexCount % 3 != 0)
            {
                reason = "index count is not a positive multiple of three";
                return false;
            }

            var indexViewIndex = ReadIndexInt(indicesAccessor["bufferView"]);
            if (indexViewIndex < 0 || indexViewIndex >= bufferViews.Count ||
                bufferViews[indexViewIndex] is not JsonObject indexView)
            {
                reason = "indices bufferView not found";
                return false;
            }

            if (ReadIndexInt(indexView["buffer"]) > 0)
            {
                reason = "indices buffer is not the first buffer";
                return false;
            }

            var indexElementSize = indexComponent switch
            {
                ComponentUnsignedByte => 1,
                ComponentUnsignedShort => 2,
                _ => 4,
            };
            var indexStrideRaw = ReadStride(indexView["byteStride"]);
            if (indexStrideRaw < 0 ||
                ReadRequiredNonNegative(indexView["byteLength"]) <= 0)
            {
                reason = "indices bufferView layout is invalid";
                return false;
            }

            var indexStride = indexStrideRaw == 0 ? indexElementSize : indexStrideRaw;
            var indexStart = ReadOffset(indexView["byteOffset"]) + ReadOffset(indicesAccessor["byteOffset"]);
            if (indexStart < 0 ||
                (long)(indexCount - 1) * indexStride + indexStart + indexElementSize > bin.Length)
            {
                reason = "indices data runs past the BIN chunk";
                return false;
            }

            indices = new int[indexCount];
            for (var i = 0; i < indexCount; i++)
            {
                var at = (int)indexStart + i * indexStride;
                var vertexId = indexComponent switch
                {
                    ComponentUnsignedByte => (int)bin[at],
                    ComponentUnsignedShort => BinaryPrimitives.ReadUInt16LittleEndian(bin.AsSpan(at, 2)),
                    _ => (int)BinaryPrimitives.ReadUInt32LittleEndian(bin.AsSpan(at, 4)),
                };
                if (vertexId < 0 || vertexId >= vertexCount)
                {
                    reason = "index value points outside POSITION";
                    return false;
                }

                indices[i] = vertexId;
            }
        }
        else
        {
            if (vertexCount % 3 != 0)
            {
                reason = "non-indexed vertex count is not a multiple of three";
                return false;
            }
        }

        var triangles = indexCount > 0 ? indexCount / 3 : vertexCount / 3;
        var accumulation = new float[vertexCount * 3];
        for (var triangle = 0; triangle < triangles; triangle++)
        {
            int corner0, corner1, corner2;
            if (indices is not null)
            {
                corner0 = indices[3 * triangle];
                corner1 = indices[3 * triangle + 1];
                corner2 = indices[3 * triangle + 2];
            }
            else
            {
                corner0 = 3 * triangle;
                corner1 = corner0 + 1;
                corner2 = corner0 + 2;
            }

            if (corner0 == corner1 || corner0 == corner2 || corner1 == corner2)
            {
                continue;
            }

            var x0 = positions[3 * corner0];
            var y0 = positions[3 * corner0 + 1];
            var z0 = positions[3 * corner0 + 2];
            var ux = positions[3 * corner1] - x0;
            var uy = positions[3 * corner1 + 1] - y0;
            var uz = positions[3 * corner1 + 2] - z0;
            var vx = positions[3 * corner2] - x0;
            var vy = positions[3 * corner2 + 1] - y0;
            var vz = positions[3 * corner2 + 2] - z0;

            var nx = uy * vz - uz * vy;
            var ny = uz * vx - ux * vz;
            var nz = ux * vy - uy * vx;

            accumulation[3 * corner0] += nx;
            accumulation[3 * corner0 + 1] += ny;
            accumulation[3 * corner0 + 2] += nz;
            accumulation[3 * corner1] += nx;
            accumulation[3 * corner1 + 1] += ny;
            accumulation[3 * corner1 + 2] += nz;
            accumulation[3 * corner2] += nx;
            accumulation[3 * corner2 + 1] += ny;
            accumulation[3 * corner2 + 2] += nz;
        }

        var blob = new byte[vertexCount * 12];
        var componentMin = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
        var componentMax = new[] { float.MinValue, float.MinValue, float.MinValue };
        for (var vertex = 0; vertex < vertexCount; vertex++)
        {
            var nx = accumulation[3 * vertex];
            var ny = accumulation[3 * vertex + 1];
            var nz = accumulation[3 * vertex + 2];
            var lengthSquared = nx * nx + ny * ny + nz * nz;
            float ox, oy, oz;
            if (!float.IsFinite(lengthSquared) || lengthSquared <= 1e-20f)
            {
                ox = 0f;
                oy = 1f;
                oz = 0f;
            }
            else
            {
                var length = MathF.Sqrt(lengthSquared);
                ox = nx / length;
                oy = ny / length;
                oz = nz / length;
            }

            BinaryPrimitives.WriteSingleLittleEndian(blob.AsSpan(vertex * 12, 4), ox);
            BinaryPrimitives.WriteSingleLittleEndian(blob.AsSpan(vertex * 12 + 4, 4), oy);
            BinaryPrimitives.WriteSingleLittleEndian(blob.AsSpan(vertex * 12 + 8, 4), oz);
            componentMin[0] = MathF.Min(componentMin[0], ox);
            componentMin[1] = MathF.Min(componentMin[1], oy);
            componentMin[2] = MathF.Min(componentMin[2], oz);
            componentMax[0] = MathF.Max(componentMax[0], ox);
            componentMax[1] = MathF.Max(componentMax[1], oy);
            componentMax[2] = MathF.Max(componentMax[2], oz);
        }

        fix = new NormalFix(blob, vertexCount, triangles, componentMin, componentMax);
        return true;
    }

    /// <summary>Pads the stream to a 4-byte boundary with zeros, appends blob, returns the start offset.</summary>
    private static long AppendAligned(MemoryStream stream, byte[] blob)
    {
        while (stream.Position % 4 != 0)
        {
            stream.WriteByte(0);
        }

        var offset = stream.Position;
        stream.Write(blob, 0, blob.Length);
        return offset;
    }

    /// <summary>Writes the full GLB (glTF magic, JSON chunk space-padded, BIN chunk zero-padded) via temp + move.</summary>
    private static void WriteGlb(JsonObject root, byte[] bin, string path, out long totalLength)
    {
        var json = Encoding.UTF8.GetBytes(root.ToJsonString());
        var jsonPadded = (json.Length + 3) & ~3;
        var binPadded = bin.Length == 0 ? 0 : (bin.Length + 3) & ~3;
        totalLength = 12 + 8 + jsonPadded + (binPadded > 0 ? 8 + binPadded : 0);

        using var output = new MemoryStream((int)totalLength);
        var header = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), GlbMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), (uint)totalLength);
        output.Write(header, 0, header.Length);

        var jsonChunkHeader = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(jsonChunkHeader.AsSpan(0, 4), (uint)jsonPadded);
        BinaryPrimitives.WriteUInt32LittleEndian(jsonChunkHeader.AsSpan(4, 4), JsonChunkType);
        output.Write(jsonChunkHeader, 0, jsonChunkHeader.Length);
        output.Write(json, 0, json.Length);
        for (var pad = json.Length; pad < jsonPadded; pad++)
        {
            output.WriteByte(0x20);
        }

        if (binPadded > 0)
        {
            var binChunkHeader = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(binChunkHeader.AsSpan(0, 4), (uint)binPadded);
            BinaryPrimitives.WriteUInt32LittleEndian(binChunkHeader.AsSpan(4, 4), BinChunkType);
            output.Write(binChunkHeader, 0, binChunkHeader.Length);
            output.Write(bin, 0, bin.Length);
            for (var pad = bin.Length; pad < binPadded; pad++)
            {
                output.WriteByte(0x00);
            }
        }

        var tempPath = path + ".tidy-tmp";
        File.WriteAllBytes(tempPath, output.ToArray());
        File.Move(tempPath, path, overwrite: true);
    }

    private static JsonArray EnsureJsonArray(JsonObject container, string propertyName)
    {
        if (container[propertyName] is JsonArray existing)
        {
            return existing;
        }

        var created = new JsonArray();
        container[propertyName] = created;
        return created;
    }

    private static int ReadIndexInt(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var parsed) ? parsed : -1;

    private static long ReadOffset(JsonNode? node)
    {
        if (node is null)
        {
            return 0; // absent means 0 per the glTF spec
        }

        return node is JsonValue value && value.TryGetValue<long>(out var parsed) && parsed >= 0
            ? parsed
            : -1; // present but invalid
    }

    private static long ReadRequiredNonNegative(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<long>(out var parsed) && parsed >= 0
            ? parsed
            : -1;

    private static int ReadStride(JsonNode? node)
    {
        if (node is null)
        {
            return 0; // absent means tightly packed per the glTF spec
        }

        return node is JsonValue value && value.TryGetValue<int>(out var parsed) && parsed >= 0
            ? parsed
            : -1; // present but invalid
    }

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var parsed) ? parsed : null;
}
