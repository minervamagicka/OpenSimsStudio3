using System.Numerics;

namespace OpenSimsStudio3.Formats.Meshes;

/// <summary>A vertex's skinning as a GEOM stores it: four bone indices packed one per byte (low byte first) and four weights.</summary>
public readonly record struct VertexSkin(uint Bones, Vector4 Weights)
{
    public int Bone(int slot) => (int)((Bones >> (8 * slot)) & 0xFF);
    public float Weight(int slot) => slot switch { 0 => Weights.X, 1 => Weights.Y, 2 => Weights.Z, _ => Weights.W };
}

/// <summary>What <see cref="MeshSimplifier.Simplify" /> reads: positions and triangles, plus optional data that steers it.</summary>
/// <param name="Positions">Vertex positions. Vertices at the same position (within <see cref="SimplifyOptions.WeldTolerance" />) are
/// one point of the surface split along a UV or normal seam, as GEOMs store them.</param>
/// <param name="Indices">Triangle list.</param>
public sealed record SimplifierInput(Vector3[] Positions, uint[] Indices)
{
    /// <summary>Per-vertex skinning (empty when the mesh has none). Removing a vertex whose skinning its new neighbours can't
    /// reproduce costs more, so skin transitions keep their vertices longest.</summary>
    public VertexSkin[] Skin { get; init; } = [];

    /// <summary>
    /// Further per-vertex values in metres, <see cref="AttributeStride" /> per vertex (empty for none), e.g. the BGEO morph
    /// deltas. Removing a vertex costs the squared difference between its values and what its new neighbours interpolate.
    /// </summary>
    public float[] Attributes { get; init; } = [];

    public int AttributeStride { get; init; }
}

public sealed record SimplifyOptions
{
    /// <summary>Weight of the skinning error (L1 over bones, 0–2) against the geometric error, in units of the mesh's size squared.</summary>
    public double SkinPenalty { get; init; } = 1e-3;

    /// <summary>Weight of the squared <see cref="SimplifierInput.Attributes" /> error (metres squared, like the geometric error).</summary>
    public double AttributePenalty { get; init; } = 1;

    /// <summary>A collapse is refused when a moved triangle's normal turns by more than this (cosine), from now or from the input.</summary>
    public double MinNormalCosine { get; init; } = 0.2;

    /// <summary>Positions closer than this (metres, per axis) are the same surface point.</summary>
    public float WeldTolerance { get; init; } = 1e-6f;
}

/// <summary>The simplified mesh: every vertex is an input vertex, unchanged.</summary>
/// <param name="SourceVertices">For each output vertex, the input vertex it is (ascending, so the input's order is kept).</param>
/// <param name="Indices">Triangles over the output vertices, in the input's winding.</param>
public sealed record SimplifiedMesh(int[] SourceVertices, uint[] Indices, int TrianglesBefore, int TargetTriangles)
{
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>
/// Mesh simplification by edge collapse ordered by the quadric error metric (M. Garland and P. S. Heckbert, "Surface
/// Simplification Using Quadric Error Metrics", SIGGRAPH 1997), written for OSS3's LOD regeneration. Choices made here:
/// <list type="bullet">
/// <item>Half-edge collapses: a vertex is moved onto a neighbour and removed (the paper's endpoint placement, not the optimal
/// point). The survivor keeps everything it had, so skinning, tangents, colours, UVs and VertexIDs stay exactly as they were
/// on some input vertex: bone weights still sum as before and only use the input's bones, and BGEO morphs still match by ID.</item>
/// <item>Vertices are grouped by position. A seam point (several vertices, one per side) moves only when each of its vertices
/// has exactly one neighbour in the target point, so seams collapse along themselves and are never crossed or opened.</item>
/// <item>Points on an open border or a non-manifold edge never move: a CAS part's border meets other parts (waist, neck,
/// cuffs) and must stay where it is.</item>
/// <item>A collapse is refused when it breaks the link condition (would pinch the surface) or turns a triangle too far
/// (<see cref="SimplifyOptions.MinNormalCosine" />) or makes it degenerate.</item>
/// <item>Quadrics are area-weighted plane sums per point. Skinning and attributes (morph deltas) add a penalty: the difference
/// between the removed vertex's own values and what the triangles left behind interpolate at its position.</item>
/// </list>
/// Deterministic: the same input gives the same output (ties are broken by vertex order).
/// </summary>
public static class MeshSimplifier
{
    const int MaxPasses = 12;

