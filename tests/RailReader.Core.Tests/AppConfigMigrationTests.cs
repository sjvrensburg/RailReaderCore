using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailReader.Core.Tests;

public class AppConfigMigrationTests
{
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
        Assert.Equal(3, config.NavigableRoles.Count);

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
        Assert.Equal(2, config.NavigableRoles.Count);
    }
}
