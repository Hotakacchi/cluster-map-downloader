using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using ClusterMapDownloader;
using Xunit;

namespace ClusterMapDownloader.Tests;

/// <summary>
/// GlbTidier behaviour checked on synthetic GLB files only:
/// normals generation for indexed/non-indexed triangle primitives, correct
/// handling of byte-strided POSITION, honest skips (unsupported data shapes)
/// that leave the file byte-identical, byte-identical idempotency for
/// nothing-to-fix runs, and loud failures on corrupt containers.
/// </summary>
public class GlbTidierTests
{
    private const uint JsonChunkType = 0x4E4F534Au;
    private const uint BinChunkType = 0x004E4942u;
    private const int ComponentUnsignedShort = 5123;
    private const int ComponentFloat = 5126;

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cmd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    private static MemoryStream WriteFloat(MemoryStream stream, float value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(buffer, value);
        stream.Write(buffer);
        return stream;
    }

    private static MemoryStream WriteUShort(MemoryStream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        stream.Write(buffer);
        return stream;
    }

    /// <summary>
    /// Builds a one-primitive GLB: positions in bufferView 0 (optional
    /// byteStride), indices in bufferView 1 (ushort), optional pre-existing
    /// normals in bufferView 2.
    /// </summary>
    private static (JsonObject Root, byte[] Bin) BuildTriangleGlb(
        float[] positions,
        int[]? indices = null,
        bool existingNormals = false,
        int positionStride = 0,
        int positionComponentType = ComponentFloat,
        int indexComponentType = ComponentUnsignedShort,
        long? positionByteLength = null,
        int? mode = 4)
    {
        var vertexCount = positions.Length / 3;
        var bin = new MemoryStream();

        if (positionStride <= 12)
        {
            foreach (var value in positions)
            {
                WriteFloat(bin, value);
            }
        }
        else
        {
            for (var vertex = 0; vertex < vertexCount; vertex++)
            {
                WriteFloat(bin, positions[3 * vertex]);
                WriteFloat(bin, positions[3 * vertex + 1]);
                WriteFloat(bin, positions[3 * vertex + 2]);
                for (var pad = 12; pad < positionStride; pad++)
                {
                    bin.WriteByte(0xEE);
                }
            }
        }

        var positionBlockLength = bin.Length;
        var indexViewOffset = (int)bin.Length;
        if (indices is not null)
        {
            foreach (var index in indices)
            {
                WriteUShort(bin, (ushort)index);
            }
        }
        var indexBlockLength = bin.Length - indexViewOffset;

        var normalsViewOffset = (int)bin.Length;
        if (existingNormals)
        {
            for (var vertex = 0; vertex < vertexCount; vertex++)
            {
                WriteFloat(bin, 0f);
                WriteFloat(bin, 1f);
                WriteFloat(bin, 0f);
            }
        }

        var bufferViews = new JsonArray();
        var positionView = new JsonObject
        {
            ["buffer"] = 0,
            ["byteOffset"] = 0,
            ["byteLength"] = positionByteLength ?? positionBlockLength,
        };
        if (positionStride > 0)
        {
            positionView["byteStride"] = positionStride;
        }
        bufferViews.Add(positionView);

        var accessors = new JsonArray();
        accessors.Add(new JsonObject
        {
            ["bufferView"] = 0,
            ["componentType"] = positionComponentType,
            ["count"] = vertexCount,
            ["type"] = "VEC3",
            ["min"] = new JsonArray(0.0, 0.0, 0.0),
            ["max"] = new JsonArray(1.0, 1.0, 1.0),
        });

        var attributes = new JsonObject
        {
            ["POSITION"] = accessors.Count - 1,
        };
        var indexAccessorIndexWire = -1;

        if (indices is not null)
        {
            bufferViews.Add(new JsonObject
            {
                ["buffer"] = 0,
                ["byteOffset"] = indexViewOffset,
                ["byteLength"] = indexBlockLength,
            });
            accessors.Add(new JsonObject
            {
                ["bufferView"] = bufferViews.Count - 1,
                ["componentType"] = indexComponentType,
                ["count"] = indices.Length,
                ["type"] = "SCALAR",
            });
            indexAccessorIndexWire = accessors.Count - 1;
        }

        if (existingNormals)
        {
            bufferViews.Add(new JsonObject
            {
                ["buffer"] = 0,
                ["byteOffset"] = normalsViewOffset,
                ["byteLength"] = vertexCount * 12,
            });
            accessors.Add(new JsonObject
            {
                ["bufferView"] = bufferViews.Count - 1,
                ["componentType"] = ComponentFloat,
                ["count"] = vertexCount,
                ["type"] = "VEC3",
            });
            attributes["NORMAL"] = accessors.Count - 1;
        }

        var primitive = new JsonObject { ["attributes"] = attributes };
        if (indexAccessorIndexWire >= 0)
        {
            primitive["indices"] = indexAccessorIndexWire;
        }
        if (mode is int modeValue)
        {
            primitive["mode"] = modeValue;
        }

        var meshes = new JsonArray();
        var primitives = new JsonArray { primitive };
        meshes.Add(new JsonObject { ["primitives"] = primitives });

        var root = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray
            {
                new JsonObject { ["nodes"] = new JsonArray(0) },
            },
            ["nodes"] = new JsonArray
            {
                new JsonObject { ["mesh"] = 0, ["name"] = "TinyTriangle" },
            },
            ["meshes"] = meshes,
            ["accessors"] = accessors,
            ["bufferViews"] = bufferViews,
            ["buffers"] = new JsonArray
            {
                new JsonObject { ["byteLength"] = bin.Length },
            },
        };

