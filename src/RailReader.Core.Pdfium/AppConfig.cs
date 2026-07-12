using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader.Core;
using RailReader.Core.Models;

namespace RailReader.Core.Services;

public sealed class AppConfig : IRecentFilesStore
{

    /// <summary>
    /// Schema version. Increment when introducing a field change that needs
    /// migration (renames, removed defaults, semantic shifts). Migrations run
    /// once in <see cref="Load"/> and bump this to <see cref="CurrentSchemaVersion"/>.
    /// </summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    internal const int CurrentSchemaVersion = 3;

    public double RailZoomThreshold { get; set; } = 3.0;
    public double SnapDurationMs { get; set; } = 450.0;
    public double ScrollSpeedStart { get; set; } = 14.0;
    public double ScrollSpeedMax { get; set; } = 42.0;
    public double DefaultAutoScrollSpeed => (ScrollSpeedStart + ScrollSpeedMax) / 2.0;
    public double ScrollRampTime { get; set; } = 1.5;
    public int AnalysisLookaheadPages { get; set; } = 2;
    public int BackgroundAnalysisWindowPages { get; set; } = 12;
    public int PageCacheRadius { get; set; } = 24;

    /// <summary>
    /// Render-fidelity preset. Maps to a max-DPI / tier-step pair via
    /// <see cref="RenderDpiSettings.ForPreset"/> in <see cref="ToCoreSettings"/>.
    /// Defaults to <see cref="RenderQuality.Quality"/> — the pre-preset behaviour
    /// (cap 600, tier step 75). Persisted as an integer (mirroring
    /// <see cref="ColourEffect"/>).
    /// </summary>
    public RenderQuality RenderQuality { get; set; } = RenderQuality.Quality;

    /// <summary>Max render DPI used only when <see cref="RenderQuality"/> is <see cref="RenderQuality.Custom"/>.</summary>
    public int CustomMaxRenderDpi { get; set; } = 600;

    /// <summary>Render tier step used only when <see cref="RenderQuality"/> is <see cref="RenderQuality.Custom"/>.</summary>
    public int CustomRenderTierStep { get; set; } = 75;

    public float UiFontScale { get; set; } = 1.25f;
    public ColourEffect ColourEffect { get; set; } = ColourEffect.None;
    public double ColourEffectIntensity { get; set; } = 1.0;
    public bool MotionBlur { get; set; } = true;
    public double MotionBlurIntensity { get; set; } = 0.33;
    public bool PixelSnapping { get; set; } = true;
    public bool LineFocusBlur { get; set; }
    public double LineFocusBlurIntensity { get; set; } = 0.5;
    public double LinePadding { get; set; } = 0.2;
    public double AutoScrollLinePauseMs { get; set; } = 400.0;
    public bool AutoScrollTriggerEnabled { get; set; }
    public double AutoScrollTriggerDelayMs { get; set; } = 2000.0;
    public double JumpPercentage { get; set; } = 25.0;
    public bool DarkMode { get; set; }
    public bool LineHighlightEnabled { get; set; } = true;
    public LineHighlightTint LineHighlightTint { get; set; } = LineHighlightTint.Auto;
    public double LineHighlightOpacity { get; set; } = 0.25;
    public bool MarginCropping { get; set; }
    public double MinimapWidth { get; set; } = 180;
    public double MinimapHeight { get; set; } = 240;
    public double MinimapMarginRight { get; set; } = 10;
    public double MinimapMarginBottom { get; set; } = 10;
    [JsonConverter(typeof(RecentFilesConverter))]
    public List<RecentFileEntry> RecentFiles { get; set; } = [];

    [JsonConverter(typeof(BlockRoleSetConverter))]
    public HashSet<BlockRole> NavigableRoles { get; set; } = new(DefaultRoleSets.Navigable);

    [JsonConverter(typeof(BlockRoleSetConverter))]
    public HashSet<BlockRole> CenteringRoles { get; set; } = new(DefaultRoleSets.Centering);

    /// <summary>Block roles that park semi-automatic auto-scroll on entry (it waits for an
    /// explicit advance keypress). See <see cref="DefaultRoleSets.AutoScrollStop"/>.</summary>
    [JsonConverter(typeof(BlockRoleSetConverter))]
    public HashSet<BlockRole> AutoScrollStopClasses { get; set; } = new(DefaultRoleSets.AutoScrollStop);

