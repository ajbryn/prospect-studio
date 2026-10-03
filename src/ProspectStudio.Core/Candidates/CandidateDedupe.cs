namespace ProspectStudio.Core.Candidates;

/// <summary>
/// Dedupe within a campaign (technical-design §7.2), in rule order: registrable domain, then
/// <c>name_norm</c> plus geohash-7, then Jaro-Winkler ≥ 0.92 within 200 m. The highest-confidence
/// record of a group becomes the lead; the rest are stored as <c>duplicate</c> and keep their source
/// records, so nothing is thrown away.
/// </summary>
/// <remarks>
/// <strong>Matching is transitive.</strong> §7.2: treat the matches as edges and take connected
/// components, so A matching B by domain and B matching C by name collapses all three. Handling pairs
/// independently would make the outcome depend on row order, and the same input could then yield
/// different leads on a re-run - which is exactly what idempotency (NFR-3) forbids.
/// </remarks>
public static class CandidateDedupe
{
    /// <summary>§7.2 rule 3's distance window, in metres.</summary>
    public const double FuzzyRadiusMeters = 200;

    /// <summary>
    /// The side of the blocking cell rule 3 compares within, in degrees. Without the blocking, rule 3
    /// is a full cross join, which is minutes on a metro-sized result.
    /// </summary>
    private const double BlockDegrees = 0.005;

    /// <summary>Metres per degree of latitude; the figure for longitude shrinks with <c>cos(lat)</c>.</summary>
    private const double MetresPerDegree = 111_320;

    /// <summary>
    /// The invariant the blocking has to keep: every cell dimension scanned must cover at least
    /// <see cref="FuzzyRadiusMeters"/>, or a qualifying pair two cells apart is silently missed. A
    /// latitude cell is about 556 m everywhere, so one neighbour each way is always enough; a
    /// <em>longitude</em> cell shrinks towards the poles - about 482 m at 30°N but only 181 m at 71°N -
    /// so the longitude span is computed from the latitude rather than assumed to be one.
    /// </summary>
    private static readonly int _latSpan = (int)Math.Ceiling(FuzzyRadiusMeters / (BlockDegrees * MetresPerDegree));

    /// <summary>
    /// A ceiling on the longitude span, reached only within about 3 m of a pole, where every longitude
    /// converges and no candidate will ever be.
    /// </summary>
    private const int MaxLonSpan = 64;

    private static readonly string[] _ruleOrder = [DedupeRules.Domain, DedupeRules.NamePlace, DedupeRules.Fuzzy];

    /// <summary>
    /// One <see cref="CandidateGroup"/> per surviving company, in no guaranteed order - one per
    /// connected component of the match graph. Deterministic: the same input always produces the same
    /// grouping and the same primaries whatever order the rows arrive in, so a re-run of
    /// <c>find_candidates</c> cannot reshuffle which record is the lead.
    /// </summary>
    public static IReadOnlyList<CandidateGroup> Group(IReadOnlyList<CandidateSite> sites)
    {
        ArgumentNullException.ThrowIfNull(sites);

        if (sites.Count == 0)
        {
            return [];
        }

        var union = new DisjointSet(sites.Count);

        AddDomainEdges(sites, union);
        AddNamePlaceEdges(sites, union);
        AddFuzzyEdges(sites, union);

        var components = new Dictionary<int, List<int>>();
        for (var index = 0; index < sites.Count; index++)
        {
            var root = union.Find(index);
            if (!components.TryGetValue(root, out var members))
            {
                components[root] = members = [];
            }

            members.Add(index);
        }

        var groups = new List<CandidateGroup>(components.Count);
        foreach (var (root, members) in components.OrderBy(entry => entry.Key))
        {
            var ordered = members
                .OrderByDescending(index => sites[index].Confidence)
                .ThenBy(index => sites[index].OvertureId, StringComparer.Ordinal)
                .ToList();

            groups.Add(new CandidateGroup(
                sites[ordered[0]],
                [
                    .. ordered.Skip(1)
                        .OrderBy(index => sites[index].OvertureId, StringComparer.Ordinal)
                        .Select(index => sites[index]),
                ],
                members.Count == 1 ? DedupeRules.None : _ruleOrder[union.EarliestRule(root)]));
        }

        return groups;
    }

