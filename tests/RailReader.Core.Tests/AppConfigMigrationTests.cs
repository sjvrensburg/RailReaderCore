using System.Text.Json;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class AppConfigMigrationTests
{
    [Fact]
    public void Deserialize_ConfigWithoutAnalysisKnobs_UsesDefaults()
    {
        // A current-schema config saved before the window/cache knobs existed.
        // They are purely additive with defaults, so no migration is needed —
        // the absent keys must deserialize to the property initializers.
        const string json = """
        { "schema_version": 2, "rail_zoom_threshold": 4.0 }
        """;

        var config = JsonSerializer.Deserialize(json, RailReaderJsonContext.Default.AppConfig);

        Assert.NotNull(config);
        Assert.Equal(4.0, config!.RailZoomThreshold);
        Assert.Equal(12, config.BackgroundAnalysisWindowPages);
        Assert.Equal(24, config.PageCacheRadius);
    }

    [Fact]
    public void Migrate_TranslatesLegacyPPDocLayoutNamesToRoles()
    {
        // A v0 config (no schema_version) that uses the pre-role PP-DocLayoutV3
        // string names. The migration must translate them to BlockRole values.
        const string legacyJson = """
        {
          "navigable_classes": ["text", "display_formula", "doc_title"],
          "centering_classes": ["text", "display_formula"]
        }
        """;

        var config = new AppConfig { SchemaVersion = 0 };
        bool migrated = AppConfig.Migrate(config, legacyJson);

        Assert.True(migrated);
        Assert.Equal(AppConfig.CurrentSchemaVersion, config.SchemaVersion);

        Assert.Contains(BlockRole.Text, config.NavigableRoles);
        Assert.Contains(BlockRole.DisplayMath, config.NavigableRoles);
        Assert.Contains(BlockRole.Title, config.NavigableRoles);
        // The cumulative v0 → v3 migration also injects Table (it became navigable
        // by default in v3), so the translated trio plus Table = 4 roles.
        Assert.Contains(BlockRole.Table, config.NavigableRoles);
        Assert.Equal(4, config.NavigableRoles.Count);

        Assert.Contains(BlockRole.Text, config.CenteringRoles);
        Assert.Contains(BlockRole.DisplayMath, config.CenteringRoles);
        Assert.Equal(2, config.CenteringRoles.Count);
    }

    [Fact]
    public void Migrate_OnCurrentSchema_NoOp()
    {
        var config = new AppConfig { SchemaVersion = AppConfig.CurrentSchemaVersion };
        var nav = new HashSet<BlockRole>(config.NavigableRoles);

        bool migrated = AppConfig.Migrate(config, "{}");

        Assert.False(migrated);
        Assert.Equal(nav, config.NavigableRoles);
    }

    [Fact]
    public void Migrate_LegacyJsonWithoutClassFields_KeepsDefaults()
    {
        // Old config that never set custom classes — migration should leave the
        // defaults installed by the constructor untouched.
        var config = new AppConfig { SchemaVersion = 0 };
        var defaults = new HashSet<BlockRole>(config.NavigableRoles);

        AppConfig.Migrate(config, "{}");

        Assert.Equal(defaults, config.NavigableRoles);
    }

    [Fact]
    public void Migrate_UnknownLegacyNamesAreDropped()
    {
        const string legacyJson = """
        {
          "navigable_classes": ["text", "nonsense_class_name", "display_formula"]
        }
        """;

        var config = new AppConfig { SchemaVersion = 0 };
        AppConfig.Migrate(config, legacyJson);

        Assert.Contains(BlockRole.Text, config.NavigableRoles);
        Assert.Contains(BlockRole.DisplayMath, config.NavigableRoles);
        Assert.DoesNotContain(BlockRole.Unknown, config.NavigableRoles);
        // "nonsense_class_name" dropped; the v3 migration adds Table → {Text, DisplayMath, Table}.
        Assert.Contains(BlockRole.Table, config.NavigableRoles);
        Assert.Equal(3, config.NavigableRoles.Count);
    }

    [Fact]
    public void Migrate_V2Config_AddsTableToNavigableRoles()
    {
        // v2 configs were persisted before tables became navigable. The v2→v3
        // migration injects Table once so existing users pick up table-row reading.
        var config = new AppConfig { SchemaVersion = 2 };
        config.NavigableRoles.Remove(BlockRole.Table); // a pre-v3 persisted set lacked it

        bool migrated = AppConfig.Migrate(config, "{}");

        Assert.True(migrated);
        Assert.Equal(AppConfig.CurrentSchemaVersion, config.SchemaVersion);
        Assert.Contains(BlockRole.Table, config.NavigableRoles);
    }

    [Fact]
    public void Migrate_AfterV3Upgrade_UserRemovalOfTableSticks()
    {
        // The "users can still override it" guarantee: once a config is at v3, the
        // injection does not re-run, so a user who removes Table keeps it removed.
        var config = new AppConfig { SchemaVersion = AppConfig.CurrentSchemaVersion };
        config.NavigableRoles.Remove(BlockRole.Table);

        bool migrated = AppConfig.Migrate(config, "{}");

        Assert.False(migrated);
        Assert.DoesNotContain(BlockRole.Table, config.NavigableRoles);
    }
}
