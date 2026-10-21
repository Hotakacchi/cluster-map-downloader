using System.Net;
using System.Text;
using System.Text.Json;
using ClusterMapDownloader;
using ClusterMapDownloader.GuestEntry;
using Xunit;

namespace ClusterMapDownloader.Tests;

/// <summary>
/// Tests for the opt-in guest-entry mode. All HTTP traffic goes to stub handlers;
/// no real network calls, no real key/token literals (synthetic AIza-shaped tokens only).
/// </summary>
public class GuestEntryTests
{
    // Synthetic key shaped like a Firebase web API key (AIza + 35 chars). Not a real key.
    private const string FakeKey = "AIza" + "SyTestKey0123456789ABCDEFGHIJKLMNOP";
    private const string OtherFakeKey = "AIza" + "SyOtherKey123456789ABCDEFGHIJKLMNOP";
    private const string FakeIdToken =
        "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0.eyJzdWIiOiJ0In0.c2lnbmF0dXJlMTIz";

    private const string SignUpResponseJson = $$"""
        {
          "idToken": "{{FakeIdToken}}",
          "refreshToken": "QmFkUmVmcmVzaFRva2Vu",
          "expiresIn": "3600",
          "localId": "local-id-1"
        }
        """;

    private const string SignUpErrorResponseJson = """
        {
          "error": { "message": "OPERATION_NOT_ALLOWED", "status": "INVALID_ARGUMENT" }
        }
        """;

    private const string ClusterErrorResponseJson = """
        {
          "id": "err-1",
          "code": "BAD_TOKEN_EXIST",
          "status": 401,
          "detail": "authentication required"
        }
        """;

    private const string ClusterBadRequestResponseJson = """
        {
          "id": "err-2",
          "code": "BAD_REQUEST_ROOM_ENTER",
          "status": 400,
          "detail": "invalid metrics payload"
        }
        """;

    private const string EntryRequestResponseJson = """
        {
          "roomId": "room_1111",
          "deepLink": "https://example.invalid/room/room_1111"
        }
        """;

    private const string LoginResponseJson = """
        {
          "userId": "u-login-1",
          "isAnonymous": true,
          "displayName": "",
          "shareUrl": "https://cluster.mu/u/u-login-1"
        }
        """;

    private const string RoomEnterResponseJson = """
        {
          "roomId": "room_1111",
          "entryId": "entry_1",
          "worldRoomSetId": "11111111-2222-3333-4444-555555555555",
          "venue": {
            "venueId": "venue_0123456789abcdef01234567",
            "venueKey": "vk",
            "name": "Fixture Venue",
            "revision": 2,
            "unityVersion": "2022.3.50f1",
            "platformResources": {
              "assets": [
                { "id": "assetA", "assetBundleUrl": "https://cdn.example.invalid/b/assetA.bundle" },
                { "id": "assetB", "assetBundleUrl": "https://cdn.example.invalid/b/assetB.bundle" },
                { "id": "assetA-dup", "assetBundleUrl": "https://cdn.example.invalid/b/assetA.bundle" }
              ],
              "mainScene": {
                "assetBundleUrl": "https://cdn.example.invalid/b/mainScene.bundle",
                "assetIdsDependsOn": ["assetA", "assetB"],
                "unitySceneName": "MainScene"
              },
              "subScenes": [
                {
                  "assetBundleUrl": "https://cdn.example.invalid/b/subScene0.bundle",
                  "assetIdsDependsOn": ["assetA"],
                  "unitySceneName": "SubScene0"
                }
              ]
            }
          },
          "unknownRoomField": { "keep": true }
        }
        """;

    // ---------------------------------------------------------------------
    // FirebaseApiKeyLocator
    // ---------------------------------------------------------------------

    [Fact]
    public void Locator_uses_explicit_key_and_reports_command_line_origin()
    {
        var located = FirebaseApiKeyLocator.Locate(FakeKey, null, null);
        Assert.Equal(FirebaseApiKeyOrigin.CommandLine, located.Origin);
        Assert.Equal(FakeKey, located.Value);
        Assert.DoesNotContain(FakeKey, located.Masked);
    }

    [Fact]
    public void Locator_rejects_explicit_key_with_wrong_shape()
    {
        Assert.Throws<FirebaseApiKeyLocatorException>(
            () => FirebaseApiKeyLocator.Locate("not-a-key", null, null));
    }

