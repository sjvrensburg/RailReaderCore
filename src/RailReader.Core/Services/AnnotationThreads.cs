using RailReader.Core.Models;

namespace RailReader.Core.Services;

/// <summary>
/// A comment thread: a root annotation and every annotation that replies to it, directly or
/// through other replies. Threads are <b>flat</b> — <see cref="Replies"/> lists all descendants in
/// one chronological list, however deeply another PDF reader nested them — which is also the shape
/// <see cref="DocumentModel.AddReply"/> produces (a reply to a reply attaches to the thread's root).
/// </summary>
/// <param name="Page">Page the root sits on. Replies are normally on the same page but need not be.</param>
/// <param name="Root">The annotation that opens the thread (it may have no replies).</param>
/// <param name="Replies">Descendants of <paramref name="Root"/> ordered by <see cref="Annotation.CreatedUtc"/>
/// (undated first), ties broken by document order (page, then position on the page). A reply to one of
/// the root's <see cref="Grouped"/> annotations is a reply to the root.</param>
public sealed record AnnotationThread(int Page, Annotation Root, IReadOnlyList<Annotation> Replies)
{
    /// <summary>Annotations grouped with <see cref="Root"/> (<c>/RT /Group</c>, e.g. the strikeout of
    /// Acrobat's Replace Text, grouped to its caret), in document order. They are part of the root,
    /// not replies to it, and are never roots of threads of their own.</summary>
    public IReadOnlyList<Annotation> Grouped { get; init; } = [];
}

/// <summary>
/// Pure reply-thread logic over an <see cref="AnnotationFile"/>: grouping, root lookup and the
/// the reply set that goes with a deleted comment. Annotations link by
/// <see cref="Annotation.NativeId"/> / <see cref="Annotation.InReplyTo"/> strings, so the input may
/// be anything a PDF or sidecar can contain — dangling parents, self-replies, cycles, duplicate ids.
/// Every such case resolves to "treat as a root" rather than throwing or looping.
/// </summary>
public static class AnnotationThreads
{
    /// <summary>
    /// Builds the threads of <paramref name="file"/>, in document order of their roots. With
    /// <paramref name="page"/> only threads whose root is on that page are returned (their replies
    /// are still gathered file-wide).
    /// <para>A root is an annotation with no usable parent: <c>InReplyTo</c> empty, naming itself,
    /// naming no annotation in the file, or part of a cycle (the cycle is broken at its earliest
    /// member in document order, which becomes the root).</para>
    /// <para>A group (<c>/RT /Group</c>) acts as one annotation, its <i>leader</i> — the annotation the
    /// group links to. Members are listed in the leader's <see cref="AnnotationThread.Grouped"/>, not as
    /// threads, and a reply to a member joins the leader's thread.</para>
    /// </summary>
    public static List<AnnotationThread> Build(AnnotationFile file, int? page = null)
    {
        var g = new Graph(file);
        var result = new List<AnnotationThread>();
        for (int i = 0; i < g.Nodes.Count; i++)
        {
            if (g.Parent[i] >= 0 || g.IsMember(i)) continue;
            var (nodePage, ann) = g.Nodes[i];
            if (page is { } p && nodePage != p) continue;

            var descendants = new List<int>();
            var stack = new Stack<int>(g.Children[i]);
            while (stack.Count > 0)
            {
                int n = stack.Pop();
                descendants.Add(n);
                foreach (var c in g.Children[n]) stack.Push(c);
            }
            descendants.Sort((a, b) =>
            {
                int byDate = (g.Nodes[a].Ann.CreatedUtc ?? DateTimeOffset.MinValue)
                    .CompareTo(g.Nodes[b].Ann.CreatedUtc ?? DateTimeOffset.MinValue);
                return byDate != 0 ? byDate : a.CompareTo(b);
            });
            var grouped = new List<Annotation>();
            for (int m = 0; m < g.Nodes.Count; m++)
                if (g.Leader[m] == i) grouped.Add(g.Nodes[m].Ann);
            result.Add(new AnnotationThread(nodePage, ann, descendants.Select(d => g.Nodes[d].Ann).ToList()) { Grouped = grouped });
        }
        return result;
    }

    /// <summary>The root of the thread <paramref name="annotation"/> belongs to — itself when it is
    /// a root or is not in <paramref name="file"/>. A grouped annotation (<c>/RT /Group</c>) belongs to
    /// its group leader's thread.</summary>
    public static Annotation RootOf(AnnotationFile file, Annotation annotation)
    {
        var g = new Graph(file);
        int i = g.IndexOf(annotation);
        if (i < 0) return annotation;
        if (g.IsMember(i)) i = g.Leader[i];
        while (g.Parent[i] >= 0) i = g.Parent[i];
        return g.Nodes[i].Ann;
    }

    /// <summary>The page <paramref name="annotation"/> is on, or null when it is not in the file.</summary>
    public static int? PageOf(AnnotationFile file, Annotation annotation)
    {
        foreach (var (page, list) in file.Pages)
            if (list.Contains(annotation)) return page;
        return null;
    }

