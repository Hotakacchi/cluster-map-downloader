using ClusterMapDownloader;
using ClusterMapDownloader.GuestEntry;
using Xunit;

namespace ClusterMapDownloader.Tests;

/// <summary>
/// Auto-discovery of the local unpacked cluster client package. Roots are
/// always injected — no test touches the machine's real registry or
/// install directories.
/// </summary>
public class ClientPackageLocatorTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cmd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    [Fact]
    public void LooksLikeClientPackage_requires_unity_data_files()
    {
        var dir = TempDir();
        try
        {
            // Empty directory: not a package.
            Assert.False(ClientPackageLocator.LooksLikeClientPackage(dir));

            // Classic Unity layout: <name>_Data/resources.assets.
            var dataDir = Directory.CreateDirectory(Path.Combine(dir, "cluster_Data"));
            File.WriteAllText(Path.Combine(dataDir.FullName, "resources.assets"), "placeholder");
            Assert.True(ClientPackageLocator.LooksLikeClientPackage(dir));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void LooksLikeClientPackage_accepts_global_metadata_layout()
    {
        var dir = TempDir();
        try
        {
            var metadataDir = Directory.CreateDirectory(Path.Combine(dir, "il2cpp_data", "Metadata"));
            File.WriteAllText(Path.Combine(metadataDir.FullName, "global-metadata.dat"), "placeholder");
            Assert.True(ClientPackageLocator.LooksLikeClientPackage(dir));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Locate_returns_validated_root_when_it_looks_like_a_package()
    {
        var dir = TempDir();
        try
        {
            var dataDir = Directory.CreateDirectory(Path.Combine(dir, "cluster_Data"));
            File.WriteAllText(Path.Combine(dataDir.FullName, "resources.assets"), "placeholder");

            Assert.Equal(dir, ClientPackageLocator.Locate(explicitDirectory: null, [dir]));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Locate_skips_roots_without_package_markers_and_returns_null_when_none_qualify()
    {
        var emptyRoot = TempDir();
        var missingRoot = Path.Combine(TempDir(), "never-created");
        try
        {
            Assert.Null(ClientPackageLocator.Locate(explicitDirectory: null, new[] { emptyRoot, missingRoot }));
        }
        finally
        {
            TryDelete(emptyRoot);
        }
    }

    [Fact]
    public void Locate_explicit_directory_wins_and_is_validated()
    {
        var good = TempDir();
        var other = TempDir();
        try
        {
            var dataDir = Directory.CreateDirectory(Path.Combine(good, "cluster_Data"));
            File.WriteAllText(Path.Combine(dataDir.FullName, "resources.assets"), "placeholder");

            Assert.Equal(good, ClientPackageLocator.Locate(good, [other]));

            // An explicit directory that is not a package fails loudly (null), not silently substituted.
            Assert.Null(ClientPackageLocator.Locate(other, [good]));
        }
        finally
        {
            TryDelete(good);
            TryDelete(other);
        }
    }

    [Fact]
    public void Factory_returns_null_when_no_key_and_nothing_discovered()
    {
        // No key, no dumps, no package: the documented "report options" path.
        Assert.Null(GuestEntryResolverFactory.Create(
            firebaseApiKey: null,
            clientPackageDirectory: Path.Combine(Path.GetTempPath(), "cmd-tests-missing-package"),
            il2CppDumpDirectory: Path.Combine(Path.GetTempPath(), "cmd-tests-missing-dump")));
    }

    [Fact]
    public void Factory_builds_resolver_from_explicit_key_even_without_package()
    {
        var resolver = GuestEntryResolverFactory.Create(
            firebaseApiKey: FakeFirebaseApiKey,
            clientPackageDirectory: Path.Combine(Path.GetTempPath(), "cmd-tests-missing-package"),
            il2CppDumpDirectory: null);
        Assert.NotNull(resolver);
    }

    [Fact]
    public void Factory_builds_resolver_when_dump_directory_exists_implicitly()
    {
        // dump-only discovery: the working-tree .work/il2cpp-dump convention.
        var dump = TempDir();
        try
        {
            var resolver = GuestEntryResolverFactory.Create(
                firebaseApiKey: null,
                clientPackageDirectory: Path.Combine(Path.GetTempPath(), "cmd-tests-missing-package"),
                il2CppDumpDirectory: dump);
            Assert.NotNull(resolver);
        }
        finally
        {
            TryDelete(dump);
        }
    }

    /// <summary>Synthetic key shaped like a Firebase web API key (AIza + 35 chars).
    /// Not a real key; split in source like <c>GuestEntryTests</c> so no
    /// contiguous AIza literal is committed.</summary>
    private const string FakeFirebaseApiKey = "AIza" + "SyTestKey0123456789ABCDEFGHIJKLMNOP";
}