    [Fact]
    public void Locator_reads_key_from_stringliteral_dump()
    {
        var dumpDir = CreateTempDir();
        try
        {
            var literalPath = Path.Combine(dumpDir, "stringliteral.json");
            File.WriteAllText(literalPath, $$"""
                [
                  { "value": "unrelated", "address": "0x1" },
                  { "value": "{{FakeKey}}", "address": "0x2" }
                ]
                """);

            var located = FirebaseApiKeyLocator.Locate(null, null, dumpDir);
            Assert.Equal(FakeKey, located.Value);
            Assert.Equal(FirebaseApiKeyOrigin.LocalResearchDump, located.Origin);
        }
        finally
        {
            TryDelete(dumpDir);
        }
    }

    [Fact]
    public void Locator_fails_with_clear_instructions_when_nothing_found()
    {
        var emptyDir = CreateTempDir();
        try
        {
            var exception = Assert.Throws<FirebaseApiKeyLocatorException>(
                () => FirebaseApiKeyLocator.Locate(null, emptyDir, emptyDir));
            Assert.Contains("--firebase-api-key", exception.Message);
        }
        finally
        {
            TryDelete(emptyDir);
        }
    }

    [Fact]
    public void Locator_fails_when_multiple_distinct_keys_found()
    {
        var dumpDir = CreateTempDir();
        try
        {
            var literalPath = Path.Combine(dumpDir, "stringliteral.json");
            File.WriteAllText(literalPath, $$"""
                [
                  { "value": "{{FakeKey}}", "address": "0x1" },
                  { "value": "{{OtherFakeKey}}", "address": "0x2" }
                ]
                """);

            var exception = Assert.Throws<FirebaseApiKeyLocatorException>(
                () => FirebaseApiKeyLocator.Locate(null, null, dumpDir));
            Assert.Contains("refusing to guess", exception.Message);
            Assert.DoesNotContain(FakeKey, exception.Message);
            Assert.DoesNotContain(OtherFakeKey, exception.Message);
        }
        finally
        {
            TryDelete(dumpDir);
        }
    }

    [Fact]
    public void Locator_scans_client_package_global_metadata()
    {
        var packageDir = CreateTempDir();
        try
        {
            var dataDir = Path.Combine(packageDir, "cluster_Data");
            Directory.CreateDirectory(dataDir);
            var metadataFile = Path.Combine(dataDir, "global-metadata.dat");
            var binary = Encoding.Latin1.GetBytes("\0\0garbage" + FakeKey + "trailing");
            File.WriteAllBytes(metadataFile, binary);

            var located = FirebaseApiKeyLocator.Locate(null, packageDir, null);
            Assert.Equal(FakeKey, located.Value);
            Assert.Equal(FirebaseApiKeyOrigin.LocalClientPackage, located.Origin);
        }
        finally
        {
            TryDelete(packageDir);
        }
    }

    [Fact]
    public void Locator_scans_unity_resources_assets_when_metadata_lacks_the_key()
    {
        var packageDir = CreateTempDir();
        try
        {
            var dataDir = Path.Combine(packageDir, "cluster_Data");
            Directory.CreateDirectory(dataDir);
            // Observed October 2026 carrier: *_Data/resources.assets (Unity serialized data).
            var resourcesFile = Path.Combine(dataDir, "resources.assets");
            File.WriteAllBytes(resourcesFile, Encoding.Latin1.GetBytes("unity-blob" + FakeKey + "tail"));

            var located = FirebaseApiKeyLocator.Locate(null, packageDir, null);
            Assert.Equal(FakeKey, located.Value);
            Assert.Equal(FirebaseApiKeyOrigin.LocalClientPackage, located.Origin);
        }
        finally
        {
            TryDelete(packageDir);
        }
    }