    /// <summary>
    /// Everything deleted along with <paramref name="root"/>: every annotation that replies to it,
    /// directly or through other replies, and its whole group (<c>/RT /Group</c>) — a group goes as
    /// one, as in Acrobat, so deleting the strikeout of a Replace Text deletes its caret too (and the
    /// caret's replies). Follows <see cref="Annotation.InReplyTo"/> by id until nothing new turns up, so
    /// cycles and self-replies terminate; <paramref name="root"/> itself is never included.
    /// </summary>
    internal static HashSet<Annotation> Descendants(AnnotationFile file, Annotation root)
    {
        var found = new HashSet<Annotation>(ReferenceEqualityComparer.Instance);
        var g = new Graph(file);
        int at = g.IndexOf(root);
        var start = at >= 0 && g.IsMember(at) ? g.Nodes[g.Leader[at]].Ann : root;
        if (!ReferenceEquals(start, root)) found.Add(start);
        if (string.IsNullOrEmpty(start.NativeId)) return found;

        var ids = new HashSet<string>(StringComparer.Ordinal) { start.NativeId };
        var all = file.Pages.Values.SelectMany(l => l).ToList();
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var ann in all)
            {
                if (ReferenceEquals(ann, root) || ReferenceEquals(ann, start) || found.Contains(ann)) continue;
                if (string.IsNullOrEmpty(ann.InReplyTo) || !ids.Contains(ann.InReplyTo)) continue;
                found.Add(ann);
                grew = true;
                if (!string.IsNullOrEmpty(ann.NativeId)) ids.Add(ann.NativeId);
            }
        }
        return found;
    }

    private sealed class Graph
    {
        public readonly List<(int Page, Annotation Ann)> Nodes = [];
        public readonly int[] Parent;
        /// <summary>For a grouped annotation, the node its group acts as (the first annotation up its
        /// chain of <c>/RT /Group</c> links that is not itself grouped); -1 for any other node.</summary>
        public readonly int[] Leader;
        public readonly List<int>[] Children;
        private readonly Dictionary<Annotation, int> _index = new(ReferenceEqualityComparer.Instance);

        public Graph(AnnotationFile file)
        {
            foreach (var page in file.Pages.Keys.OrderBy(k => k))
                foreach (var ann in file.Pages[page])
                    if (_index.TryAdd(ann, Nodes.Count)) Nodes.Add((page, ann));

            // First annotation wins a duplicated /NM.
            var byId = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < Nodes.Count; i++)
                if (!string.IsNullOrEmpty(Nodes[i].Ann.NativeId)) byId.TryAdd(Nodes[i].Ann.NativeId!, i);

            int Linked(int i) => Nodes[i].Ann.InReplyTo is { Length: > 0 } irt && byId.TryGetValue(irt, out var p) && p != i ? p : -1;

            // A grouped annotation (/RT /Group) is part of its leader, not a reply to it. A dangling
            // group link, or a ring of group links with no leader, leaves the annotation ungrouped.
            Leader = new int[Nodes.Count];
            for (int i = 0; i < Nodes.Count; i++)
            {
                Leader[i] = -1;
                int n = i;
                for (int steps = 0; steps <= Nodes.Count && Nodes[n].Ann.ReplyType == AnnotationReplyType.Group; steps++)
                {
                    int next = Linked(n);
                    if (next < 0) { n = -1; break; }
                    n = next;
                }
                if (n >= 0 && n != i && Nodes[n].Ann.ReplyType != AnnotationReplyType.Group) Leader[i] = n;
            }

            // A group link is never a reply link, so members (and grouped annotations that found no
            // leader) are not threaded; a reply to a member replies to its leader.
            Parent = new int[Nodes.Count];
            for (int i = 0; i < Nodes.Count; i++)
            {
                int p = Nodes[i].Ann.ReplyType == AnnotationReplyType.Group ? -1 : Linked(i);
                if (p >= 0 && IsMember(p)) p = Leader[p];
                Parent[i] = p != i ? p : -1;
            }

            BreakCycles();

            Children = new List<int>[Nodes.Count];
            for (int i = 0; i < Children.Length; i++) Children[i] = [];
            for (int i = 0; i < Nodes.Count; i++)
                if (Parent[i] >= 0) Children[Parent[i]].Add(i);
        }

        public int IndexOf(Annotation a) => _index.TryGetValue(a, out var i) ? i : -1;

        public bool IsMember(int i) => Leader[i] >= 0;

        // Every node has at most one parent, so a cycle is a closed loop of parent links. Cut each at
        // its earliest member in document order — that member becomes a root and the rest hang off it.
        private void BreakCycles()
        {
            var state = new byte[Nodes.Count]; // 0 unseen, 1 on the current walk, 2 settled
            for (int start = 0; start < Nodes.Count; start++)
            {
                if (state[start] != 0) continue;
                var walk = new List<int>();
                int n = start;
                while (n >= 0 && state[n] == 0)
                {
                    state[n] = 1;
                    walk.Add(n);
                    n = Parent[n];
                }
                if (n >= 0 && state[n] == 1)
                {
                    int earliest = n;
                    for (int m = Parent[n]; m != n; m = Parent[m])
                        earliest = Math.Min(earliest, m);
                    Parent[earliest] = -1;
                }
                foreach (var w in walk) state[w] = 2;
            }
        }
    }
}