        return (root, bin.ToArray());
    }

    /// <summary>Serializes root + bin into a spec-conformant GLB container.</summary>
    private static byte[] WrapGlb(JsonObject root, byte[] bin)
    {
        var json = Encoding.UTF8.GetBytes(root.ToJsonString());
        var jsonPadded = (json.Length + 3) & ~3;
        var binPadded = bin.Length == 0 ? 0 : (bin.Length + 3) & ~3;
        var total = 12 + 8 + jsonPadded + (binPadded > 0 ? 8 + binPadded : 0);

        using var output = new MemoryStream();
        var header = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0x46546C67u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), (uint)total);
        output.Write(header, 0, header.Length);

        var jsonChunk = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(jsonChunk.AsSpan(0, 4), (uint)jsonPadded);
        BinaryPrimitives.WriteUInt32LittleEndian(jsonChunk.AsSpan(4, 4), JsonChunkType);
        output.Write(jsonChunk, 0, jsonChunk.Length);
        output.Write(json, 0, json.Length);
        for (var pad = json.Length; pad < jsonPadded; pad++)
        {
            output.WriteByte(0x20);
        }

        if (binPadded > 0)
        {
            var binChunk = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(binChunk.AsSpan(0, 4), (uint)binPadded);
            BinaryPrimitives.WriteUInt32LittleEndian(binChunk.AsSpan(4, 4), BinChunkType);
            output.Write(binChunk, 0, binChunk.Length);
            output.Write(bin, 0, bin.Length);
            for (var pad = bin.Length; pad < binPadded; pad++)
            {
                output.WriteByte(0x00);
            }
        }

        return output.ToArray();
    }

    private static (JsonObject Root, byte[] Bin) ReadGlbBack(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 12);
        JsonObject? root = null;
        byte[] bin = [];
        var index = 12;
        while (index + 8 <= bytes.Length)
        {
            var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index, 4));
            var chunkType = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index + 4, 4));
            index += 8;
            if (chunkType == JsonChunkType)
            {
                root = Assert.IsType<JsonObject>(JsonNode.Parse(Encoding.UTF8.GetString(bytes, index, (int)chunkLength)));
            }
            else if (chunkType == BinChunkType)
            {
                bin = new byte[chunkLength];
                Buffer.BlockCopy(bytes, index, bin, 0, (int)chunkLength);
            }
            index += (int)chunkLength;
        }
        Assert.NotNull(root);
        return (root!, bin);
    }

    /// <summary>Reads the generated NORMAL attribute of the first primitive, if present.</summary>
    private static float[]? ReadFirstPrimitiveNormals(JsonObject root, byte[] bin)
    {
        if (root["meshes"] is not JsonArray meshes ||
            meshes[0] is not JsonObject mesh ||
            mesh["primitives"] is not JsonArray primitives ||
            primitives[0] is not JsonObject primitive ||
            primitive["attributes"] is not JsonObject attributes ||
            attributes["NORMAL"] is not JsonValue normalValue ||
            !normalValue.TryGetValue<int>(out var accessorIndex) ||
            root["accessors"] is not JsonArray accessors ||
            accessors[accessorIndex] is not JsonObject accessor ||
            accessor["bufferView"] is not JsonValue viewValue ||
            !viewValue.TryGetValue<int>(out var viewIndex) ||
            root["bufferViews"] is not JsonArray bufferViews ||
            bufferViews[viewIndex] is not JsonObject view ||
            accessor["count"] is not JsonValue countValue ||
            !countValue.TryGetValue<int>(out var count) ||
            view["byteOffset"] is not JsonValue offsetValue ||
            !offsetValue.TryGetValue<int>(out var viewOffset))
        {
            return null;
        }

        var start = viewOffset + (accessor["byteOffset"] is JsonValue accessorOffset &&
                                  accessorOffset.TryGetValue<int>(out var aOffset)
            ? aOffset
            : 0);
        var normals = new float[count * 3];
        for (var i = 0; i < count * 3; i++)
        {
            normals[i] = BinaryPrimitives.ReadSingleLittleEndian(bin.AsSpan(start + i * 4, 4));
        }

        return normals;
    }

    private static string WriteGlb(string dir, string name, JsonObject root, byte[] bin)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, WrapGlb(root, bin));
        return path;
    }

    private static readonly float[] UpFacingTriangle = [0, 0, 0, 1, 0, 0, 0, 1, 0];
    private static readonly int[] TriangleIndices = [0, 1, 2];

    [Fact]
    public void Metallic_factor_is_zeroed_when_mr_texture_is_missing()
    {
        var dir = TempDir();
        try
        {
            var (root, bin) = BuildTriangleGlb(UpFacingTriangle, TriangleIndices, existingNormals: true);
            root["materials"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "StylizedConcrete",
                    ["pbrMetallicRoughness"] = new JsonObject
                    {
                        ["metallicFactor"] = 1.0,
                        ["roughnessFactor"] = 1.0,
                    },
                },
            };
            var path = WriteGlb(dir, "metal.glb", root, bin);

            var report = GlbTidier.TidyFile(path);

            Assert.True(report.Changed);
            Assert.Equal(0, report.NormalsGeneratedPrimitives);
            Assert.Equal(1, report.MetallicSanitizedMaterials);

            var (rootBack, _) = ReadGlbBack(path);
            var pbr = Assert.IsType<JsonObject>(
                Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(rootBack["materials"])[0])["pbrMetallicRoughness"]);
            Assert.Equal(0.0, pbr["metallicFactor"]?.GetValue<double>() ?? -1.0, 6);

            // second run must be a byte-identical no-op
            var afterFirst = File.ReadAllBytes(path);
            var second = GlbTidier.TidyFile(path);
            Assert.False(second.Changed);
            Assert.Equal(afterFirst, File.ReadAllBytes(path));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Missing_pbr_object_is_created_with_zero_metallic()
    {
        var dir = TempDir();
        try
        {
            var (root, bin) = BuildTriangleGlb(UpFacingTriangle, TriangleIndices, existingNormals: true);
            root["materials"] = new JsonArray { new JsonObject { ["name"] = "BareMaterial" } };
            var path = WriteGlb(dir, "bare.glb", root, bin);

            var report = GlbTidier.TidyFile(path);

            Assert.Equal(1, report.MetallicSanitizedMaterials);

            var (rootBack, _) = ReadGlbBack(path);
            var material = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(rootBack["materials"])[0]);
            var pbr = Assert.IsType<JsonObject>(material["pbrMetallicRoughness"]);
            Assert.Equal(0.0, pbr["metallicFactor"]?.GetValue<double>() ?? -1.0, 6);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Materials_with_mr_texture_are_left_untouched()
    {
        var dir = TempDir();
        try
        {
            var (root, bin) = BuildTriangleGlb(UpFacingTriangle, TriangleIndices, existingNormals: true);
            root["textures"] = new JsonArray { new JsonObject() };
            root["materials"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "RealPbrExport",
                    ["pbrMetallicRoughness"] = new JsonObject
                    {
                        ["metallicFactor"] = 1.0,
                        ["metallicRoughnessTexture"] = new JsonObject { ["index"] = 0 },
                    },
                },
            };
            var path = WriteGlb(dir, "pbr.glb", root, bin);
            var before = File.ReadAllBytes(path);

            var report = GlbTidier.TidyFile(path);

            Assert.False(report.Changed);
            Assert.Equal(0, report.MetallicSanitizedMaterials);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Normals_and_metallic_fixes_can_land_in_one_run()
    {
        var dir = TempDir();
        try
        {
            var (root, bin) = BuildTriangleGlb(UpFacingTriangle, TriangleIndices);
            root["materials"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "EverythingWrong",
                    ["pbrMetallicRoughness"] = new JsonObject { ["metallicFactor"] = 1.0 },
                },
            };
            var path = WriteGlb(dir, "mixed.glb", root, bin);
            var sizeBefore = new FileInfo(path).Length;

            var report = GlbTidier.TidyFile(path);

            Assert.True(report.Changed);
            Assert.Equal(1, report.NormalsGeneratedPrimitives);
            Assert.Equal(1, report.MetallicSanitizedMaterials);
            Assert.True(report.BytesAfter > sizeBefore);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Indexed_triangle_gets_smooth_unit_normals()
    {
        var dir = TempDir();
        try
        {
            var (root, bin) = BuildTriangleGlb(UpFacingTriangle, TriangleIndices);
            var path = WriteGlb(dir, "indexed.glb", root, bin);

            var report = GlbTidier.TidyFile(path);

            Assert.True(report.Changed);
            Assert.Equal(1, report.NormalsGeneratedPrimitives);
            Assert.Equal(1, report.NormalsGeneratedTriangles);
            Assert.Equal(3, report.NormalsGeneratedVertices);
            Assert.Equal(0, report.SkippedUnsupportedPrimitives);

            var (rootBack, binBack) = ReadGlbBack(path);
            var normals = ReadFirstPrimitiveNormals(rootBack, binBack);
            Assert.NotNull(normals);
            Assert.Equal(9, normals!.Length);
            for (var vertex = 0; vertex < 3; vertex++)
            {
                Assert.Equal(0f, normals[3 * vertex], 4);
                Assert.Equal(0f, normals[3 * vertex + 1], 4);
                Assert.Equal(1f, normals[3 * vertex + 2], 4);
            }

            var buffers = Assert.IsType<JsonArray>(rootBack["buffers"]);
            var buffer = Assert.IsType<JsonObject>(buffers[0]);
            var declared = buffer["byteLength"]?.GetValue<int>() ?? -1;
            Assert.Equal(binBack.Length, declared);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Non_indexed_triangles_get_normals_per_vertex()
    {
        var dir = TempDir();
        try
        {
            // two sequential triangles in the XY plane, all faces facing +Z
            var positions = new float[]
            {
                0, 0, 0, 1, 0, 0, 0, 1, 0,
                1, 0, 0, 1, 1, 0, 0, 1, 0,
            };
            var (root, bin) = BuildTriangleGlb(positions, indices: null);
            var path = WriteGlb(dir, "nonindexed.glb", root, bin);

            var report = GlbTidier.TidyFile(path);

            Assert.True(report.Changed);
            Assert.Equal(2, report.NormalsGeneratedTriangles);
            Assert.Equal(6, report.NormalsGeneratedVertices);
            var (rootBack, binBack) = ReadGlbBack(path);
            var normals = ReadFirstPrimitiveNormals(rootBack, binBack);
            Assert.NotNull(normals);
            Assert.Equal(18, normals!.Length);
            for (var i = 2; i < normals.Length; i += 3)
            {
                Assert.Equal(1f, normals[i], 4);
            }
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Strided_positions_still_get_correct_normals()
    {
        var dir = TempDir();
        try
        {
            var (root, bin) = BuildTriangleGlb(UpFacingTriangle, TriangleIndices, positionStride: 16);
            var path = WriteGlb(dir, "strided.glb", root, bin);

            var report = GlbTidier.TidyFile(path);

            Assert.True(report.Changed);
            var (rootBack, binBack) = ReadGlbBack(path);
            var normals = ReadFirstPrimitiveNormals(rootBack, binBack);
            Assert.NotNull(normals);
            for (var vertex = 0; vertex < 3; vertex++)
            {
                Assert.Equal(1f, normals![3 * vertex + 2], 4);
            }
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Existing_normals_leave_the_file_byte_identical()
    {
        var dir = TempDir();
        try
        {
            var (root, bin) = BuildTriangleGlb(UpFacingTriangle, TriangleIndices, existingNormals: true);
            var path = WriteGlb(dir, "normal.glb", root, bin);
            var before = File.ReadAllBytes(path);

            var report = GlbTidier.TidyFile(path);

            Assert.False(report.Changed);
            Assert.Equal(before.Length, report.BytesAfter);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Unsupported_data_shapes_are_counted_and_skipped_untouched()
    {
        var dir = TempDir();
        try
        {
            // float indices (not an unsigned type) => honest skip, file intact
            var (badIndices, binA) = BuildTriangleGlb(UpFacingTriangle, TriangleIndices, indexComponentType: ComponentFloat);
            var pathA = WriteGlb(dir, "badindices.glb", badIndices, binA);
            var beforeA = File.ReadAllBytes(pathA);
            var reportA = GlbTidier.TidyFile(pathA);
            Assert.False(reportA.Changed);
            Assert.Equal(1, reportA.SkippedUnsupportedPrimitives);
            Assert.Equal(beforeA, File.ReadAllBytes(pathA));

            // line primitive (mode 1) => honest skip, file intact
            var (lines, binB) = BuildTriangleGlb(UpFacingTriangle, TriangleIndices, mode: 1);
            var pathB = WriteGlb(dir, "lines.glb", lines, binB);
            var beforeB = File.ReadAllBytes(pathB);
            var reportB = GlbTidier.TidyFile(pathB);
            Assert.False(reportB.Changed);
            Assert.Equal(1, reportB.SkippedUnsupportedPrimitives);
            Assert.Equal(beforeB, File.ReadAllBytes(pathB));

            // bufferView claiming more bytes than the BIN holds => honest skip
            var (overrun, binC) = BuildTriangleGlb(UpFacingTriangle, TriangleIndices, positionByteLength: 1 << 20);
            var pathC = WriteGlb(dir, "overrun.glb", overrun, binC);
            var beforeC = File.ReadAllBytes(pathC);
            var reportC = GlbTidier.TidyFile(pathC);
            Assert.False(reportC.Changed);
            Assert.Equal(1, reportC.SkippedUnsupportedPrimitives);
            Assert.Equal(beforeC, File.ReadAllBytes(pathC));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Second_run_after_a_fix_is_a_byte_identical_noop()
    {
        var dir = TempDir();
        try
        {
            var (root, bin) = BuildTriangleGlb(UpFacingTriangle, TriangleIndices);
            var path = WriteGlb(dir, "twice.glb", root, bin);

            Assert.True(GlbTidier.TidyFile(path).Changed);
            var afterFirst = File.ReadAllBytes(path);

            var second = GlbTidier.TidyFile(path);

            Assert.False(second.Changed);
            Assert.Equal(afterFirst, File.ReadAllBytes(path));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Corrupt_file_fails_loudly_without_touching_the_file()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "garbage.glb");
            File.WriteAllBytes(path, [0x12, 0x34, 0xAB, 0xCD, 0x00, 0x01, 0x02, 0x03]);

            Assert.Throws<GlbTidyException>(() => GlbTidier.TidyFile(path));
        }
        finally
        {
            TryDelete(dir);
        }
    }
}