    // ---------------------------------------------------------------------
    // FirebaseGuestAuthenticator
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Authenticator_performs_anonymous_sign_up_round_trip()
    {
        string? seenQueryString = null;
        var handler = new StubHttpHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            var url = request.RequestUri!.ToString();
            Assert.Contains("/identitytoolkit/v3/relyingparty/signupNewUser?key=", url);
            seenQueryString = request.RequestUri!.Query["?key=".Length..];
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("returnSecureToken", body);
            return Json(SignUpResponseJson);
        });

        using var authenticator = new FirebaseGuestAuthenticator(handler);
        var credential = await authenticator.SignInAnonymouslyAsync(FakeKey);

        Assert.Equal(FakeIdToken, credential.IdToken);
        Assert.Equal("QmFkUmVmcmVzaFRva2Vu", credential.RefreshToken);
        Assert.Equal(3600, credential.ExpiresInSecondsParsed);
        Assert.Equal(FakeKey, seenQueryString);
    }

    [Fact]
    public async Task Authenticator_surfaces_firebase_refusals()
    {
        var handler = new StubHttpHandler(_ => Json(SignUpErrorResponseJson, 400));
        using var authenticator = new FirebaseGuestAuthenticator(handler);

        var exception = await Assert.ThrowsAsync<FirebaseAuthException>(
            () => authenticator.SignInAnonymouslyAsync(FakeKey));
        Assert.Equal(400, exception.StatusCode);
        Assert.Contains("OPERATION_NOT_ALLOWED", exception.Message);
    }

    // ---------------------------------------------------------------------
    // RoomEntryApiClient
    // ---------------------------------------------------------------------

    private static ClusterApiOptions StubApiOptions() => new() { BaseUrl = "https://api.example.invalid/v1" };

    [Fact]
    public async Task Entry_request_uses_world_room_set_route_with_bearer_token()
    {
        string? seenAuth = null;
        var handler = new StubHttpHandler(request =>
        {
            Assert.Equal("/v1/world_room_set/11111111-2222-3333-4444-555555555555/entry_request",
                request.RequestUri!.AbsolutePath);
            seenAuth = request.Headers.TryGetValues("Authorization", out var values) ? values.First() : null;
            return Json(EntryRequestResponseJson);
        });

        using var api = new RoomEntryApiClient(StubApiOptions(), handler);
        var result = await api.CreateWorldRoomSetEntryRequestAsync(
            "11111111-2222-3333-4444-555555555555", "token-from-fixture");

        Assert.Equal("Bearer token-from-fixture", seenAuth);
        Assert.Equal("room_1111", result.RoomId);
    }

    [Fact]
    public async Task Enter_room_sends_metrics_payload_and_parses_platform_resources()
    {
        string? seenPayload = null;
        string? seenAuth = null;
        var handler = new StubHttpHandler(request =>
        {
            Assert.Equal("/v1/rooms/room_1111/enter_v2", request.RequestUri!.AbsolutePath);
            seenAuth = request.Headers.TryGetValues("Authorization", out var values) ? values.First() : null;
            seenPayload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(RoomEnterResponseJson);
        });

        using var api = new RoomEntryApiClient(StubApiOptions(), handler);
        var result = await api.EnterRoomV2Async("room_1111", """{"entryMode":"test"}""", "token-from-fixture");

        Assert.Contains("metricsPayload", seenPayload);
        Assert.Contains("entryMode", seenPayload);
        Assert.Equal("Bearer token-from-fixture", seenAuth);
        Assert.Equal("room_1111", result.RoomId);
        Assert.Equal("venue_0123456789abcdef01234567", result.Venue!.VenueId);

        var resources = result.Venue.PlatformResources!;
        Assert.Equal(3, resources.Assets!.Count);
        Assert.Equal("https://cdn.example.invalid/b/mainScene.bundle", resources.MainScene!.AssetBundleUrl);
        Assert.Equal(new[] { "assetA", "assetB" }, resources.MainScene.AssetIdsDependsOn);
        Assert.Single(resources.SubScenes!);
        Assert.True(result.Extra!.ContainsKey("unknownRoomField"));
    }

    [Fact]
    public async Task Cluster_error_envelope_is_reported_with_code_and_status()
    {
        var handler = new StubHttpHandler(_ => Json(ClusterErrorResponseJson, 401));
        using var api = new RoomEntryApiClient(StubApiOptions(), handler);

        var exception = await Assert.ThrowsAsync<ClusterApiException>(
            () => api.CreateWorldRoomSetEntryRequestAsync("wrs", "token"));

        Assert.Equal(401, exception.StatusCode);
        Assert.Contains("BAD_TOKEN_EXIST", exception.Message);
    }

    // ---------------------------------------------------------------------
    // GuestEntryDeliveryResolver
    // ---------------------------------------------------------------------

    /// <summary>Records the guest-flow requests the stub handler saw.</summary>
    private sealed class GuestFlowFixture
    {
        public List<string> LoginRequests { get; } = [];

        public List<(string Url, string? LoginId, string? Device, string? Platform)> EntryRequests { get; } = [];
    }

    private static string? Header(HttpRequestMessage request, string name)
        => request.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static StubHttpHandler GuestFlowHandler(GuestFlowFixture? fixture = null)
    {
        return new StubHttpHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("signupNewUser", StringComparison.Ordinal))
            {
                return Json(SignUpResponseJson);
            }

            if (url.EndsWith("/v1/login", StringComparison.Ordinal))
            {
                fixture?.LoginRequests.Add(url);
                return Json(LoginResponseJson);
            }

            if (url.EndsWith("/entry_request", StringComparison.Ordinal))
            {
                fixture?.EntryRequests.Add((url,
                    Header(request, "X-Cluster-LoginID"),
                    Header(request, "X-Cluster-Device"),
                    Header(request, "X-Cluster-Platform")));
                return Json(EntryRequestResponseJson);
            }

            if (url.EndsWith("/enter_v2", StringComparison.Ordinal))
            {
                return Json(RoomEnterResponseJson);
            }

            throw new InvalidOperationException($"Unexpected request in fixture: {url}");
        });
    }

    private static WorldMetadata ParseMetadata(string json)
        => JsonSerializer.Deserialize<WorldMetadata>(json, WorldMetadataJson.Options)!;

    private static DeliveryRequest SampleRequest()
    {
        var metadata = ParseMetadata(FixtureData.SampleWorldMetadataJson);
        return new DeliveryRequest(metadata, VenueResolver.Resolve(metadata));
    }

    [Fact]
    public async Task Resolver_walks_the_full_guest_flow_and_maps_assets()
    {
        var handler = GuestFlowHandler();
        var options = new GuestEntryOptions
        {
            FirebaseApiKey = FakeKey,
            Api = StubApiOptions(),
        };
        var resolver = new GuestEntryDeliveryResolver(options, handler);

        var resolution = await resolver.ResolveAsync(SampleRequest());

        Assert.Equal(DeliveryStatus.Available, resolution.Status);
        Assert.Equal(4, resolution.Assets.Count); // duplicate assetA url collapses

        Assert.Equal(new Uri("https://cdn.example.invalid/b/assetA.bundle"), resolution.Assets[0].Url);
        Assert.Equal("bundles/asset-00-assetA.bundle", resolution.Assets[0].RelativeFileName);
        Assert.Equal("bundles/asset-01-assetB.bundle", resolution.Assets[1].RelativeFileName);
        Assert.Equal("bundles/mainScene-00-mainScene.bundle", resolution.Assets[2].RelativeFileName);
        Assert.Equal("bundles/subScene-00-subScene0.bundle", resolution.Assets[3].RelativeFileName);
        Assert.Contains("entry request", resolution.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolver_logs_in_before_room_routes_and_carries_win_platform_headers()
    {
        // Live-verified chain (October 2026): anonymous Firebase sign-up →
        // POST /v1/login (Bearer idToken, empty JSON) → userId becomes the
        // X-Cluster-LoginID header on room routes; room routes also gate on
        // platform, so the guest client overrides the Web defaults with Win.
        var fixture = new GuestFlowFixture();
        var options = new GuestEntryOptions
        {
            FirebaseApiKey = FakeKey,
            Api = StubApiOptions(),
        };
        var resolver = new GuestEntryDeliveryResolver(options, GuestFlowHandler(fixture));

        var resolution = await resolver.ResolveAsync(SampleRequest());

        Assert.Equal(DeliveryStatus.Available, resolution.Status);
        var entry = Assert.Single(fixture.EntryRequests);
        Assert.Equal("u-login-1", entry.LoginId);
        Assert.Equal("Win", entry.Device);
        Assert.Equal("Win", entry.Platform);
        Assert.Single(fixture.LoginRequests);
    }

    [Fact]
    public async Task Resolver_reports_refusal_when_guest_entry_is_rejected()
    {
        var requestedEnter = false;
        var handler = new StubHttpHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("signupNewUser", StringComparison.Ordinal))
            {
                return Json(SignUpResponseJson);
            }

            if (url.EndsWith("/v1/login", StringComparison.Ordinal))
            {
                return Json(LoginResponseJson);
            }

            if (url.EndsWith("/entry_request", StringComparison.Ordinal))
            {
                return Json(EntryRequestResponseJson);
            }

            if (url.EndsWith("/enter_v2", StringComparison.Ordinal))
            {
                requestedEnter = true;
                return Json(ClusterErrorResponseJson, 401);
            }

            throw new InvalidOperationException($"Unexpected request in fixture: {url}");
        });

        var options = new GuestEntryOptions
        {
            FirebaseApiKey = FakeKey,
            Api = StubApiOptions(),
        };
        var resolver = new GuestEntryDeliveryResolver(options, handler);

        var resolution = await resolver.ResolveAsync(SampleRequest());

        Assert.True(requestedEnter);
        Assert.Equal(DeliveryStatus.RequiresNonPublicAccess, resolution.Status);
        Assert.Contains("was refused", resolution.Reason, StringComparison.Ordinal);
        Assert.Contains("BAD_TOKEN_EXIST", resolution.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("token-from-fixture", resolution.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolver_retries_enter_v2_once_with_an_empty_metrics_payload()
    {
        var payloads = new List<string>();
        var handler = new StubHttpHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("signupNewUser", StringComparison.Ordinal))
            {
                return Json(SignUpResponseJson);
            }

            if (url.EndsWith("/v1/login", StringComparison.Ordinal))
            {
                return Json(LoginResponseJson);
            }

            if (url.EndsWith("/entry_request", StringComparison.Ordinal))
            {
                return Json(EntryRequestResponseJson);
            }

            if (url.EndsWith("/enter_v2", StringComparison.Ordinal))
            {
                payloads.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return payloads.Count == 1
                    ? Json(ClusterBadRequestResponseJson, 400)
                    : Json(RoomEnterResponseJson);
            }

            throw new InvalidOperationException($"Unexpected request in fixture: {url}");
        });

        var options = new GuestEntryOptions
        {
            FirebaseApiKey = FakeKey,
            Api = StubApiOptions(),
        };
        var resolver = new GuestEntryDeliveryResolver(options, handler);

        var resolution = await resolver.ResolveAsync(SampleRequest());

        Assert.Equal(DeliveryStatus.Available, resolution.Status);
        Assert.Equal(2, payloads.Count);
        Assert.Contains("entryMode", payloads[0], StringComparison.Ordinal);
        Assert.Contains("\"metricsPayload\":\"\"", payloads[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolver_fails_fast_without_any_key_source()
    {
        var missingDir = Path.Combine(Path.GetTempPath(), "cmd-tests", Guid.NewGuid().ToString("N"));
        var options = new GuestEntryOptions
        {
            Api = StubApiOptions(),
            ClientPackageDirectory = missingDir,
            Il2CppDumpDirectory = missingDir,
        };
        var resolver = new GuestEntryDeliveryResolver(options);

        await Assert.ThrowsAsync<FirebaseApiKeyLocatorException>(
            () => resolver.ResolveAsync(SampleRequest()).AsTask());
    }

    // ---------------------------------------------------------------------
    // SecretMasker
    // ---------------------------------------------------------------------

    [Fact]
    public void Secret_masker_scubs_keys_and_jwt_shaped_values()
    {
        var output = SecretMasker.Mask($"prefix {FakeKey} suffix {FakeIdToken}");
        Assert.DoesNotContain(FakeKey, output);
        Assert.DoesNotContain(FakeIdToken, output);
        Assert.Contains("prefix", output);
        Assert.Contains("suffix", output);
    }

    [Fact]
    public async Task WorldDownloader_downloads_bundles_when_guest_entry_resolver_is_injected()
    {
        byte[] assetBytes = "CLUSTER-BUNDLE-BYTES"u8.ToArray();
        var metadataHandler = HttpResponseFactory.JsonHandler(FixtureData.SampleWorldMetadataJson);

        var handler = new StubHttpHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/v1/worlds/", StringComparison.Ordinal))
            {
                return metadataHandler.Respond(request);
            }

            if (url.Contains("signupNewUser", StringComparison.Ordinal))
            {
                return Json(SignUpResponseJson);
            }

            if (url.EndsWith("/v1/login", StringComparison.Ordinal))
            {
                return Json(LoginResponseJson);
            }

            if (url.EndsWith("/entry_request", StringComparison.Ordinal))
            {
                return Json(EntryRequestResponseJson);
            }

            if (url.EndsWith("/enter_v2", StringComparison.Ordinal))
            {
                return Json(RoomEnterResponseJson);
            }

            throw new InvalidOperationException($"Unexpected request in fixture: {url}");
        });

        // Asset downloads must return raw bytes; route them before the API fixture.
        var routingHandler = new StubHttpHandler(request =>
            request.RequestUri!.Host == "cdn.example.invalid"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(assetBytes),
                }
                : handler.Respond(request));

        var options = FixtureData.Options(handler: routingHandler) with
        {
            DryRun = false,
            DeliveryResolver = new GuestEntryDeliveryResolver(
                new GuestEntryOptions { FirebaseApiKey = FakeKey, Api = StubApiOptions() }, routingHandler),
        };

        var notifier = new RecordingNotifier();
        var result = await WorldDownloader.RunAsync(options, notifier);

        Assert.Equal(WorldDownloadOutcome.Success, result.Outcome);
        Assert.Equal(4, result.Files.Count);
        Assert.All(result.Files, f => Assert.Equal(assetBytes.Length, f.Bytes));
        Assert.Equal("bundles/asset-00-assetA.bundle", result.Files[0].RelativeFileName);
        Assert.True(notifier.Contains("Downloaded bundles/asset-00-assetA.bundle"));
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static HttpResponseMessage Json(string json, int status = 200)
        => new((HttpStatusCode)status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "cmd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