    public static SimplifiedMesh Simplify(SimplifierInput input, double ratio, SimplifyOptions? options = null)
    {
        options ??= new SimplifyOptions();
        if (input.Indices.Length % 3 != 0) throw new ArgumentException("the index count is not a multiple of 3", nameof(input));
        if (input.Skin.Length != 0 && input.Skin.Length != input.Positions.Length) throw new ArgumentException("one skin entry per vertex", nameof(input));
        if (input.Attributes.Length != 0 && (input.AttributeStride <= 0 || input.Attributes.Length != input.AttributeStride * input.Positions.Length))
            throw new ArgumentException("Attributes must hold AttributeStride values per vertex", nameof(input));
        foreach (var i in input.Indices)
            if (i >= input.Positions.Length) throw new ArgumentException($"index {i} out of range", nameof(input));
        return new Run(input, Math.Clamp(ratio, 0, 1), options).Execute();
    }

    /// <summary>A symmetric 4x4 quadric, upper triangle.</summary>
    struct Quadric
    {
        double a2, ab, ac, ad, b2, bc, bd, c2, cd, d2;

        public static Quadric Plane(double a, double b, double c, double d, double w) => new()
        {
            a2 = w * a * a, ab = w * a * b, ac = w * a * c, ad = w * a * d, b2 = w * b * b,
            bc = w * b * c, bd = w * b * d, c2 = w * c * c, cd = w * c * d, d2 = w * d * d,
        };

        public void Add(in Quadric q)
        {
            a2 += q.a2; ab += q.ab; ac += q.ac; ad += q.ad; b2 += q.b2;
            bc += q.bc; bd += q.bd; c2 += q.c2; cd += q.cd; d2 += q.d2;
        }

        public readonly double Error(Vector3 p)
        {
            double x = p.X, y = p.Y, z = p.Z;
            return a2 * x * x + 2 * ab * x * y + 2 * ac * x * z + 2 * ad * x
                 + b2 * y * y + 2 * bc * y * z + 2 * bd * y
                 + c2 * z * z + 2 * cd * z + d2;
        }
    }

    readonly record struct Candidate(int From, int To, int FromStamp, int ToStamp);

    sealed class Run
    {
        readonly SimplifierInput _in;
        readonly SimplifyOptions _opt;
        readonly int _target;
        readonly int[] _tri;             // corners, rewritten as vertices collapse
        readonly bool[] _alive;
        readonly Vector3[] _origNormal;  // unit normal of each input triangle
        readonly List<int>[] _vertTris;  // triangles per vertex (may hold dead ones)
        readonly int[] _group;           // position group of each vertex
        readonly List<int>[] _members;   // vertices of each group (removed vertices leave)
        readonly Vector3[] _groupPos;
        readonly bool[] _locked;
        readonly Quadric[] _q;
        readonly int[] _stamp;
        readonly double _sizeSquared;
        int _aliveCount;

        // Scratch: a marker per group, and the vertex mapping of the collapse being looked at.
        readonly int[] _mark;
        int _markStamp;
        readonly List<(int From, int To)> _mapping = [];