    /// <summary>Detect table rows so rail mode can step through them (one line per row)
    /// instead of collapsing a table to a single atomic line. See
    /// <see cref="CoreSettings.TableRowReading"/>.</summary>
    public bool TableRowReading { get; set; } = true;

    /// <summary>Split table rows into navigable cells (requires <see cref="TableRowReading"/>)
    /// so rail mode can step a row cell-by-cell. Off by default. See
    /// <see cref="CoreSettings.CellNavigation"/>.</summary>
    public bool CellNavigation { get; set; }

    // VLM (Vision Language Model) settings for Copy as LaTeX / Markdown / Description
    public string? VlmEndpoint { get; set; }
    public string? VlmModel { get; set; }
    public string? VlmApiKey { get; set; }
    public bool VlmStructuredOutput { get; set; } = false;

    /// <summary>
    /// Build an immutable <see cref="CoreSettings"/> snapshot of the runtime
    /// tuning values. UI-only fields (font scale, dark mode, minimap dimensions,
    /// recent files) are deliberately excluded from the Core contract.
    /// </summary>
    public CoreSettings ToCoreSettings() => new()
    {
        RailZoomThreshold = RailZoomThreshold,
        SnapDurationMs = SnapDurationMs,
        ScrollSpeedStart = ScrollSpeedStart,
        ScrollSpeedMax = ScrollSpeedMax,
        ScrollRampTime = ScrollRampTime,
        AnalysisLookaheadPages = AnalysisLookaheadPages,
        BackgroundAnalysisWindowPages = BackgroundAnalysisWindowPages,
        PageCacheRadius = PageCacheRadius,
        RenderDpi = RenderDpiSettings.ForPreset(RenderQuality, CustomMaxRenderDpi, CustomRenderTierStep),
        LinePadding = LinePadding,
        AutoScrollLinePauseMs = AutoScrollLinePauseMs,
        AutoScrollTriggerEnabled = AutoScrollTriggerEnabled,
        AutoScrollTriggerDelayMs = AutoScrollTriggerDelayMs,
        JumpPercentage = JumpPercentage,
        ColourEffect = ColourEffect,
        ColourEffectIntensity = ColourEffectIntensity,
        MotionBlur = MotionBlur,
        MotionBlurIntensity = MotionBlurIntensity,
        PixelSnapping = PixelSnapping,
        LineFocusBlur = LineFocusBlur,
        LineFocusBlurIntensity = LineFocusBlurIntensity,
        LineHighlightEnabled = LineHighlightEnabled,
        LineHighlightTint = LineHighlightTint,
        LineHighlightOpacity = LineHighlightOpacity,
        MarginCropping = MarginCropping,
        NavigableRoles = NavigableRoles,
        CenteringRoles = CenteringRoles,
        AutoScrollStopClasses = AutoScrollStopClasses,
        TableRowReading = TableRowReading,
        CellNavigation = CellNavigation,
        VlmEndpoint = VlmEndpoint,
        VlmModel = VlmModel,
        VlmApiKey = VlmApiKey,
        VlmStructuredOutput = VlmStructuredOutput,
    };

    /// <summary>Creates an independent deep copy via JSON round-trip.</summary>
    public AppConfig Clone() =>
        JsonSerializer.Deserialize(
            JsonSerializer.Serialize(this, RailReaderJsonContext.Default.AppConfig),
            RailReaderJsonContext.Default.AppConfig)
        ?? new AppConfig();

    private static string? s_configDir;

    /// <summary>Test hook: redirects <see cref="ConfigDir"/> (and everything derived
    /// from it) to <paramref name="dir"/>; pass null to restore auto-detection.</summary>
    internal static void OverrideConfigDirForTesting(string? dir) => s_configDir = dir;

    public static string ConfigDir
    {
        get
        {
            if (s_configDir is not null) return s_configDir;

            string baseDir;
            if (OperatingSystem.IsWindows())
                baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            else if (OperatingSystem.IsMacOS())
                baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support");
            else
                baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

            var dir = Path.Combine(baseDir, "railreader2");
            Directory.CreateDirectory(dir);
            s_configDir = dir;
            return dir;
        }
    }

