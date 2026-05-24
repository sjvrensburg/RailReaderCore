namespace RailReader.Core.Models;

public sealed class LayoutBlock
{
    public BBox BBox { get; set; }
    public BlockRole Role { get; set; } = BlockRole.Unknown;
    public int ClassId { get; set; }
    public float Confidence { get; set; }
    public int Order { get; set; }
    public List<LineInfo> Lines { get; set; } = [];
}