        public Run(SimplifierInput input, double ratio, SimplifyOptions options)
        {
            _in = input;
            _opt = options;
            var n = input.Positions.Length;
            var triCount = input.Indices.Length / 3;
            _target = (int)Math.Ceiling(ratio * triCount);
            _tri = input.Indices.Select(i => (int)i).ToArray();
            _alive = new bool[triCount];
            _origNormal = new Vector3[triCount];
            _vertTris = new List<int>[n];
            for (var v = 0; v < n; v++) _vertTris[v] = [];

            // Position groups, numbered in vertex order.
            _group = new int[n];
            var byKey = new Dictionary<(long, long, long), int>();
            var tol = (double)options.WeldTolerance;
            var groupPos = new List<Vector3>();
            for (var v = 0; v < n; v++)
            {
                var p = input.Positions[v];
                var key = ((long)Math.Round(p.X / tol), (long)Math.Round(p.Y / tol), (long)Math.Round(p.Z / tol));
                if (!byKey.TryGetValue(key, out var g)) { g = groupPos.Count; byKey[key] = g; groupPos.Add(p); }
                _group[v] = g;
            }
            var groups = groupPos.Count;
            _groupPos = groupPos.ToArray();
            _members = new List<int>[groups];
            for (var g = 0; g < groups; g++) _members[g] = [];
            for (var v = 0; v < n; v++) _members[_group[v]].Add(v);
            _q = new Quadric[groups];
            _stamp = new int[groups];
            _locked = new bool[groups];
            _mark = new int[groups];

            var (min, max) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
            foreach (var p in input.Positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            _sizeSquared = n == 0 ? 1 : Math.Max(Vector3.DistanceSquared(min, max), 1e-12);

            // Triangles: input triangles that are degenerate in position (two corners at one point) are dropped.
            var edgeUse = new Dictionary<long, int>();
            for (var t = 0; t < triCount; t++)
            {
                int a = _tri[3 * t], b = _tri[3 * t + 1], c = _tri[3 * t + 2];
                int ga = _group[a], gb = _group[b], gc = _group[c];
                if (ga == gb || gb == gc || ga == gc) continue;
                _alive[t] = true;
                _aliveCount++;
                _vertTris[a].Add(t); _vertTris[b].Add(t); _vertTris[c].Add(t);
                var cross = Vector3.Cross(input.Positions[b] - input.Positions[a], input.Positions[c] - input.Positions[a]);
                var len = cross.Length();
                if (len > 0)
                {
                    var nrm = cross / len;
                    _origNormal[t] = nrm;
                    var d = -Vector3.Dot(nrm, input.Positions[a]);
                    var plane = Quadric.Plane(nrm.X, nrm.Y, nrm.Z, d, 0.5 * len);
                    _q[ga].Add(plane); _q[gb].Add(plane); _q[gc].Add(plane);
                }
                Count(edgeUse, ga, gb); Count(edgeUse, gb, gc); Count(edgeUse, gc, ga);
            }
            // A point on a border (edge used once) or a non-manifold edge (more than twice) is locked.
            foreach (var (key, uses) in edgeUse)
                if (uses != 2) { _locked[(int)(key >> 32)] = true; _locked[(int)(key & 0xFFFFFFFF)] = true; }
        }

        static void Count(Dictionary<long, int> edges, int a, int b)
        {
            var key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            edges[key] = edges.TryGetValue(key, out var c) ? c + 1 : 1;
        }

        public SimplifiedMesh Execute()
        {
            var before = _alive.Length;
            for (var pass = 0; pass < MaxPasses && _aliveCount > _target; pass++)
                if (!Pass()) break;
            return Compact(before);
        }

        /// <summary>One sweep over a fresh queue of every collapse; false when nothing collapsed.</summary>
        bool Pass()
        {
            var queue = new PriorityQueue<Candidate, (double Cost, int From, int To)>();
            for (var g = 0; g < _members.Length; g++)
                foreach (var h in Neighbours(g)) Push(queue, g, h);
            var collapsed = false;
            while (_aliveCount > _target && queue.TryDequeue(out var c, out _))
            {
                if (_stamp[c.From] != c.FromStamp || _stamp[c.To] != c.ToStamp) continue;
                if (!Valid(c.From, c.To)) continue;
                Collapse(c.From, c.To);
                collapsed = true;
                // The 1-ring's triangles changed, so its collapses are re-costed (older entries go stale by stamp).
                var ring = Neighbours(c.To);
                foreach (var h in ring) _stamp[h]++;
                foreach (var h in ring) PushAll(queue, h);
            }
            return collapsed;
        }

        /// <summary>Queues every collapse into and out of <paramref name="g" /> (its stamp just changed, so older ones are stale).</summary>
        void PushAll(PriorityQueue<Candidate, (double, int, int)> queue, int g)
        {
            foreach (var h in Neighbours(g)) { Push(queue, g, h); Push(queue, h, g); }
        }

        void Push(PriorityQueue<Candidate, (double, int, int)> queue, int from, int to)
        {
            if (_locked[from] || !Map(from, to)) return;
            var q = _q[from];
            q.Add(_q[to]);
            var cost = Math.Max(q.Error(_groupPos[to]), 0);
            if (_in.Skin.Length > 0 || _in.Attributes.Length > 0)
            {
                double skin = 0, attr = 0;
                foreach (var (u, v) in _mapping)
                {
                    var (s, a) = InterpolationError(u, v, to);
                    skin = Math.Max(skin, s);
                    attr = Math.Max(attr, a);
                }
                cost += _opt.SkinPenalty * skin * skin * _sizeSquared + _opt.AttributePenalty * attr;
            }
            queue.Enqueue(new Candidate(from, to, _stamp[from], _stamp[to]), (cost, from, to));
        }

        /// <summary>
        /// How far vertex <paramref name="u" />'s own skinning (L1 over bones) and attributes (squared distance) are from what
        /// the triangles left after moving it onto <paramref name="v" /> interpolate at its position. This is what removing
        /// u loses: a skin or morph transition stays sharp because the vertices along it are expensive to remove.
        /// </summary>
        (double Skin, double Attributes) InterpolationError(int u, int v, int to)
        {
            var p = _in.Positions[u];
            var target = _groupPos[to];
            double best = double.MaxValue;
            (int A, int B, int C, float La, float Lb, float Lc) pick = (v, v, v, 1, 0, 0);
            foreach (var t in _vertTris[u])
            {
                if (!_alive[t] || Has(t, to)) continue;
                int a = _tri[3 * t], b = _tri[3 * t + 1], c = _tri[3 * t + 2];
                if (a == u) a = v; else if (b == u) b = v; else c = v;
                Vector3 pa = a == v ? target : _in.Positions[a], pb = b == v ? target : _in.Positions[b], pc = c == v ? target : _in.Positions[c];
                var (la, lb, lc) = Barycentric(p, pa, pb, pc);
                var d = Vector3.DistanceSquared(p, la * pa + lb * pb + lc * pc);
                if (d < best) { best = d; pick = (a, b, c, la, lb, lc); }
            }
            double skin = 0, attr = 0;
            if (_in.Skin.Length > 0)
                skin = SkinError(_in.Skin[u], (_in.Skin[pick.A], pick.La), (_in.Skin[pick.B], pick.Lb), (_in.Skin[pick.C], pick.Lc));
            if (_in.Attributes.Length > 0)
            {
                var stride = _in.AttributeStride;
                for (var k = 0; k < stride; k++)
                {
                    double mixed = pick.La * _in.Attributes[pick.A * stride + k] + pick.Lb * _in.Attributes[pick.B * stride + k] + pick.Lc * _in.Attributes[pick.C * stride + k];
                    var d = _in.Attributes[u * stride + k] - mixed;
                    attr += d * d;
                }
            }
            return (skin, attr);
        }

        /// <summary>Barycentric coordinates of p's projection onto triangle abc, clamped into the triangle.</summary>
        static (float, float, float) Barycentric(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 e0 = b - a, e1 = c - a, ep = p - a;
            float d00 = Vector3.Dot(e0, e0), d01 = Vector3.Dot(e0, e1), d11 = Vector3.Dot(e1, e1);
            float d20 = Vector3.Dot(ep, e0), d21 = Vector3.Dot(ep, e1);
            var den = d00 * d11 - d01 * d01;
            if (den <= 0) return (1, 0, 0);
            float lb = (d11 * d20 - d01 * d21) / den, lc = (d00 * d21 - d01 * d20) / den;
            float la = 1 - lb - lc;
            la = Math.Max(la, 0); lb = Math.Max(lb, 0); lc = Math.Max(lc, 0);
            var sum = la + lb + lc;
            return sum <= 0 ? (1, 0, 0) : (la / sum, lb / sum, lc / sum);
        }

        /// <summary>L1 distance between a vertex's bone→weight map and the barycentric mix of three others'.</summary>
        static double SkinError(VertexSkin own, (VertexSkin Skin, float W) a, (VertexSkin Skin, float W) b, (VertexSkin Skin, float W) c)
        {
            Span<int> bones = stackalloc int[16];
            Span<double> diff = stackalloc double[16];
            var count = 0;
            void Add(Span<int> bones, Span<double> diff, ref int count, VertexSkin skin, double scale)
            {
                for (var s = 0; s < 4; s++)
                {
                    var w = scale * skin.Weight(s);
                    if (w == 0) continue;
                    var bone = skin.Bone(s);
                    var i = 0;
                    while (i < count && bones[i] != bone) i++;
                    if (i == count) { bones[count] = bone; diff[count++] = 0; }
                    diff[i] += w;
                }
            }
            Add(bones, diff, ref count, own, 1);
            Add(bones, diff, ref count, a.Skin, -a.W);
            Add(bones, diff, ref count, b.Skin, -b.W);
            Add(bones, diff, ref count, c.Skin, -c.W);
            double sum = 0;
            for (var i = 0; i < count; i++) sum += Math.Abs(diff[i]);
            return sum;
        }

        /// <summary>The groups sharing a live triangle with <paramref name="g" />, in first-seen order.</summary>
        List<int> Neighbours(int g)
        {
            var result = new List<int>();
            var stamp = ++_markStamp;
            _mark[g] = stamp;
            foreach (var v in _members[g])
                foreach (var t in _vertTris[v])
                {
                    if (!_alive[t]) continue;
                    for (var k = 0; k < 3; k++)
                    {
                        var h = _group[_tri[3 * t + k]];
                        if (_mark[h] == stamp) continue;
                        _mark[h] = stamp;
                        result.Add(h);
                    }
                }
            return result;
        }

        /// <summary>
        /// Fills <see cref="_mapping" />: each vertex of <paramref name="from" /> that has live triangles, paired with its one
        /// neighbour in <paramref name="to" />. False when a vertex has none (the edge crosses a seam) or several.
        /// </summary>
        bool Map(int from, int to)
        {
            _mapping.Clear();
            foreach (var u in _members[from])
            {
                int found = -1, live = 0;
                foreach (var t in _vertTris[u])
                {
                    if (!_alive[t]) continue;
                    live++;
                    for (var k = 0; k < 3; k++)
                    {
                        var w = _tri[3 * t + k];
                        if (_group[w] != to || w == found) continue;
                        if (found >= 0) return false;
                        found = w;
                    }
                }
                if (live == 0) continue;
                if (found < 0) return false;
                _mapping.Add((u, found));
            }
            return _mapping.Count > 0;
        }

        bool Valid(int from, int to)
        {
            if (_locked[from] || !Map(from, to)) return false;

            // Link condition: the points next to both ends must be exactly the third corners of the edge's triangles.
            var near = new HashSet<int>(Neighbours(from));
            var common = 0;
            foreach (var h in Neighbours(to))
                if (h != from && near.Contains(h)) common++;
            var thirds = new HashSet<int>();
            foreach (var u in _members[from])
                foreach (var t in _vertTris[u])
                {
                    if (!_alive[t] || !Has(t, to)) continue;
                    for (var k = 0; k < 3; k++)
                    {
                        var h = _group[_tri[3 * t + k]];
                        if (h != from && h != to) thirds.Add(h);
                    }
                }
            if (common != thirds.Count) return false;

            // Moved triangles must not flip, fold or degenerate.
            var target = _groupPos[to];
            foreach (var (u, _) in _mapping)
                foreach (var t in _vertTris[u])
                {
                    if (!_alive[t] || Has(t, to)) continue;
                    int a = _tri[3 * t], b = _tri[3 * t + 1], c = _tri[3 * t + 2];
                    Vector3 pa = _in.Positions[a], pb = _in.Positions[b], pc = _in.Positions[c];
                    var before = Vector3.Cross(pb - pa, pc - pa);
                    if (a == u) pa = target; else if (b == u) pb = target; else pc = target;
                    var after = Vector3.Cross(pb - pa, pc - pa);
                    var len = after.Length();
                    if (len <= 1e-10 * _sizeSquared) return false;
                    var unit = after / len;
                    var beforeLen = before.Length();
                    if (beforeLen > 0 && Vector3.Dot(before / beforeLen, unit) < _opt.MinNormalCosine) return false;
                    if (_origNormal[t] != Vector3.Zero && Vector3.Dot(_origNormal[t], unit) < _opt.MinNormalCosine) return false;
                }
            return true;
        }

        bool Has(int t, int group) =>
            _group[_tri[3 * t]] == group || _group[_tri[3 * t + 1]] == group || _group[_tri[3 * t + 2]] == group;

        void Collapse(int from, int to)
        {
            foreach (var (u, v) in _mapping)
            {
                foreach (var t in _vertTris[u])
                {
                    if (!_alive[t]) continue;
                    if (Has(t, to)) { _alive[t] = false; _aliveCount--; continue; }
                    for (var k = 0; k < 3; k++) if (_tri[3 * t + k] == u) _tri[3 * t + k] = v;
                    _vertTris[v].Add(t);
                }
                _vertTris[u].Clear();
            }
            _q[to].Add(_q[from]);
            _members[from].Clear();
            _stamp[from]++;
            _stamp[to]++;
        }

        SimplifiedMesh Compact(int before)
        {
            var used = new bool[_in.Positions.Length];
            for (var t = 0; t < _alive.Length; t++)
                if (_alive[t]) { used[_tri[3 * t]] = true; used[_tri[3 * t + 1]] = true; used[_tri[3 * t + 2]] = true; }
            var newIndex = new int[used.Length];
            var source = new List<int>();
            for (var v = 0; v < used.Length; v++)
                if (used[v]) { newIndex[v] = source.Count; source.Add(v); }
            var indices = new List<uint>(3 * _aliveCount);
            for (var t = 0; t < _alive.Length; t++)
                if (_alive[t])
                    for (var k = 0; k < 3; k++) indices.Add((uint)newIndex[_tri[3 * t + k]]);
            return new SimplifiedMesh(source.ToArray(), indices.ToArray(), before, _target);
        }
    }
}