    public static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public static AppConfig Load()
    {
        if (File.Exists(ConfigPath))
        {
            try
            {
                var json = File.ReadAllText(ConfigPath);
                var loaded = JsonSerializer.Deserialize(json, RailReaderJsonContext.Default.AppConfig) ?? new AppConfig();
                if (Migrate(loaded, json))
                    loaded.Save();
                return loaded;
            }
            catch (Exception ex)
            {
                // The file exists but could not be read/parsed (transient IO error,
                // corrupt JSON, …). Do NOT overwrite it with defaults — the user's
                // recent files, reading positions, role sets and VLM settings may be
                // recoverable. Preserve a copy as config.json.bad for forensics and
                // run this session on in-memory defaults; the original file is only
                // replaced if the user later changes a setting (explicit Save()).
                RailReaderLogging.Logger.Error(
                    "Failed to load config — using defaults for this session (existing config.json left in place)", ex);
                try
                {
                    File.Copy(ConfigPath, ConfigPath + ".bad", overwrite: true);
                }
                catch
                {
                    // Best-effort backup only.
                }
                return new AppConfig();
            }
        }

        // First run: persist the defaults so the file exists for hand-editing.
        var config = new AppConfig();
        config.Save();
        return config;
    }

    /// <summary>
    /// Upgrade a loaded config to <see cref="CurrentSchemaVersion"/>. Returns
    /// true if any migration ran (caller should persist the result). When adding
    /// a new schema version, append a block here that reads old fields and
    /// writes new ones, then bumps <see cref="SchemaVersion"/>.
    /// </summary>
    internal static bool Migrate(AppConfig config, string rawJson)
    {
        if (config.SchemaVersion >= CurrentSchemaVersion) return false;

        // v0/v1 → v2: navigable_classes / centering_classes (PP-DocLayoutV3 string
        // names like "text", "display_formula") are replaced by navigable_roles /
        // centering_roles (BlockRole enum names like "Text", "DisplayMath"). When
        // upgrading, translate the legacy fields via PP-DocLayoutV3's role mapping.
        if (config.SchemaVersion < 2)
        {
            MigrateLegacyClasses(config, rawJson);
        }

        // v2 → v3: tables became navigable by default so rail mode can step through
        // their rows (see CoreSettings.TableRowReading). Inject Table into the
        // persisted navigable set once, so configs written before v3 pick up the
        // feature. This runs only on the v2→v3 upgrade — a user who later removes
        // Table keeps it removed (the config is then at v3 and this block is skipped).
        if (config.SchemaVersion < 3)
        {
            config.NavigableRoles.Add(BlockRole.Table);
        }

        config.SchemaVersion = CurrentSchemaVersion;
        return true;
    }

