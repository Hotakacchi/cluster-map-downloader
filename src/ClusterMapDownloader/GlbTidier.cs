using System.Buffers.Binary;
using System.Security.Cryptography;
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

    /// <summary>
    /// Materials whose metallicFactor was explicitly set to 0 because they
    /// carry no metallicRoughness texture: glTF's default metallic is 1
    /// (pure metal), which renders black in viewers without an environment
    /// map; Unity's default is dielectric.
    /// </summary>
    public int MetallicSanitizedMaterials { get; init; }

    /// <summary>
    /// Groups of scene nodes whose whole subtree (mesh content + every
    /// descendant) and whose exact world matrix matched another node's, i.e.
    /// perfectly stacked duplicate copies of the same object. Only groups
    /// from which at least one copy was detached are counted.
    /// </summary>
    public int CoincidentDuplicateGroups { get; init; }

    /// <summary>Duplicate nodes detached from their parent (or scene roots).</summary>
    public int CoincidentDuplicateNodesRemoved { get; init; }

    /// <summary>Triangles that were only drawn by the detached copies.</summary>
    public long CoincidentDuplicateTrianglesRemoved { get; init; }

    /// <summary>Duplicate scan skipped because the file has animations (they may pin node indices).</summary>
    public bool CoincidentDuplicateSkippedForAnimations { get; init; }

    /// <summary>Duplicate scan skipped because the file has skins (joints reference node indices).</summary>
    public bool CoincidentDuplicateSkippedForSkins { get; init; }

    /// <summary>Duplicate scan skipped because a referenced accessor/addressable layout could not be resolved.</summary>
    public bool CoincidentDuplicateSkippedUnsupported { get; init; }

    /// <summary>File size in bytes before the run.</summary>
    public long BytesBefore { get; init; }

    /// <summary>File size in bytes after the run (equal to before when unchanged).</summary>
    public long BytesAfter { get; init; }

    /// <summary>True when the file was rewritten; a run with nothing to fix writes nothing.</summary>
    public bool Changed =>
        NormalsGeneratedPrimitives > 0 ||
        MetallicSanitizedMaterials > 0 ||
        CoincidentDuplicateNodesRemoved > 0;
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
/// A second pass fixes the metallic default: every material WITHOUT a
/// metallicRoughness texture is forced to an explicit metallicFactor of 0.
/// glTF's default metallic is 1 (pure metal); with no environment map such
/// materials render black regardless of their base color texture.
///
/// A third pass detaches exact coincident duplicate subtrees: script-driven
/// instancing (vehicles instantiated at runtime) survives bundle export as
/// several node copies with identical content stacked at the same world
/// position, and their coincident faces fight in the depth buffer. One
/// deterministic representative per copy group is kept. See
/// RemoveCoincidentDuplicates for the exact matching rule.
///
/// Deliberately conservative:
///  - triangles primitives only (MODE 4); anything else is counted in
///    SkippedUnsupportedPrimitives and left exactly as exported,
///  - only FLOAT POSITIONs are supported (AssetRipper's own output),
///  - meshes that already have normals are never touched,
///  - duplicate copies must match inside exact double precision (any world
///    drift, however small, keeps the node) and inside whole-subtree content,
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

        // Duplicate-subtree pass runs FIRST so its content digests resolve
        // inside the BIN chunk exactly as parsed here; the normals pass below
        // appends accessors beyond that range and would break the reads.
        var duplicates = RemoveCoincidentDuplicates(root, originalBin);

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

        // Metallic sanity pass: AssetRipper's generic export leaves every
        // material at glTF's default metallicFactor = 1 (pure metal) with no
        // metallicRoughness texture, and a pure metal reflects only the
        // environment map - without one it renders black in three.js and
        // most other consumers. Unity's default is dielectric (metallic 0),
        // and cluster's stylized materials are unlit/basic shaders, so any
        // material without a metallicRoughness texture is forced to an
        // explicit metallicFactor of 0. Materials WITH such a texture are
        // real PBR exports and are never touched.
        var metalSanitized = 0;
        var materials = root["materials"] as JsonArray;
        if (materials is not null)
        {
            for (var materialIndex = 0; materialIndex < materials.Count; materialIndex++)
            {
                if (materials[materialIndex] is not JsonObject material)
                {
                    continue;
                }

                if (material["pbrMetallicRoughness"] is not JsonObject pbr)
                {
                    pbr = new JsonObject();
                    material["pbrMetallicRoughness"] = pbr;
                }

                if (pbr["metallicRoughnessTexture"] is not null)
                {
                    continue;
                }

                if (pbr["metallicFactor"] is JsonValue metalValue &&
                    metalValue.TryGetValue<double>(out var metalFactor) &&
                    Math.Abs(metalFactor) <= 1e-6)
                {
                    continue; // already an explicit 0 - keeps re-runs no-ops
                }

                pbr["metallicFactor"] = 0.0;
                metalSanitized++;
            }
        }

        if (primitivesFixed == 0 && metalSanitized == 0 && duplicates.NodesRemoved == 0)
        {
            return new GlbTidyResult
            {
                File = path,
                SkippedUnsupportedPrimitives = skippedUnsupported,
                CoincidentDuplicateSkippedForAnimations = duplicates.SkippedForAnimations,
                CoincidentDuplicateSkippedForSkins = duplicates.SkippedForSkins,
                CoincidentDuplicateSkippedUnsupported = duplicates.SkippedUnsupported,
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
            MetallicSanitizedMaterials = metalSanitized,
            CoincidentDuplicateGroups = duplicates.Groups,
            CoincidentDuplicateNodesRemoved = duplicates.NodesRemoved,
            CoincidentDuplicateTrianglesRemoved = duplicates.TrianglesRemoved,
            CoincidentDuplicateSkippedForAnimations = duplicates.SkippedForAnimations,
            CoincidentDuplicateSkippedForSkins = duplicates.SkippedForSkins,
            CoincidentDuplicateSkippedUnsupported = duplicates.SkippedUnsupported,
            BytesBefore = before,
            BytesAfter = bytesAfter,
        };
    }

    private sealed record NormalFix(byte[] Blob, int VertexCount, long Triangles, float[] Min, float[] Max);

    // ---------------------------------------------------------------------
    // Pass 3: exact coincident duplicate subtrees
    // ---------------------------------------------------------------------
    // Static scenes rebuilt by AssetRipper can contain the same object twice:
    // cluster.mu scripts instance vehicles at runtime, the placement offsets
    // live in code (not in the bundle), so every clone exports at the origin
    // of its parent chain and the copies stack perfectly - their coincident
    // faces fight in the depth buffer and read as thin marching-ant stripes.
    //
    // Matching rule (deliberately strict):
    //  - nodes are grouped by key = exact world matrix bits + a content
    //    digest of the whole subtree: local TRS/matrix, the mesh primitive
    //    content (accessor buffer bytes hashed with count/stride/type, plus
    //    mode and material raw JSON) and the ordered digests of every child,
    //  - digests are content-based only, so buffer offsets or accessor and
    //    material numbers that differ between re-written copies still match,
    //    while names (which differ freely) are ignored on purpose,
    //  - the world matrix must equal within exact double bits - any drift,
    //    however small, keeps the node (observed duplicates were 0.00 mm),
    //  - groups are processed shallow-first; every non-kept member is
    //    detached from its parent's children array (or the scene roots) with
    //    the lowest node index kept as the deterministic representative.
    //
    // Safety: node objects stay in the nodes array unreferenced (legal glTF),
    // no buffer bytes change, and a run whose remaining copies no longer
    // match finds nothing and rewrites nothing (idempotent). The scan is
    // skipped - and reported honestly - when animations or skins are present
    // (they may pin node indices) or when any referenced accessor/view layout
    // cannot be resolved inside the BIN chunk. Detaching a copy can expose a
    // further identical pair (for example a wrapper whose only held copy got
    // removed turns into another identical empty node), so the scan repeats
    // rounds inside one TidyFile run until no group remains, keeping a single
    // conversion run convergent.
    private sealed record DuplicateScan(
        int Groups,
        int NodesRemoved,
        long TrianglesRemoved,
        bool SkippedForAnimations,
        bool SkippedForSkins,
        bool SkippedUnsupported);

    /// <summary>
    /// Safety cap for the internal convergence loop: removing copies can
    /// expose further pairs (a parent whose only child was a redundant copy
    /// turns into another redundant copy), so the scan repeats until no group
    /// remains. 64 rounds far exceed any legitimate node-chain depth.
    /// </summary>
    private const int MaxDuplicateScanRounds = 64;

    private static DuplicateScan RemoveCoincidentDuplicates(JsonObject root, byte[] bin)
    {
        if (root["animations"] is JsonArray animations && animations.Count > 0)
        {
            return new DuplicateScan(0, 0, 0, true, false, false);
        }

        if (root["skins"] is JsonArray skins && skins.Count > 0)
        {
            return new DuplicateScan(0, 0, 0, false, true, false);
        }

        var total = new DuplicateScan(0, 0, 0, false, false, false);
        for (var round = 0; round < MaxDuplicateScanRounds; round++)
        {
            var roundScan = ScanCoincidentDuplicatesOnce(root, bin);
            total = total with
            {
                Groups = total.Groups + roundScan.Groups,
                NodesRemoved = total.NodesRemoved + roundScan.NodesRemoved,
                TrianglesRemoved = total.TrianglesRemoved + roundScan.TrianglesRemoved,
                SkippedForAnimations = total.SkippedForAnimations || roundScan.SkippedForAnimations,
                SkippedForSkins = total.SkippedForSkins || roundScan.SkippedForSkins,
                SkippedUnsupported = total.SkippedUnsupported || roundScan.SkippedUnsupported,
            };
            if (roundScan.NodesRemoved == 0)
            {
                break;
            }
        }

        return total;
    }

    private static DuplicateScan ScanCoincidentDuplicatesOnce(JsonObject root, byte[] bin)
    {
        if (root["nodes"] is not JsonArray nodesArray ||
            nodesArray.Count == 0 ||
            root["scenes"] is not JsonArray scenesArray ||
            root["meshes"] is not JsonArray meshesArray ||
            root["accessors"] is not JsonArray accessorsArray ||
            root["bufferViews"] is not JsonArray bufferViewsArray)
        {
            return new DuplicateScan(0, 0, 0, false, false, false);
        }

        var nodeCount = nodesArray.Count;
        var meshCount = meshesArray.Count;
        var accCount = accessorsArray.Count;

        var visited = new bool[nodeCount];
        var world = new double[nodeCount][];
        var digest = new string?[nodeCount];
        var subtreeTriangles = new long[nodeCount];
        var depthOf = new int[nodeCount];
        var parentLists = new List<int>[nodeCount];
        var sceneOwners = new List<int>[nodeCount];
        for (var i = 0; i < nodeCount; i++)
        {
            parentLists[i] = new List<int>();
            sceneOwners[i] = new List<int>();
        }

        var meshDigest = new string?[meshCount];
        var meshTriangles = new long[meshCount];
        var meshProbed = new bool[meshCount];
        var meshBroken = new bool[meshCount];

        var accDigest = new string?[accCount];
        var accProbed = new bool[accCount];
        var accBroken = new bool[accCount];

        var unsupported = false;

        bool TryAccessorDigest(int accessorIndex, out string accessorDigestText)
        {
            accessorDigestText = "";
            if (accessorIndex < 0 || accessorIndex >= accCount)
            {
                return false;
            }

            if (accProbed[accessorIndex])
            {
                if (accBroken[accessorIndex])
                {
                    return false;
                }

                accessorDigestText = accDigest[accessorIndex]!;
                return true;
            }

            accProbed[accessorIndex] = true;
            if (accessorsArray[accessorIndex] is not JsonObject accessor)
            {
                accBroken[accessorIndex] = true;
                return false;
            }

            var viewIndex = ReadIndexInt(accessor["bufferView"]);
            if (viewIndex < 0 ||
                viewIndex >= bufferViewsArray.Count ||
                bufferViewsArray[viewIndex] is not JsonObject view)
            {
                accBroken[accessorIndex] = true;
                return false;
            }

            var componentType = ReadIndexInt(accessor["componentType"]);
            var componentSize = componentType switch
            {
                5120 => 1, 5121 => 1, 5122 => 2, 5123 => 2, 5125 => 4, 5126 => 4,
                _ => 0,
            };
            var componentCount = ReadString(accessor["type"]) switch
            {
                "SCALAR" => 1,
                "VEC2" => 2,
                "VEC3" => 3,
                "VEC4" => 4,
                "MAT2" => 4,
                "MAT3" => 9,
                "MAT4" => 16,
                _ => 0,
            };
            var count = ReadIndexInt(accessor["count"]);
            if (componentSize == 0 || componentCount == 0 || count < 0)
            {
                accBroken[accessorIndex] = true;
                return false;
            }

            // GLB files keep the vertex data in the first buffer only.
            if (ReadIndexInt(view["buffer"]) != 0)
            {
                accBroken[accessorIndex] = true;
                return false;
            }

            var viewOffset = ReadOffset(view["byteOffset"]);
            var viewLength = ReadRequiredNonNegative(view["byteLength"]);
            var stride = ReadStride(view["byteStride"]);
            var dataOffset = ReadOffset(accessor["byteOffset"]);
            if (viewOffset < 0 || viewLength <= 0 || stride < 0 || dataOffset < 0)
            {
                accBroken[accessorIndex] = true;
                return false;
            }

            var elementSize = componentSize * componentCount;
            var effectiveStride = stride == 0 ? elementSize : stride;
            if (count == 0)
            {
                accessorDigestText = accDigest[accessorIndex] =
                    "0/" + effectiveStride + "/" + componentType + "/" + ReadString(accessor["type"]) + "/empty";
                return true;
            }

            var dataStart = viewOffset + dataOffset;
            var dataLength = (long)(count - 1) * effectiveStride + elementSize;
            if (dataStart < 0 || dataStart + dataLength > bin.Length)
            {
                accBroken[accessorIndex] = true;
                return false;
            }

            var shaText = Convert.ToHexString(
                SHA256.HashData(bin.AsSpan((int)dataStart, (int)dataLength)));
            accessorDigestText = accDigest[accessorIndex] =
                count + "/" + effectiveStride + "/" + componentType + "/" +
                ReadString(accessor["type"]) + "/" + shaText;
            return true;
        }

        bool TryMeshInfo(int meshIndex, out string meshDigestText, out long meshTriangleCount)
        {
            meshDigestText = "";
            meshTriangleCount = 0;
            if (meshIndex < 0 || meshIndex >= meshCount)
            {
                return false;
            }

            if (meshProbed[meshIndex])
            {
                if (meshBroken[meshIndex])
                {
                    return false;
                }

                meshDigestText = meshDigest[meshIndex]!;
                meshTriangleCount = meshTriangles[meshIndex];
                return true;
            }

            meshProbed[meshIndex] = true;
            if (meshesArray[meshIndex] is not JsonObject mesh ||
                mesh["primitives"] is not JsonArray prims ||
                prims.Count == 0)
            {
                meshBroken[meshIndex] = true;
                return false;
            }

            var meshBuilder = new StringBuilder();
            var totalTriangles = 0L;
            foreach (var primNode in prims)
            {
                if (primNode is not JsonObject prim)
                {
                    meshBroken[meshIndex] = true;
                    return false;
                }

                var mode = ReadIndexInt(prim["mode"]);
                if (mode < 0)
                {
                    mode = ModeTriangles;
                }

                meshBuilder.Append("|m").Append(mode);

                if (prim["material"] is not null)
                {
                    var materialIndex = ReadIndexInt(prim["material"]);
                    if (materialIndex < 0 ||
                        root["materials"] is not JsonArray materials ||
                        materialIndex >= materials.Count ||
                        materials[materialIndex] is not JsonObject material)
                    {
                        meshBroken[meshIndex] = true;
                        return false;
                    }

                    // Raw material JSON: copies that share the same material
                    // content match even when the material index differs.
                    meshBuilder.Append("|mat=").Append(material.ToJsonString());
                }
                else
                {
                    meshBuilder.Append("|mat=-");
                }

                if (prim["attributes"] is not JsonObject attributes || attributes.Count == 0)
                {
                    meshBroken[meshIndex] = true;
                    return false;
                }

                foreach (var attribute in attributes)
                {
                    var attributeAccessor = ReadIndexInt(attribute.Value);
                    if (attributeAccessor < 0 ||
                        !TryAccessorDigest(attributeAccessor, out var attributeDigest))
                    {
                        meshBroken[meshIndex] = true;
                        return false;
                    }

                    meshBuilder.Append("|a:").Append(attribute.Key).Append('=').Append(attributeDigest);
                }

                if (prim["indices"] is not null)
                {
                    var indexAccessor = ReadIndexInt(prim["indices"]);
                    if (indexAccessor < 0 ||
                        !TryAccessorDigest(indexAccessor, out var indexDigest))
                    {
                        meshBroken[meshIndex] = true;
                        return false;
                    }

                    meshBuilder.Append("|ix=").Append(indexDigest);
                    if (mode == ModeTriangles)
                    {
                        if (accessorsArray[indexAccessor] is not JsonObject indicesObject)
                        {
                            meshBroken[meshIndex] = true;
                            return false;
                        }

                        var entries = ReadIndexInt(indicesObject["count"]);
                        if (entries < 0 || entries % 3 != 0)
                        {
                            meshBroken[meshIndex] = true;
                            return false;
                        }

                        totalTriangles += entries / 3;
                    }
                }

                foreach (var property in prim)
                {
                    if (property.Key is "attributes" or "indices" or "mode" or "material")
                    {
                        continue;
                    }

                    meshBuilder
                        .Append("|p:").Append(property.Key)
                        .Append('=').Append(property.Value?.ToJsonString() ?? "null");
                }
            }

            meshDigest[meshIndex] = meshBuilder.ToString();
            meshTriangles[meshIndex] = totalTriangles;
            meshDigestText = meshBuilder.ToString();
            meshTriangleCount = totalTriangles;
            return true;
        }

        bool Visit(int nodeIndex, double[] parentWorld, int level)
        {
            if (unsupported)
            {
                return false;
            }

            if (nodeIndex < 0 || nodeIndex >= nodeCount || visited[nodeIndex])
            {
                unsupported = true; // out of range, cycle or second reference
                return false;
            }

            visited[nodeIndex] = true;
            depthOf[nodeIndex] = level;
            if (nodesArray[nodeIndex] is not JsonObject node ||
                !TryLocalMatrix(node, out var local))
            {
                unsupported = true;
                return false;
            }

            var nodeWorld = MultiplyMatrix(parentWorld, local);
            world[nodeIndex] = nodeWorld;

            // Content digest: own mesh primitives first, then every child's
            // digest in order, so whole subtrees can be compared as text.
            var contentDigest = "-";
            if (node["mesh"] is not null)
            {
                var meshIndex = ReadIndexInt(node["mesh"]);
                if (!TryMeshInfo(meshIndex, out var ownMeshDigest, out var ownTriangles))
                {
                    unsupported = true;
                    return false;
                }

                contentDigest = ownMeshDigest;
                subtreeTriangles[nodeIndex] = ownTriangles;
            }

            // Other node properties that affect rendering (camera, weights,
            // extensions, extras) join the digest; name is the one property
            // copies may freely differ in, so it is excluded on purpose.
            var extrasDigest = "";
            foreach (var property in node)
            {
                if (property.Key is "name" or "children" or "mesh" or
                    "translation" or "rotation" or "scale" or "matrix")
                {
                    continue;
                }

                extrasDigest += property.Key + "=" +
                    (property.Value?.ToJsonString() ?? "null") + ";";
            }

            if (node["children"] is JsonArray childrenList && childrenList.Count > 0)
            {
                // Register every first, then walk: equal digests come from
                // equal subtree content, so the visit order does not matter,
                // but the parent map must be complete before grouping.
                foreach (var childNode in childrenList)
                {
                    var child = ReadIndexInt(childNode);
                    if (child < 0 || child >= nodeCount || visited[child])
                    {
                        unsupported = true;
                        return false;
                    }

                    parentLists[child].Add(nodeIndex);
                }

                foreach (var childNode in childrenList)
                {
                    var child = ReadIndexInt(childNode);
                    if (!Visit(child, nodeWorld, level + 1))
                    {
                        return false;
                    }

                    contentDigest += "|" + digest[child];
                    subtreeTriangles[nodeIndex] += subtreeTriangles[child];
                }
            }

            var nodeBuilder = new StringBuilder(96);
            nodeBuilder.Append("L");
            foreach (var value in local)
            {
                nodeBuilder.Append(BitConverter.DoubleToInt64Bits(value).ToString("X16"));
            }

            nodeBuilder.Append("M").Append(contentDigest);
            nodeBuilder.Append("E").Append(extrasDigest);
            digest[nodeIndex] = nodeBuilder.ToString();
            return true;
        }

        var identity = new double[16];
        identity[0] = identity[5] = identity[10] = identity[15] = 1.0;
        for (var sceneIndex = 0; sceneIndex < scenesArray.Count && !unsupported; sceneIndex++)
        {
            if (scenesArray[sceneIndex] is not JsonObject scene ||
                scene["nodes"] is not JsonArray sceneRoots)
            {
                continue;
            }

            foreach (var rootNode in sceneRoots)
            {
                if (unsupported)
                {
                    break;
                }

                var rootIndex = ReadIndexInt(rootNode);
                if (rootIndex < 0 || rootIndex >= nodeCount)
                {
                    unsupported = true;
                    break;
                }

                sceneOwners[rootIndex].Add(sceneIndex);
                Visit(rootIndex, identity, 0);
            }
        }

        if (unsupported)
        {
            return new DuplicateScan(0, 0, 0, false, false, true);
        }

        // Group visited nodes by (exact world matrix bits + subtree digest).
        var buckets = new Dictionary<string, List<int>>();
        for (var i = 0; i < nodeCount; i++)
        {
            if (!visited[i])
            {
                continue;
            }

            var key = WorldKey(world[i]) + "!" + digest[i];
            if (!buckets.TryGetValue(key, out var bucket))
            {
                buckets[key] = bucket = new List<int>();
            }

            bucket.Add(i);
        }

        var clusters = buckets.Values.Where(bucket => bucket.Count >= 2).ToList();
        // Shallow-first (then lowest index): acting on a top-level copy pair
        // renders deeper pairs inside the removed subtrees moot, which both
        // keeps the triangle accounting non-overlapping and deterministic.
        clusters.Sort((left, right) =>
        {
            var leftDepth = left.Min(member => depthOf[member]);
            var rightDepth = right.Min(member => depthOf[member]);
            if (leftDepth != rightDepth)
            {
                return leftDepth.CompareTo(rightDepth);
            }

            return left.Min().CompareTo(right.Min());
        });

        var removed = new HashSet<int>();
        var actedGroups = 0;
        var nodesRemoved = 0;
        long trianglesRemoved = 0;

        bool CoveredByRemoved(int nodeIndex)
        {
            var cursor = nodeIndex;
            while (true)
            {
                if (removed.Contains(cursor))
                {
                    return true;
                }

                var parents = parentLists[cursor];
                if (parents.Count == 0)
                {
                    return false;
                }

                cursor = parents[0];
            }
        }

        foreach (var cluster in clusters)
        {
            var active = cluster.Where(member => !CoveredByRemoved(member)).ToList();
            if (active.Count < 2)
            {
                continue;
            }

            var keep = active.Min();
            foreach (var member in active)
            {
                if (member == keep)
                {
                    continue;
                }

                foreach (var holder in parentLists[member])
                {
                    if (nodesArray[holder] is JsonObject holderNode &&
                        holderNode["children"] is JsonArray children)
                    {
                        for (var k = children.Count - 1; k >= 0; k--)
                        {
                            if (ReadIndexInt(children[k]) == member)
                            {
                                children.RemoveAt(k);
                            }
                        }
                    }
                }

                foreach (var sceneIndex in sceneOwners[member])
                {
                    if (scenesArray[sceneIndex] is JsonObject sceneNode &&
                        sceneNode["nodes"] is JsonArray sceneRoots)
                    {
                        for (var k = sceneRoots.Count - 1; k >= 0; k--)
                        {
                            if (ReadIndexInt(sceneRoots[k]) == member)
                            {
                                sceneRoots.RemoveAt(k);
                            }
                        }
                    }
                }

                removed.Add(member);
                nodesRemoved++;
                trianglesRemoved += subtreeTriangles[member];
            }

            actedGroups++;
        }

        return new DuplicateScan(actedGroups, nodesRemoved, trianglesRemoved, false, false, false);
    }

    private static string WorldKey(double[] matrix)
    {
        var builder = new StringBuilder(160);
        foreach (var value in matrix)
        {
            builder.Append(BitConverter.DoubleToInt64Bits(value).ToString("X16"));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reads the node's local transform into a column-major 4x4 matrix:
    /// an explicit glTF "matrix", or TRS composed as translation*rotation*scale.
    /// </summary>
    private static bool TryLocalMatrix(JsonObject node, out double[] matrix)
    {
        if (node["matrix"] is JsonArray matrixArray)
        {
            var explicitMatrix = ReadVectorOfLength(matrixArray, 16);
            if (explicitMatrix is null)
            {
                matrix = Array.Empty<double>();
                return false;
            }

            matrix = explicitMatrix;
            return true;
        }

        var translation = new[] { 0.0, 0.0, 0.0 };
        var quaternion = new[] { 0.0, 0.0, 0.0, 1.0 };
        var scale = new[] { 1.0, 1.0, 1.0 };
        if (node["translation"] is not null)
        {
            var parsed = ReadVectorOfLength(node["translation"], 3);
            if (parsed is null)
            {
                matrix = Array.Empty<double>();
                return false;
            }

            translation = parsed;
        }

        if (node["rotation"] is not null)
        {
            var parsed = ReadVectorOfLength(node["rotation"], 4);
            if (parsed is null)
            {
                matrix = Array.Empty<double>();
                return false;
            }

            quaternion = parsed;
        }

        if (node["scale"] is not null)
        {
            var parsed = ReadVectorOfLength(node["scale"], 3);
            if (parsed is null)
            {
                matrix = Array.Empty<double>();
                return false;
            }

            scale = parsed;
        }

        // Column-major TRS composition. The quaternion must be normalised for
        // the rotation matrix to stay orthonormal; exporters already ship it
        // normalised, and a renormalisation makes equal inputs hash equal.
        var x = quaternion[0];
        var y = quaternion[1];
        var z = quaternion[2];
        var w = quaternion[3];
        var norm = Math.Sqrt(x * x + y * y + z * z + w * w);
        if (norm <= 0.0)
        {
            matrix = Array.Empty<double>();
            return false;
        }

        x /= norm;
        y /= norm;
        z /= norm;
        w /= norm;
        var xy2 = 2.0 * x * y;
        var yz2 = 2.0 * y * z;
        var zx2 = 2.0 * z * x;
        var wx2 = 2.0 * w * x;
        var wy2 = 2.0 * w * y;
        var wz2 = 2.0 * w * z;
        var xx2 = 2.0 * x * x;
        var yy2 = 2.0 * y * y;
        var zz2 = 2.0 * z * z;

        var local = new double[16];
        local[0] = 1.0 - (yy2 + zz2);
        local[1] = xy2 + wz2;
        local[2] = zx2 - wy2;
        local[4] = xy2 - wz2;
        local[5] = 1.0 - (xx2 + zz2);
        local[6] = yz2 + wx2;
        local[8] = zx2 + wy2;
        local[9] = yz2 - wx2;
        local[10] = 1.0 - (xx2 + yy2);
        local[15] = 1.0;

        // Scale the three basis columns (column-major); the translation
        // column stays pure translation.
        local[0] *= scale[0];
        local[1] *= scale[0];
        local[2] *= scale[0];
        local[4] *= scale[1];
        local[5] *= scale[1];
        local[6] *= scale[1];
        local[8] *= scale[2];
        local[9] *= scale[2];
        local[10] *= scale[2];
        local[12] = translation[0];
        local[13] = translation[1];
        local[14] = translation[2];
        matrix = local;
        return true;
    }

    private static double[]? ReadVectorOfLength(JsonNode? node, int length)
    {
        if (node is not JsonArray array || array.Count != length)
        {
            return null;
        }

        var parsed = new double[length];
        for (var i = 0; i < length; i++)
        {
            if (array[i] is not JsonValue value || !value.TryGetValue<double>(out parsed[i]))
            {
                return null;
            }
        }

        return parsed;
    }

    /// <summary>Column-major 4x4 multiply: result = a * b.</summary>
    private static double[] MultiplyMatrix(double[] a, double[] b)
    {
        var result = new double[16];
        for (var column = 0; column < 4; column++)
        {
            for (var row = 0; row < 4; row++)
            {
                var sum = 0.0;
                for (var k = 0; k < 4; k++)
                {
                    sum += a[k * 4 + row] * b[column * 4 + k];
                }

                result[column * 4 + row] = sum;
            }
        }

        return result;
    }

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