    private static void AddDomainEdges(IReadOnlyList<CandidateSite> sites, DisjointSet union)
    {
        var byDomain = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var index = 0; index < sites.Count; index++)
        {
            if (DomainKey.For(sites[index].Website) is not { } domain)
            {
                continue;
            }

            if (byDomain.TryGetValue(domain, out var first))
            {
                union.Union(first, index, rule: 0);
            }
            else
            {
                byDomain[domain] = index;
            }
        }
    }

    private static void AddNamePlaceEdges(IReadOnlyList<CandidateSite> sites, DisjointSet union)
    {
        var byNamePlace = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var index = 0; index < sites.Count; index++)
        {
            var site = sites[index];
            if (site.NameNorm.Length == 0)
            {
                continue;
            }

            var key = $"{site.NameNorm}\u001f{Geohash.Encode(site.Lat, site.Lon)}";
            if (byNamePlace.TryGetValue(key, out var first))
            {
                union.Union(first, index, rule: 1);
            }
            else
            {
                byNamePlace[key] = index;
            }
        }
    }

    private static void AddFuzzyEdges(IReadOnlyList<CandidateSite> sites, DisjointSet union)
    {
        var blocks = new Dictionary<(long Lat, long Lon), List<int>>();

        for (var index = 0; index < sites.Count; index++)
        {
            if (sites[index].NameNorm.Length == 0)
            {
                continue;
            }

            var cell = Cell(sites[index]);
            if (!blocks.TryGetValue(cell, out var members))
            {
                blocks[cell] = members = [];
            }

            members.Add(index);
        }

        foreach (var (cell, members) in blocks)
        {
            foreach (var index in members)
            {
                var lonSpan = LonSpan(sites[index].Lat);

                for (var deltaLat = -_latSpan; deltaLat <= _latSpan; deltaLat++)
                {
                    for (var deltaLon = -lonSpan; deltaLon <= lonSpan; deltaLon++)
                    {
                        if (!blocks.TryGetValue((cell.Lat + deltaLat, cell.Lon + deltaLon), out var neighbours))
                        {
                            continue;
                        }

                        foreach (var other in neighbours)
                        {
                            if (other <= index || !IsFuzzyMatch(sites[index], sites[other]))
                            {
                                continue;
                            }

                            union.Union(index, other, rule: 2);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// How many longitude cells either side have to be scanned so the span covers
    /// <see cref="FuzzyRadiusMeters"/>. Measured at the pole-ward edge of the cell, where a longitude
    /// degree is shortest, so the invariant holds across the whole cell and not just at its centre.
    /// </summary>
    private static int LonSpan(double lat)
    {
        var edge = Math.Min(Math.Abs(lat) + BlockDegrees, 90);
        var metres = BlockDegrees * MetresPerDegree * Math.Cos(double.DegreesToRadians(edge));

        return metres <= 0
            ? MaxLonSpan
            : Math.Min(MaxLonSpan, (int)Math.Ceiling(FuzzyRadiusMeters / metres));
    }

    private static bool IsFuzzyMatch(CandidateSite left, CandidateSite right) =>
        JaroWinkler.Similarity(left.NameNorm, right.NameNorm) >= JaroWinkler.DuplicateThreshold
        && Geohash.DistanceMeters(left.Lat, left.Lon, right.Lat, right.Lon) <= FuzzyRadiusMeters;

    private static (long Lat, long Lon) Cell(CandidateSite site) =>
        ((long)Math.Floor(site.Lat / BlockDegrees), (long)Math.Floor(site.Lon / BlockDegrees));

    /// <summary>
    /// Union-find with the earliest §7.2 rule that contributed an edge to each component, so a group
    /// assembled from edges of more than one kind reports the first rule involved.
    /// </summary>
    private sealed class DisjointSet
    {
        private readonly int[] _parent;
        private readonly int[] _rank;
        private readonly int[] _earliestRule;

        public DisjointSet(int size)
        {
            _parent = new int[size];
            _rank = new int[size];
            _earliestRule = new int[size];

            for (var index = 0; index < size; index++)
            {
                _parent[index] = index;
                _earliestRule[index] = int.MaxValue;
            }
        }

        public int Find(int index)
        {
            while (_parent[index] != index)
            {
                _parent[index] = _parent[_parent[index]];
                index = _parent[index];
            }

            return index;
        }

        public void Union(int left, int right, int rule)
        {
            var a = Find(left);
            var b = Find(right);
            var earliest = Math.Min(rule, Math.Min(_earliestRule[a], _earliestRule[b]));

            if (a == b)
            {
                _earliestRule[a] = earliest;
                return;
            }

            if (_rank[a] < _rank[b])
            {
                (a, b) = (b, a);
            }

            _parent[b] = a;
            if (_rank[a] == _rank[b])
            {
                _rank[a]++;
            }

            _earliestRule[a] = earliest;
        }

        public int EarliestRule(int root) =>
            _earliestRule[root] == int.MaxValue ? 0 : _earliestRule[root];
    }
}