    private static void MigrateLegacyClasses(AppConfig config, string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (TryReadLegacyClassNames(root, "navigable_classes") is { } nav)
                config.NavigableRoles = nav;
            if (TryReadLegacyClassNames(root, "centering_classes") is { } cen)
                config.CenteringRoles = cen;
        }
        catch (JsonException)
        {
            // Malformed legacy data — fall back to defaults already on the config.
        }
    }

    private static HashSet<BlockRole>? TryReadLegacyClassNames(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;

        var result = new HashSet<BlockRole>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var name = item.GetString();
            if (name is null) continue;
            if (LegacyPPDocLayoutV3NameToRole.TryGetValue(name, out var role))
                result.Add(role);
        }
        return result;
    }

    /// <summary>
    /// Pre-role-based configs persisted PP-DocLayoutV3 class names. This table
    /// translates them to <see cref="BlockRole"/> values for the v1 → v2
    /// migration. The same mapping lives canonically on
    /// <c>PPDocLayoutV3Roles</c> in <c>RailReader.Core.Analysis</c>; duplicated
    /// here because Core.Pdfium deliberately does not reference Core.Analysis
    /// (which would pull ONNX Runtime into the lightweight package).
    /// </summary>
    private static readonly Dictionary<string, BlockRole> LegacyPPDocLayoutV3NameToRole = new()
    {
        ["abstract"] = BlockRole.Text,
        ["algorithm"] = BlockRole.Algorithm,
        ["aside_text"] = BlockRole.Aside,
        ["chart"] = BlockRole.Chart,
        ["content"] = BlockRole.Text,
        ["display_formula"] = BlockRole.DisplayMath,
        ["doc_title"] = BlockRole.Title,
        ["figure_title"] = BlockRole.Caption,
        ["footer"] = BlockRole.Footer,
        ["footer_image"] = BlockRole.Figure,
        ["footnote"] = BlockRole.Footnote,
        ["formula_number"] = BlockRole.Decoration,
        ["header"] = BlockRole.Header,
        ["header_image"] = BlockRole.Figure,
        ["image"] = BlockRole.Figure,
        ["inline_formula"] = BlockRole.InlineMath,
        ["number"] = BlockRole.PageNumber,
        ["paragraph_title"] = BlockRole.Heading,
        ["reference"] = BlockRole.Reference,
        ["reference_content"] = BlockRole.Reference,
        ["seal"] = BlockRole.Decoration,
        ["table"] = BlockRole.Table,
        ["text"] = BlockRole.Text,
        ["vertical_text"] = BlockRole.Text,
        ["vision_footnote"] = BlockRole.Footnote,
    };

    public void AddRecentFile(string filePath)
    {
        EnsureRecentEntry(filePath);
        Save();
    }

    public void SaveReadingPosition(string filePath, int page, double zoom, double offsetX, double offsetY,
        ColourEffect? colourEffect = null)
    {
        EnsureRecentEntry(filePath);
        var entry = RecentFiles[0];
        entry.Page = page;
        entry.Zoom = zoom;
        entry.OffsetX = offsetX;
        entry.OffsetY = offsetY;
        entry.ColourEffect = colourEffect;
        Save();
    }

    private void EnsureRecentEntry(string filePath)
    {
        var idx = RecentFiles.FindIndex(e => e.FilePath == filePath);
        RecentFileEntry entry;
        if (idx >= 0)
        {
            entry = RecentFiles[idx];
            RecentFiles.RemoveAt(idx);
        }
        else
        {
            entry = new RecentFileEntry { FilePath = filePath };
        }
        RecentFiles.Insert(0, entry);
        if (RecentFiles.Count > 10)
            RecentFiles.RemoveRange(10, RecentFiles.Count - 10);
    }

    public RecentFileEntry? GetReadingPosition(string filePath)
    {
        return RecentFiles.Find(e => e.FilePath == filePath);
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, RailReaderJsonContext.Default.AppConfig);
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error("Failed to save config", ex);
        }
    }
}

/// <summary>
/// Handles backward compatibility: deserializes both old-format string arrays
/// and new-format object arrays into List&lt;RecentFileEntry&gt;.
/// </summary>
internal sealed class RecentFilesConverter : JsonConverter<List<RecentFileEntry>>
{
    public override List<RecentFileEntry>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new List<RecentFileEntry>();
        if (reader.TokenType != JsonTokenType.StartArray) return result;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) break;

            if (reader.TokenType == JsonTokenType.String)
            {
                // Old format: plain string path
                var path = reader.GetString();
                if (path is not null)
                    result.Add(new RecentFileEntry { FilePath = path });
            }
            else if (reader.TokenType == JsonTokenType.StartObject)
            {
                // New format: object with file_path, page, zoom, etc.
                var entry = JsonSerializer.Deserialize(ref reader, RailReaderJsonContext.Default.RecentFileEntry);
                if (entry is not null)
                    result.Add(entry);
            }
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, List<RecentFileEntry> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var entry in value)
            JsonSerializer.Serialize(writer, entry, RailReaderJsonContext.Default.RecentFileEntry);
        writer.WriteEndArray();
    }
}

/// <summary>
/// Reads / writes <see cref="HashSet{BlockRole}"/> as a JSON array of enum name
/// strings (e.g. <c>["Text", "DisplayMath"]</c>). Unknown names are silently
/// dropped so future enum additions in older app builds don't crash. Legacy
/// PP-DocLayoutV3 class names (from configs written before role-based settings)
/// are translated in the schema-version migration in <see cref="AppConfig.Migrate"/>.
/// </summary>
internal sealed class BlockRoleSetConverter : JsonConverter<HashSet<BlockRole>>
{
    public override HashSet<BlockRole>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new HashSet<BlockRole>();
        if (reader.TokenType != JsonTokenType.StartArray) return result;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) break;
            if (reader.TokenType == JsonTokenType.String)
            {
                var name = reader.GetString();
                if (name is not null && Enum.TryParse<BlockRole>(name, ignoreCase: false, out var role))
                    result.Add(role);
            }
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, HashSet<BlockRole> value, JsonSerializerOptions options)
    {
        var names = value.Select(r => r.ToString()).OrderBy(n => n);
        writer.WriteStartArray();
        foreach (var name in names)
            writer.WriteStringValue(name);
        writer.WriteEndArray();
    }
}
