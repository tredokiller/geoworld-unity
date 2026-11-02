using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeoWorld.Editor
{
    // The level of the water of an export's rivers, worked out for all of them at once, and shared by the exports of a world.
    //
    // The level used to come from the elevation data under each river's line, smoothed over 40 m (GeoRivers.SmoothLevels), line
    // by line: the noise of the data over longer distances stayed, so a river rose and fell by a metre or more along its length,
    // a river that OpenStreetMap draws as several lines changed its level where one ended and the next began, a stream met the
    // river it flows into at its own height, and a lake that a river only touches (an oxbow, a backwater) kept the level of its
    // own shore: steps of up to two metres where water met water.
    //
    //  - Lines that continue each other (one's start at the other's end) are one chain, levelled together. Where several end at
    //    one start, the widest goes on and the others flow into it.
    //  - Along a chain the level never rises downstream (OpenStreetMap draws a waterway in the direction of its flow). A stream
    //    never stands above the data: the data without its spikes (a median), then the lowest level so far; a bump of the data —
    //    a line that crosses a spur, a bank, a bridge in the elevation data — is cut through, never filled (filling raised
    //    streams by up to 11 m over their banks, and the ground, shaped from the water, rose with them). A river as wide as
    //    GapWidth or wider is the nearest profile that never rises (isotonic regression), at most MaxFill above the data: the
    //    lowest level so far held kilometres of a wide river at the bottom of one dip of its noisy data. Smoothed again, so pools
    //    meet falls without corners, and nowhere steeper than MaxSlope: above a steeper fall the water is cut down to a ramp.
    //  - A chain that meets no river at its last point and climbs along its points is drawn against its flow, and is levelled
    //    the other way.
    //  - Where a chain runs through other water — a lake, or a wider river (a stream's mouth inside the river, a side channel
    //    where it leaves it, a ditch that crosses a canal) — it is that water's level there, so the water has one level: the
    //    surface of rivers takes the level of the nearest line, and a stream's line inside a river at its own level raised a ridge
    //    in the river. Before such water it comes down to its level over the last metres (or is backed up to it when lower), it
    //    is never higher after it, and where it lies more than OutletDrop below it just after it (a dam, a culvert under a canal)
    //    it starts again Separation beyond it. Where an end of a chain only comes near other water (a stream that ends at a
    //    river's bank, one that leaves a pond's shore), it meets it at its level the same way.
    //  - Where meeting other water at its level would stand a chain more than MaxBackwater over its data or cut it more than
    //    MaxCut into it (a stream far below its river, a weir, the wall of a quarry), it stops short of that water instead, with
    //    ground between them.
    //  - A lake that a chain flows through is flat at the chain's level where it leaves the lake (its outlet). A lake that a river
    //    touches without flowing through it takes the river's level where they meet: the river's water where they come within
    //    Separation of each other, else lowered to it or raised by MaxLakeRise at most (its shore lies just above its own level,
    //    and ground lies between them).
    //  - The exports of a world (GeoTiles.Generate) level their rivers in turn with one GeoRiverLevels: where a river was
    //    levelled by an earlier export within that export's ground (the same water within a few metres, flowing the same way), its
    //    levels are kept, and a lake keeps the level the first export gave it, so the water is the same on both sides of an
    //    export's edge. Beyond its own ground an export sees only part of the water (lakes and streams there are not in it), and
    //    its levels there are not kept.
    // The ground is shaped from these levels afterwards (GeoLakes, GeoShore), so beds and banks follow them.
    public sealed class GeoRiverLevels
    {
        // Metres within which a line's end meets another line (its start or a point of it): lines are resampled and moved to the
        // middle of the water, so ends that share a node in OpenStreetMap can lie a few metres apart.
        public const float Meet = 6;
        // Metres from a lake's outline, beyond the river's half width, within which a river touches the lake.
        public const float Touch = 6;
        // Metres over which a chain comes down to the water it meets: so many for each metre of the drop, within the limits.
        public const float FallPerMetre = 40, MinFall = 40, MaxFall = 240;
        // Metres of the median that takes the spikes out of the data, and of the mean that smooths the profile before and after.
        public const float SpikeWindow = 60, SmoothWindow = 20;
        // Metres that water backed up to the water it meets (or brought up to the water it leaves) may stand over its data, and
        // metres a lake is raised at most to a river that touches it across ground.
        public const float MaxBackwater = 1, MaxLakeRise = 0.3f;
        // The steepest fall of the water along a chain (metres for each metre along it).
        public const float MaxSlope = 0.05f;
        // Metres of ground left between a chain that stops short of water and that water (two cells of a 1 km tile of 513
        // nodes), so that their surfaces never meet; a lake as near as this to a river is the river's water.
        public const float Separation = 4;
        // Metres that a chain may lie below water it has run through just after it before it starts beyond that water instead.
        public const float OutletDrop = 0.5f;
        // A chain climbs along its points when the ends of its data (the mean over EndMetres) differ by more than so many metres
        // (and at least ClimbSlope of its length).
        const float EndMetres = 100, Climb = 1, ClimbSlope = 0.0005f;
        // Metres of data over the water beyond which a stream stops at a bump and goes on beyond it, as under a culvert (a road's or
        // a railway's embankment, a line drawn over a spur), instead of a gorge cut through it: no water there and no bed. Only
        // streams narrower than GapWidth: a wide river through a bump is under a bridge, and its bed is cut under it.
        public const float MaxCut = 4, GapWidth = 16;
        // Metres that a river as wide as GapWidth or wider may stand above its data (see Fit).
        public const float MaxFill = 1;
        // A point of an earlier export's river is the same water within this many metres, flowing within 60 degrees of the same way.
        public const float Same = 4;
        const float CellSize = 16;
        // levels nearer than this are the same
        const float Even = 0.001f;

        struct Station { public Vector2 position, direction; public float level; }
        // the levelled rivers of earlier exports, in cells of CellSize metres
        readonly Dictionary<long, List<Station>> stations = new Dictionary<long, List<Station>>();
        // the lakes whose level a river gave (through or touching), by id
        readonly Dictionary<string, float> lakes = new Dictionary<string, float>();

        // What the last Level did, for notes: chains levelled, metres that the water rose downstream before (summed over chains),
        // the largest change of a level, lakes given a river's level, streams brought to the river they flow into, stretches left
        // dry (under bumps, short of water), ends that stop short of water they would meet with a step, and places where the water
        // starts again beyond a dam (or a culvert under other water).
        public int Chains, LakesLevelled, Tributaries, Gaps, Short, Dams;
        public float RiseRemoved, LargestChange, GapMetres;
        // the chains whose level moved more than LargeChange metres off the data, described (diagnostics)
        public readonly List<string> Large = new List<string>();
        public const float LargeChange = 2;
        // What is left after Level (diagnostics): places where water meets water at levels more than Step apart (Unmet of them,
        // the worst WorstUnmet metres off, described in UnmetNotes), and falls steeper than MaxSlope (Steep, the steepest Steepest).
        public int Unmet, Steep;
        public float WorstUnmet, Steepest;
        public readonly List<string> UnmetNotes = new List<string>();
        public const float Step = 0.05f;

        // What an end of a chain meets: a point of another chain (its index there), or a lake (an index into the lakes that Level
        // works with); -1 for neither.
        struct Contact
        {
            public int chain, at, lake;
            public static readonly Contact None = new Contact { chain = -1, at = -1, lake = -1 };
            public bool Any => chain >= 0 || lake >= 0;
        }

        // A run of a chain's samples inside other water: a lake, or the water of a wider chain (at: that chain's nearest sample to
        // each of them).
        sealed class Span
        {
            public int lake = -1, chain = -1, first, last;
            public int[] at;
        }

        sealed class Chain
        {
            public readonly List<int> lines = new List<int>();
            public readonly List<(int line, int sample)> points = new List<(int, int)>();
            // data: the raw levels without their spikes (Fit); tail: samples that come down (or up) to the water the chain meets,
            // never left dry under a bump, or the chain would end short of that water; dry: samples left without water, short of
            // water the chain cannot meet without a step; inside: samples inside other water (a span); pinned: samples at the level
            // of water they are in or end at, which no ramp moves
            public float[] level, raw, half, data;
            public Vector2[] position;
            public bool[] kept, tail, dry, inside, pinned;
            public Rect box;
            public int depth;
            // reversed: drawn against its flow. From Fit until the levels are written its arrays run with the flow (index 0
            // upstream). wide: a river (GapWidth or wider); width: its median width.
            public bool reversed, wide;
            public float width;
            public Contact mouth = Contact.None, source = Contact.None;
            // the other water it runs through, along its flow
            public readonly List<Span> spans = new List<Span>();
            public string notes = "";
        }

        // Levels the rivers of paths (their samples' y: the ground under the line less 0.35 m, smoothed) and the levels of the
        // lakes among water that rivers flow through or touch. lakeLevels: the level of each lake by its id, shared by the
        // exports of a world (GeoTileSource.Prepare); a lake given a river's level here is written back to it. bounds: the
        // export's own ground; only the levels there are kept for later exports (null: all of them).
        public void Level(IList<PathBuild> paths, IList<AreaFeature> water, IList<float> levels, float step, Dictionary<string, float> lakeLevels = null, Rect? bounds = null)
        {
            Chains = LakesLevelled = Tributaries = Gaps = Short = Dams = Unmet = Steep = 0;
            RiseRemoved = LargestChange = GapMetres = WorstUnmet = Steepest = 0;
            Large.Clear(); UnmetNotes.Clear();
            var rivers = new List<PathBuild>();
            if (paths != null) foreach (var p in paths) if (p != null && p.river && p.samples != null && p.samples.Count >= 2) rivers.Add(p);
            if (rivers.Count == 0) return;
            step = Mathf.Max(0.5f, step);
            int count = rivers.Count;
            var grid = SampleGrid(rivers);

            // the line each continues into (the nearest start at its end, the widest of several) and the widest that continues
            // into each; a line that ends at a start where a wider line also ends flows into the line that goes on
            var next = new int[count]; var main = new int[count];
            for (int i = 0; i < count; i++) { next[i] = -1; main[i] = -1; }
            for (int i = 0; i < count; i++)
            {
                Vector2 end = XZ(rivers[i].samples[rivers[i].samples.Count - 1]);
                int best = -1; float bestWidth = -1, bestDistance = float.MaxValue;
                for (int j = 0; j < count; j++)
                {
                    if (j == i) continue;
                    float d = (XZ(rivers[j].samples[0]) - end).magnitude;
                    if (d > Meet) continue;
                    float w = rivers[j].feature.width;
                    if (w > bestWidth + 0.01f || (Mathf.Abs(w - bestWidth) <= 0.01f && d < bestDistance)) { best = j; bestWidth = w; bestDistance = d; }
                }
                next[i] = best;
            }
            for (int i = 0; i < count; i++)
            {
                int j = next[i];
                if (j < 0) continue;
                if (main[j] < 0 || rivers[i].feature.width > rivers[main[j]].feature.width) main[j] = i;
            }

            // chains: from every line that no wider line continues into, along its continuations
            var chains = new List<Chain>(); var chainOf = new int[count]; var indexIn = new List<int>[count];
            for (int i = 0; i < count; i++) chainOf[i] = -1;
            for (int head = 0; head < count; head++)
            {
                if (main[head] >= 0 || chainOf[head] >= 0) continue;
                chains.Add(Build(rivers, head, next, main, chainOf, indexIn, chains.Count));
            }
            // closed loops (a braided channel drawn round an island): each loop from its first line
            for (int i = 0; i < count; i++) if (chainOf[i] < 0) chains.Add(Build(rivers, i, next, main, chainOf, indexIn, chains.Count));

            // the lakes: still water that rivers flow through or touch (a river's own water area follows its line, GeoWater)
            var lakeIndices = new List<int>(); var boxes = new List<Rect>();
            if (water != null && levels != null)
                for (int i = 0; i < water.Count && i < levels.Count; i++)
                {
                    if (water[i]?.rings == null || GeoWater.Flowing(water[i])) continue;
                    var box = Box(water[i].rings);
                    if (box.width <= 0) continue;
                    lakeIndices.Add(i); boxes.Add(box);
                }
            int lakeCount = lakeIndices.Count;

            // what each end of a chain meets (its first point and its last, along its points): the line it flows into (a point of
            // it, or the start of a wider line it ends at), a line it leaves, a lake it lies in or at
            var heads = new Contact[chains.Count]; var ends = new Contact[chains.Count];
            for (int c = 0; c < chains.Count; c++)
            {
                var chain = chains[c];
                int last = chain.lines[chain.lines.Count - 1], n = chain.level.Length;
                ends[c] = next[last] < 0 ? Nearest(rivers, chains, grid, chainOf, indexIn, c, chain.position[n - 1])
                        : chainOf[next[last]] != c ? new Contact { chain = chainOf[next[last]], at = indexIn[next[last]][0], lake = -1 } : Contact.None;
                heads[c] = Nearest(rivers, chains, grid, chainOf, indexIn, c, chain.position[0]);
                ends[c] = WithLake(ends[c], chain.position[n - 1], chain.half[n - 1], water, lakeIndices, boxes, chains);
                heads[c] = WithLake(heads[c], chain.position[0], chain.half[0], water, lakeIndices, boxes, chains);
            }

            // the levels of earlier exports where this export has the same water
            foreach (var chain in chains) Keep(chain);

            // the profile of each chain, with its flow: one that meets no river at its last point and climbs along its points is
            // drawn against its flow, and from here on every chain's arrays run with the flow
            for (int c = 0; c < chains.Count; c++)
            {
                var chain = chains[c];
                chain.reversed = ends[c].chain < 0 && Climbs(chain, step);
                if (chain.reversed) Turn(chain);
                chain.mouth = chain.reversed ? heads[c] : ends[c];
                chain.source = chain.reversed ? ends[c] : heads[c];
                Fit(chain, step);
                Chains++;
            }
            foreach (var chain in chains) { chain.mouth = WithFlow(chain.mouth, chains); chain.source = WithFlow(chain.source, chains); }

            // the other water each chain runs through: the lakes (its samples inside each, from the first to the last), and the
            // water of wider chains (its samples within their half width of their line, in runs)
            for (int c = 0; c < chains.Count; c++)
            {
                var chain = chains[c];
                for (int k = 0; k < lakeCount; k++)
                {
                    if (!boxes[k].Overlaps(chain.box)) continue;
                    int first = -1, last = -1;
                    for (int s = 0; s < chain.level.Length; s++)
                    {
                        var p = chain.position[s];
                        if (!boxes[k].Contains(p) || !GeoData.InPolygon(p.x, p.y, water[lakeIndices[k]].rings)) continue;
                        if (first < 0) first = s;
                        last = s;
                    }
                    if (first >= 0) chain.spans.Add(new Span { lake = k, first = first, last = last });
                }
                Span open = null; var at = new List<int>();
                for (int s = 0; s <= chain.level.Length; s++)
                {
                    int other = -1, sample = -1;
                    if (s < chain.level.Length) InWater(rivers, chains, grid, chainOf, indexIn, c, chain.position[s], out other, out sample);
                    if (open != null && other != open.chain)
                    {
                        open.last = s - 1; open.at = at.ToArray(); chain.spans.Add(open); open = null;
                    }
                    if (other < 0) continue;
                    if (open == null) { open = new Span { chain = other, first = s }; at.Clear(); }
                    at.Add(sample);
                }
                chain.spans.Sort((a, b) => a.first.CompareTo(b.first));
                foreach (var span in chain.spans) for (int s = span.first; s <= span.last; s++) chain.inside[s] = true;
            }

            // where a lake's level comes from: a chain through it with levels an earlier export kept inside it (the lake has that
            // level in the earlier export's tiles too), else the widest chain through it that flows on after it (its outlet sets the
            // level), else the widest that ends in it, else the river that touches it (its sample nearest the lake's outline)
            var setter = new int[lakeCount]; var touchAt = new int[lakeCount]; var touchGap = new float[lakeCount]; var rank = new float[lakeCount];
            for (int k = 0; k < lakeCount; k++) { setter[k] = -1; touchAt[k] = -1; }
            for (int c = 0; c < chains.Count; c++)
                foreach (var span in chains[c].spans)
                {
                    if (span.lake < 0) continue;
                    float r = chains[c].width + (span.last < chains[c].level.Length - 1 ? 1e6f : 0) + (KeptIn(chains[c], span) >= 0 ? 2e6f : 0);
                    if (setter[span.lake] >= 0 && r <= rank[span.lake]) continue;
                    setter[span.lake] = c; rank[span.lake] = r;
                }
            for (int k = 0; k < lakeCount; k++)
            {
                if (setter[k] >= 0) continue;
                var rings = water[lakeIndices[k]].rings; var box = boxes[k];
                float best = float.MaxValue;
                for (int c = 0; c < chains.Count; c++)
                {
                    var chain = chains[c];
                    for (int s = 0; s < chain.level.Length; s++)
                    {
                        var p = chain.position[s]; float reach = chain.half[s] + Touch;
                        if (p.x < box.xMin - reach || p.x > box.xMax + reach || p.y < box.yMin - reach || p.y > box.yMax + reach) continue;
                        float gap = OutlineDistance(p, rings) - chain.half[s];
                        if (gap > Touch || gap >= best) continue;
                        best = gap; setter[k] = c; touchAt[k] = s; touchGap[k] = gap;
                    }
                }
            }

            // no fall steeper than MaxSlope (inside other water the chain takes that water's level later)
            foreach (var chain in chains) Ramp(chain);

            // the order in which chains meet the water they meet: after that water (the river a chain flows into, leaves or runs
            // through, the chain a lake's level comes from), the widest first among the rest
            for (int iteration = 0; iteration < chains.Count; iteration++)
            {
                bool changed = false;
                for (int c = 0; c < chains.Count; c++)
                {
                    var targets = new List<int>();
                    foreach (var contact in new[] { chains[c].mouth, chains[c].source })
                        targets.Add(contact.chain >= 0 ? contact.chain : contact.lake >= 0 ? setter[contact.lake] : -1);
                    foreach (var span in chains[c].spans) targets.Add(span.chain >= 0 ? span.chain : setter[span.lake]);
                    foreach (int t in targets)
                    {
                        if (t < 0 || t == c || chains[c].depth > chains[t].depth || chains[t].depth >= chains.Count) continue;
                        chains[c].depth = chains[t].depth + 1; changed = true;
                    }
                }
                if (!changed) break;
            }
            var order = new List<int>(); for (int c = 0; c < chains.Count; c++) order.Add(c);
            order.Sort((a, b) => chains[a].depth != chains[b].depth ? chains[a].depth.CompareTo(chains[b].depth) : chains[b].width.CompareTo(chains[a].width));

            // lakes an earlier export gave a river's level keep it; the others start from their own (their shore's)
            var preset = new bool[lakeCount]; var own = new float[lakeCount];
            for (int k = 0; k < lakeCount; k++)
            {
                string id = water[lakeIndices[k]].id;
                if (!string.IsNullOrEmpty(id) && lakes.TryGetValue(id, out float earlier)) { preset[k] = true; levels[lakeIndices[k]] = earlier; }
                own[k] = levels[lakeIndices[k]];
            }

            // a lake's level from the chain that leaves it: the chain's level SmoothWindow inside its outlet, where the smoothing
            // has not spread a fall below the outlet (a dam's) into the lake
            int inset = Mathf.RoundToInt(SmoothWindow / step);

            // each chain runs through other water at its level and meets the water at its ends, and gives lakes theirs; twice, the
            // second time with the levels of the water that chains after it changed (what it was pinned to the first time counts no
            // more)
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (int c in order)
                {
                    var chain = chains[c];
                    System.Array.Clear(chain.pinned, 0, chain.pinned.Length);
                    // the water it runs through first, along its flow, then its ends where they only come near other water: an end
                    // that cannot meet that water without the chain rising downstream past water it runs through stops short of it
                    foreach (var span in chain.spans)
                    {
                        if (span.lake >= 0)
                        {
                            int k = span.lake;
                            if (setter[k] == c && !preset[k])
                            {
                                int kept = KeptIn(chain, span);
                                float level = chain.level[kept >= 0 ? kept : Mathf.Max(span.first, span.last - inset)];
                                SetLake(lakeIndices[k], level, water, levels, lakeLevels);
                                if (pass == 0) { LakesLevelled++; chain.notes += $" through {water[lakeIndices[k]].id} at {level:F1}"; }
                            }
                            float lake = levels[lakeIndices[k]];
                            Through(chain, span, s => lake, NearWater(new Contact { chain = -1, at = -1, lake = k }, rivers, chains, grid, chainOf, indexIn, water, lakeIndices));
                        }
                        else
                        {
                            var other = chains[span.chain];
                            Through(chain, span, s => other.level[span.at[s - span.first]], NearWater(new Contact { chain = span.chain, at = span.at[0], lake = -1 }, rivers, chains, grid, chainOf, indexIn, water, lakeIndices),
                                s => other.dry[span.at[s - span.first]]);
                        }
                    }
                    if (Meets(chain, chain.mouth, true, chains))
                    {
                        EndAt(chain, LevelOf(chain.mouth, chains, levels, lakeIndices), NearWater(chain.mouth, rivers, chains, grid, chainOf, indexIn, water, lakeIndices));
                        if (pass == 0 && chain.mouth.chain >= 0) Tributaries++;
                    }
                    if (Meets(chain, chain.source, false, chains))
                        StartAt(chain, LevelOf(chain.source, chains, levels, lakeIndices), NearWater(chain.source, rivers, chains, grid, chainOf, indexIn, water, lakeIndices));
                    for (int k = 0; k < lakeCount; k++)
                    {
                        if (setter[k] != c || touchAt[k] < 0 || preset[k]) continue;
                        // the river's water where they meet; across ground lowered to the river's level, or raised a little at most
                        float river = chain.level[touchAt[k]];
                        SetLake(lakeIndices[k], touchGap[k] < Separation ? river : Mathf.Min(river, own[k] + MaxLakeRise), water, levels, lakeLevels);
                        if (pass == 0) LakesLevelled++;
                    }
                    Falling(chain);
                }
                // a stretch cut more than MaxCut into the data is left dry (it stops and goes on beyond; a fall to the water a
                // chain meets excepted, or it would end short of it), and later chains know it has no water there
                foreach (var chain in chains) UnderBumps(chain);
            }
            // samples inside the water of a chain that has no water there (left dry) are not at its level
            foreach (var chain in chains) Unpin(chain, chains);

            // no fall steeper than MaxSlope again, now that chains came down to the water they meet (a fall to it on a falling
            // stretch was steeper than either), with the levels the water they meet gave them as they are
            foreach (var chain in chains) { Ramp(chain); Falling(chain); }

            // what is left: water that meets water at another level, and falls steeper than MaxSlope (diagnostics)
            foreach (var chain in chains)
            {
                int n = chain.level.Length;
                if (Meets(chain, chain.mouth, true, chains) && !chain.dry[n - 1])
                    Left(chain, n - 1, LevelOf(chain.mouth, chains, levels, lakeIndices), "flows into " + What(chain.mouth, rivers, chains, water, lakeIndices), rivers);
                if (Meets(chain, chain.source, false, chains) && !chain.dry[0])
                    Left(chain, 0, LevelOf(chain.source, chains, levels, lakeIndices), "leaves " + What(chain.source, rivers, chains, water, lakeIndices), rivers);
                foreach (var span in chain.spans)
                {
                    int worst = -1; float off = 0;
                    for (int s = span.first; s <= span.last; s++)
                    {
                        if (chain.dry[s] || !chain.inside[s]) continue;
                        float d = Mathf.Abs(chain.level[s] - SpanLevel(span, s, chains, levels, lakeIndices));
                        if (worst < 0 || d > off) { worst = s; off = d; }
                    }
                    if (worst >= 0)
                        Left(chain, worst, SpanLevel(span, worst, chains, levels, lakeIndices), "inside " + (span.lake >= 0 ? water[lakeIndices[span.lake]].id
                            : What(new Contact { chain = span.chain, at = span.at[worst - span.first], lake = -1 }, rivers, chains, water, lakeIndices)), rivers);
                }
                for (int s = 0; s + 1 < n; s++)
                {
                    if (chain.dry[s] || chain.dry[s + 1]) continue;
                    float slope = (chain.level[s] - chain.level[s + 1]) / Mathf.Max(0.01f, (chain.position[s + 1] - chain.position[s]).magnitude);
                    if (slope > MaxSlope * 1.05f) Steep++;
                    Steepest = Mathf.Max(Steepest, slope);
                }
            }
            for (int k = 0; k < lakeCount; k++)
                if (setter[k] >= 0 && touchAt[k] >= 0 && touchGap[k] < Separation)
                    Left(chains[setter[k]], touchAt[k], levels[lakeIndices[k]], "beside " + water[lakeIndices[k]].id, rivers);

            // the levels into the lines (along their points again), and what later exports keep (on this export's own ground)
            foreach (var chain in chains)
            {
                if (chain.reversed) Turn(chain);
                float largest = 0; int where = 0;
                for (int s = 0; s < chain.level.Length; s++)
                {
                    var (line, sample) = chain.points[s];
                    var p = rivers[line].samples[sample];
                    float change = Mathf.Abs(chain.level[s] - p.y);
                    if (change > largest) { largest = change; where = s; }
                    rivers[line].samples[sample] = new Vector3(p.x, chain.level[s], p.z);
                }
                LargestChange = Mathf.Max(LargestChange, largest);
                if (largest > LargeChange)
                {
                    int n = chain.level.Length;
                    var ids = new List<string>(); foreach (int line in chain.lines) ids.Add(rivers[line].feature.id + " " + rivers[line].feature.type + " " + rivers[line].feature.width.ToString("F0") + " m");
                    Large.Add($"{string.Join(" > ", ids)}: {n * step:F0} m, data {chain.raw[0]:F1} > {chain.raw[n - 1]:F1}, level {chain.level[0]:F1} > {chain.level[n - 1]:F1}, " +
                              $"{largest:F1} m off at {chain.position[where].x:F0},{chain.position[where].y:F0} (data {chain.raw[where]:F1}, level {chain.level[where]:F1})" +
                              (chain.reversed ? " reversed" : "") + chain.notes);
                }
                Record(chain, bounds);
            }

            // no water where a chain was left dry: under bumps, short of water, beyond dams
            foreach (var chain in chains)
            {
                int n = chain.level.Length; bool any = false;
                for (int s = 0; s < n; s++)
                {
                    if (!chain.dry[s]) continue;
                    any = true;
                    if (s > 0) GapMetres += (chain.position[s] - chain.position[s - 1]).magnitude;
                    if (s == 0 || !chain.dry[s - 1]) Gaps++;
                }
                if (!any) continue;
                foreach (int line in chain.lines) Split(paths, rivers[line], chain, line, chain.dry);
            }
        }

        // The nearest point of another chain that a point of chain c meets (within Meet beyond that chain's half width there),
        // leaving out the first and last points of chains that start or end at it: lines that leave the same node, and lines that
        // flow into the chain's start.
        static Contact Nearest(List<PathBuild> rivers, List<Chain> chains, Dictionary<long, List<(int line, int sample)>> grid, int[] chainOf, List<int>[] indexIn, int c, Vector2 point)
        {
            var found = Contact.None; float best = float.MaxValue;
            // a river fitted to the ground map is up to 2 x GeoRivers.Reach wide
            foreach (var (line, sample) in Near(grid, point, Meet + GeoRivers.Reach))
            {
                int other = chainOf[line];
                if (other == c) continue;
                float d = (XZ(rivers[line].samples[sample]) - point).magnitude;
                if (d > Meet + rivers[line].HalfWidth(sample) || d >= best) continue;
                int at = indexIn[line][sample];
                if (d <= Meet && (at == 0 || at == chains[other].level.Length - 1)) continue;
                best = d; found = new Contact { chain = other, at = at, lake = -1 };
            }
            return found;
        }

        // The wider chain (or of the same width, earlier) whose water a point of chain c lies in — within the half width of the
        // chain's line, the deepest inside of several — and its sample nearest the point (along that chain's flow); -1 for none.
        static void InWater(List<PathBuild> rivers, List<Chain> chains, Dictionary<long, List<(int line, int sample)>> grid, int[] chainOf, List<int>[] indexIn, int c, Vector2 point,
            out int other, out int at)
        {
            other = -1; at = -1; float best = 0;
            foreach (var (line, sample) in Near(grid, point, GeoRivers.Reach))
            {
                int t = chainOf[line];
                if (t == c || !(chains[t].width > chains[c].width + 0.01f || (Mathf.Abs(chains[t].width - chains[c].width) <= 0.01f && t < c))) continue;
                float depth = rivers[line].HalfWidth(sample) - (XZ(rivers[line].samples[sample]) - point).magnitude;
                if (depth <= best) continue;
                int s = indexIn[line][sample];
                best = depth; other = t; at = chains[t].reversed ? chains[t].level.Length - 1 - s : s;
            }
        }

        // The lake that an end of a chain lies in, or comes within Touch of beyond its half width: what the end meets, unless it
        // meets a chain's point nearer (the outline of a lake that a river flows through lies off the river's water).
        static Contact WithLake(Contact contact, Vector2 point, float half, IList<AreaFeature> water, List<int> lakeIndices, List<Rect> boxes, List<Chain> chains)
        {
            int found = -1; float best = float.MaxValue;
            for (int k = 0; k < lakeIndices.Count; k++)
            {
                var box = boxes[k]; float reach = half + Touch;
                if (point.x < box.xMin - reach || point.x > box.xMax + reach || point.y < box.yMin - reach || point.y > box.yMax + reach) continue;
                var rings = water[lakeIndices[k]].rings;
                float gap = GeoData.InPolygon(point.x, point.y, rings) ? -half : OutlineDistance(point, rings) - half;
                if (gap > Touch || gap >= best) continue;
                found = k; best = gap;
            }
            if (found < 0) return contact;
            if (contact.chain >= 0 && best > -half)
            {
                var other = chains[contact.chain];
                if ((other.position[contact.at] - point).magnitude - other.half[contact.at] <= best) return contact;
            }
            return new Contact { chain = -1, at = -1, lake = found };
        }

        // A contact with a chain, as an index into that chain's samples along its flow.
        static Contact WithFlow(Contact contact, List<Chain> chains)
        {
            if (contact.chain < 0 || !chains[contact.chain].reversed) return contact;
            contact.at = chains[contact.chain].level.Length - 1 - contact.at;
            return contact;
        }

        // Whether an end of the chain (its last sample, or its first) meets the water of a contact from outside: not a point of a
        // chain left without water, nor water the end lies inside (it is at that water's level there, a span).
        static bool Meets(Chain chain, Contact contact, bool atEnd, List<Chain> chains)
        {
            if (!contact.Any || (contact.chain >= 0 && chains[contact.chain].dry[contact.at])) return false;
            int s = atEnd ? chain.level.Length - 1 : 0;
            foreach (var span in chain.spans)
                if (span.first <= s && s <= span.last && (contact.lake >= 0 ? span.lake == contact.lake : span.chain == contact.chain)) return false;
            return true;
        }

        // The last sample of a span that keeps an earlier export's level (nearest the outlet), or -1.
        static int KeptIn(Chain chain, Span span)
        {
            for (int s = span.last; s >= span.first; s--) if (chain.kept[s]) return s;
            return -1;
        }

        static float LevelOf(Contact contact, List<Chain> chains, IList<float> levels, List<int> lakeIndices) =>
            contact.chain >= 0 ? chains[contact.chain].level[contact.at] : levels[lakeIndices[contact.lake]];

        static float SpanLevel(Span span, int s, List<Chain> chains, IList<float> levels, List<int> lakeIndices) =>
            span.lake >= 0 ? levels[lakeIndices[span.lake]] : chains[span.chain].level[span.at[s - span.first]];

        // Whether a point of a chain (with its half width there) lies within Separation of the water of a contact.
        static Func<Vector2, float, bool> NearWater(Contact contact, List<PathBuild> rivers, List<Chain> chains, Dictionary<long, List<(int line, int sample)>> grid, int[] chainOf,
            List<int>[] indexIn, IList<AreaFeature> water, List<int> lakeIndices)
        {
            if (contact.chain < 0)
            {
                var rings = water[lakeIndices[contact.lake]].rings;
                return (p, half) => GeoData.InPolygon(p.x, p.y, rings) || OutlineDistance(p, rings) < half + Separation;
            }
            int target = contact.chain;
            return (p, half) =>
            {
                foreach (var (line, sample) in Near(grid, p, half + Separation + GeoRivers.Reach))
                {
                    if (chainOf[line] != target || chains[target].dry[WithFlow(new Contact { chain = target, at = indexIn[line][sample], lake = -1 }, chains).at]) continue;
                    if ((XZ(rivers[line].samples[sample]) - p).magnitude < rivers[line].HalfWidth(sample) + half + Separation) return true;
                }
                return false;
            };
        }

        // A chain flows into water at `level` at its last sample: it comes down to it over the last metres when it is higher (but
        // stops short of it where that would cut it more than MaxCut into its data), and is backed up to it when it is lower (but
        // stops short of it where that would stand it more than MaxBackwater over its data).
        void EndAt(Chain chain, float level, Func<Vector2, float, bool> near)
        {
            int n = chain.level.Length;
            if (chain.dry[n - 1]) return;
            float end = chain.level[n - 1];
            if (end > level + Even)
            {
                if (end - level > MaxCut) { StopShort(chain, false, near); return; }
                float metres = Mathf.Clamp((end - level) * FallPerMetre, MinFall, MaxFall), along = 0;
                for (int s = n - 1; s >= 0; s--)
                {
                    if (s < n - 1) along += (chain.position[s + 1] - chain.position[s]).magnitude;
                    if (along >= metres) break;
                    if (chain.kept[s] || chain.pinned[s]) break;
                    float t = 1 - along / metres;
                    chain.level[s] = Mathf.Lerp(chain.level[s], level, t * t * (3 - 2 * t));
                    chain.tail[s] = true;
                }
            }
            else if (end < level - Even)
            {
                if (Backwater(chain, n - 1, level) > MaxBackwater) { StopShort(chain, false, near); return; }
                for (int s = n - 1; s >= 0 && chain.level[s] < level; s--) if (!chain.kept[s] && !chain.pinned[s]) { chain.level[s] = level; chain.tail[s] = true; }
            }
            if (!chain.kept[n - 1]) chain.pinned[n - 1] = true;
        }

        // A chain leaves water at `level` at its first sample: never higher than it (a river that would be cut more than MaxCut into
        // its data for it stops short of it instead; a stream is left dry where it is cut that deep, as under any bump), and when it
        // is lower it comes down from that level over the first metres (but stops short of it where that would stand it more than
        // MaxBackwater over its data).
        void StartAt(Chain chain, float level, Func<Vector2, float, bool> near)
        {
            int n = chain.level.Length;
            if (chain.dry[0]) return;
            float head = chain.level[0];
            if (head > level + Even)
            {
                float cut = 0; bool higher = false;
                for (int s = 0; s < n; s++)
                {
                    if (chain.level[s] <= level) continue;
                    if (chain.kept[s] || chain.pinned[s]) higher = true;
                    else cut = Mathf.Max(cut, chain.data[s] - level);
                }
                if (higher || (chain.wide && cut > MaxCut)) { StopShort(chain, true, near); return; }
                for (int s = 0; s < n; s++) if (!chain.kept[s] && !chain.pinned[s] && chain.level[s] > level) chain.level[s] = level;
            }
            else if (head < level - Even)
            {
                float metres = Mathf.Clamp((level - head) * FallPerMetre, MinFall, MaxFall), along = 0, flood = 0;
                var raised = new List<(int s, float y)>();
                for (int s = 0; s < n; s++)
                {
                    if (s > 0) along += (chain.position[s] - chain.position[s - 1]).magnitude;
                    if (along >= metres) break;
                    if (chain.kept[s] || chain.pinned[s]) break;
                    float t = 1 - along / metres, y = Mathf.Lerp(chain.level[s], level, t * t * (3 - 2 * t));
                    flood = Mathf.Max(flood, y - chain.data[s]);
                    raised.Add((s, y));
                }
                if (flood > MaxBackwater) { StopShort(chain, true, near); return; }
                foreach (var (s, y) in raised) { chain.level[s] = y; chain.tail[s] = true; }
            }
            if (!chain.kept[0]) chain.pinned[0] = true;
        }

        // A chain through other water from its sample span.first to span.last, at levelAt(s) inside it: before it down to that
        // level over the last metres (higher; it stops short of the water where that would cut it more than MaxCut into its data)
        // or backed up to it (lower; it stops short where that would stand it more than MaxBackwater over its data); never higher
        // after it, and where it lies more than OutletDrop below it just after it (a dam, a culvert under a canal) it starts again
        // Separation beyond the water (near). absent(s): the other chain has no water there (left dry), so the sample is the
        // chain's own.
        void Through(Chain chain, Span span, Func<int, float> levelAt, Func<Vector2, float, bool> near, Func<int, bool> absent = null)
        {
            int n = chain.level.Length, first = span.first, last = span.last;
            for (int s = first; s <= last; s++)
            {
                if (absent != null && absent(s)) { chain.inside[s] = chain.pinned[s] = false; continue; }
                if (!chain.kept[s]) { chain.level[s] = levelAt(s); chain.pinned[s] = true; }
            }
            if (first > 0 && !chain.dry[first - 1])
            {
                float level = chain.level[first], before = chain.level[first - 1];
                if (before > level + Even)
                {
                    if (before - level > MaxCut) DryBefore(chain, first, near);
                    else
                    {
                        float metres = Mathf.Clamp((before - level) * FallPerMetre, MinFall, MaxFall), along = 0;
                        for (int s = first - 1; s >= 0; s--)
                        {
                            along += (chain.position[s + 1] - chain.position[s]).magnitude;
                            if (along >= metres) break;
                            if (chain.kept[s] || chain.pinned[s]) break;
                            float t = 1 - along / metres;
                            chain.level[s] = Mathf.Lerp(chain.level[s], level, t * t * (3 - 2 * t));
                            chain.tail[s] = true;
                        }
                    }
                }
                else if (before < level - Even)
                {
                    if (Backwater(chain, first - 1, level) > MaxBackwater) DryBefore(chain, first, near);
                    else for (int s = first - 1; s >= 0 && chain.level[s] < level; s--) if (!chain.kept[s] && !chain.pinned[s]) { chain.level[s] = level; chain.tail[s] = true; }
                }
            }
            if (last < n - 1 && !chain.dry[last + 1])
            {
                float level = chain.level[last];
                bool blocked = false;
                for (int s = last + 1; s < n && !blocked; s++) blocked = (chain.kept[s] || chain.pinned[s]) && chain.level[s] > level + Even;
                if (!blocked) for (int s = last + 1; s < n; s++) if (!chain.kept[s] && !chain.pinned[s] && chain.level[s] > level) chain.level[s] = level;
                // below it by more than OutletDrop, or above it where the chain cannot come down (a kept level, other water
                // downstream higher than this): the water starts again beyond it
                float drop = level - chain.level[last + 1];
                if (blocked || drop > OutletDrop || drop < -Even)
                {
                    bool dried = false;
                    for (int s = last + 1; s < n && !chain.inside[s] && near(chain.position[s], chain.half[s]); s++) { dried |= !chain.dry[s]; chain.dry[s] = true; }
                    if (dried) Dams++;
                }
                else if (drop > Even)
                {
                    float metres = Mathf.Clamp(drop * FallPerMetre, MinFall, MaxFall), along = 0;
                    for (int s = last + 1; s < n; s++)
                    {
                        along += (chain.position[s] - chain.position[s - 1]).magnitude;
                        if (along >= metres) break;
                        if (chain.kept[s] || chain.pinned[s]) break;
                        float t = 1 - along / metres;
                        chain.level[s] = Mathf.Lerp(chain.level[s], level, t * t * (3 - 2 * t));
                        chain.tail[s] = true;
                    }
                }
            }
        }

        // A stream (narrower than GapWidth) cut more than MaxCut into its data is left dry there: it stops and goes on beyond, as
        // under a culvert, instead of a gorge cut through a bump — not where it comes down (or up) to the water it meets, or it
        // would end short of it, nor inside other water.
        static void UnderBumps(Chain chain)
        {
            for (int s = 0; s < chain.level.Length; s++)
                if (!chain.tail[s] && !chain.inside[s] && chain.half[s] * 2 < GapWidth && chain.data[s] - chain.level[s] > MaxCut) chain.dry[s] = true;
        }

        // Samples inside the water of another chain where that chain has none (left dry) are not at its level: the ramps and the
        // fall downstream treat them as the chain's own.
        static void Unpin(Chain chain, List<Chain> chains)
        {
            foreach (var span in chain.spans)
            {
                if (span.chain < 0) continue;
                var other = chains[span.chain];
                for (int s = span.first; s <= span.last; s++)
                    if (other.dry[span.at[s - span.first]]) chain.inside[s] = chain.pinned[s] = false;
            }
        }

        // How far a chain backed up to `level` from its sample `from` upstream would stand over its data; past a level it cannot
        // raise (kept, or other water it runs through upstream, lower than this) without end: water never rises downstream.
        static float Backwater(Chain chain, int from, float level)
        {
            float flood = 0;
            for (int s = from; s >= 0 && chain.level[s] < level; s--)
            {
                if (chain.kept[s] || (chain.pinned[s] && s != chain.level.Length - 1)) return float.MaxValue;
                flood = Mathf.Max(flood, level - chain.data[s]);
            }
            return flood;
        }

        // No water on a chain's samples before other water for as long as they lie within Separation of it.
        void DryBefore(Chain chain, int first, Func<Vector2, float, bool> near)
        {
            bool dried = false;
            for (int s = first - 1; s >= 0 && !chain.inside[s] && near(chain.position[s], chain.half[s]); s--) { dried |= !chain.dry[s]; chain.dry[s] = true; }
            if (dried) Short++;
        }

        // No water from a chain's first sample (or its last) for as long as it lies within Separation of the water it would meet
        // with a step.
        void StopShort(Chain chain, bool atStart, Func<Vector2, float, bool> near)
        {
            int n = chain.level.Length;
            for (int i = 0; i < n; i++)
            {
                int s = atStart ? i : n - 1 - i;
                if (chain.inside[s] || !near(chain.position[s], chain.half[s])) break;
                chain.dry[s] = true;
            }
            Short++;
        }

        // Notes a sample of a chain whose level is more than Step off the level of the water it meets there.
        void Left(Chain chain, int s, float level, string what, List<PathBuild> rivers)
        {
            float off = Mathf.Abs(chain.level[s] - level);
            if (off <= Step) return;
            Unmet++;
            WorstUnmet = Mathf.Max(WorstUnmet, off);
            var line = rivers[chain.points[s].line].feature;
            if (UnmetNotes.Count < 100)
                UnmetNotes.Add($"{off:F2} m: {line.id} {line.type} {line.width:F0} m at {chain.position[s].x:F0},{chain.position[s].y:F0} ({chain.level[s]:F2}) {what} ({level:F2})");
        }

        static string What(Contact contact, List<PathBuild> rivers, List<Chain> chains, IList<AreaFeature> water, List<int> lakeIndices)
        {
            if (contact.chain < 0) return water[lakeIndices[contact.lake]].id + " " + water[lakeIndices[contact.lake]].type;
            var line = rivers[chains[contact.chain].points[contact.at].line].feature;
            return line.id + " " + line.type + " " + line.width.ToString("F0") + " m";
        }

        // The line without its samples under a bump: the runs of the others (two samples at least) take its place among paths.
        static void Split(IList<PathBuild> paths, PathBuild path, Chain chain, int line, bool[] cut)
        {
            var dry = new bool[path.samples.Count]; bool any = false;
            for (int s = 0; s < chain.points.Count; s++)
                if (chain.points[s].line == line && cut[s]) { dry[chain.points[s].sample] = true; any = true; }
            if (!any) return;
            var runs = new List<PathBuild>();
            for (int i = 0, start = -1; i <= dry.Length; i++)
            {
                bool wet = i < dry.Length && !dry[i];
                if (wet && start < 0) start = i;
                if (wet || start < 0) continue;
                if (i - start >= 2)
                {
                    var samples = path.samples.GetRange(start, i - start);
                    runs.Add(new PathBuild { feature = path.feature, river = true, controls = new List<Vector3>(samples), samples = samples,
                        widths = path.widths != null && path.widths.Count == path.samples.Count ? path.widths.GetRange(start, i - start) : null });
                }
                start = -1;
            }
            int index = paths.IndexOf(path);
            if (index < 0) return;
            if (runs.Count == 0) { paths.RemoveAt(index); return; }
            paths[index] = runs[0];
            for (int r = 1; r < runs.Count; r++) paths.Add(runs[r]);
        }

        // A line and the lines it continues into, as one run of samples.
        static Chain Build(List<PathBuild> rivers, int head, int[] next, int[] main, int[] chainOf, List<int>[] indexIn, int id)
        {
            var chain = new Chain();
            for (int i = head; i >= 0 && chainOf[i] < 0; i = next[i] >= 0 && main[next[i]] == i ? next[i] : -1)
            {
                chainOf[i] = id; chain.lines.Add(i);
                var samples = rivers[i].samples;
                indexIn[i] = new List<int>(samples.Count);
                for (int s = 0; s < samples.Count; s++) { indexIn[i].Add(chain.points.Count); chain.points.Add((i, s)); }
            }
            int n = chain.points.Count;
            chain.level = new float[n]; chain.raw = new float[n]; chain.half = new float[n]; chain.position = new Vector2[n];
            chain.kept = new bool[n]; chain.tail = new bool[n]; chain.dry = new bool[n]; chain.inside = new bool[n]; chain.pinned = new bool[n];
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            for (int s = 0; s < n; s++)
            {
                var (line, sample) = chain.points[s];
                var p = rivers[line].samples[sample];
                chain.raw[s] = chain.level[s] = p.y; chain.position[s] = XZ(p); chain.half[s] = rivers[line].HalfWidth(sample);
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z);
            }
            chain.box = Rect.MinMaxRect(minX, minZ, maxX, maxZ);
            return chain;
        }

        // The profile: the data without its spikes (the median over SpikeWindow metres, then the mean over SmoothWindow), never
        // rising, with kept levels of an earlier export as they are, then the mean over SmoothWindow again (it keeps the profile
        // falling), so pools meet falls without corners. The chain's arrays run with its flow.
        //  - A stream (narrower than GapWidth): the lowest level so far along it, so it never stands above the data — its line
        //    crosses spurs and banks the elevation data has between it and its real bed, and those are cut through (or left dry,
        //    MaxCut).
        //  - A river: the nearest profile that never rises (isotonic regression), no more than MaxFill above the data. The data
        //    of a wide river's water is noisy by a metre or two over kilometres, and the lowest level so far held tens of
        //    kilometres of river at the bottom of one dip, deep in a trench.
        void Fit(Chain chain, float step)
        {
            int n = chain.level.Length;
            float rise = 0;
            for (int s = 1; s < n; s++) rise += Mathf.Max(0, chain.raw[s] - chain.raw[s - 1]);
            int smooth = Mathf.Max(1, Mathf.RoundToInt(SmoothWindow / step / 2));
            var data = chain.data = Mean(Median(chain.raw, Mathf.Max(1, Mathf.RoundToInt(SpikeWindow / step / 2))), smooth, null);
            var widths = new List<float>(chain.half); widths.Sort();
            chain.width = widths[widths.Count / 2] * 2;
            chain.wide = chain.width >= GapWidth;
            if (chain.wide)
            {
                for (int s = 0; s < n; s++) if (!chain.kept[s]) chain.level[s] = data[s];
                Isotonic(chain.level, chain.kept);
                for (int s = 0; s < n; s++) if (!chain.kept[s]) chain.level[s] = Mathf.Min(chain.level[s], data[s] + MaxFill);
            }
            else
                for (int s = 0; s < n; s++)
                    if (!chain.kept[s]) chain.level[s] = s > 0 ? Mathf.Min(data[s], chain.level[s - 1]) : data[s];
            Falling(chain.level, chain.kept);
            System.Array.Copy(Mean(chain.level, smooth, chain.kept), chain.level, n);
            Falling(chain.level, chain.kept);
            float after = 0;
            for (int s = 1; s < n; s++) after += Mathf.Max(0, chain.level[s] - chain.level[s - 1]);
            RiseRemoved += rise - after;
        }

        // No fall steeper than MaxSlope along a chain: above a steeper one (a drop of the isotonic profile, a jump of the data, a
        // fall to water on a stretch that falls itself) the water is cut down to a ramp, and below one that it cannot cut (kept
        // levels, the level of water a sample is in or ends at) it is brought up to one. Each stretch of water on its own (not
        // across samples left dry), and samples inside other water as they are: the way into it and out of it is Through's (a fall
        // just below it is a dam).
        static void Ramp(Chain chain)
        {
            int n = chain.level.Length;
            for (int s = n - 2; s >= 0; s--)
            {
                if (chain.kept[s] || chain.inside[s] || chain.pinned[s] || chain.dry[s] || chain.dry[s + 1] || chain.inside[s + 1]) continue;
                float limit = chain.level[s + 1] + MaxSlope * (chain.position[s + 1] - chain.position[s]).magnitude;
                if (chain.level[s] > limit) chain.level[s] = limit;
            }
            for (int s = 1; s < n; s++)
            {
                if (chain.kept[s] || chain.inside[s] || chain.pinned[s] || chain.dry[s] || chain.dry[s - 1] || chain.inside[s - 1]) continue;
                float limit = chain.level[s - 1] - MaxSlope * (chain.position[s] - chain.position[s - 1]).magnitude;
                if (chain.level[s] < limit) chain.level[s] = limit;
            }
        }

        // Isotonic regression, falling: the nearest sequence that never rises, by pooling neighbours that do (each stretch between
        // kept values on its own, within them).
        static void Isotonic(float[] y, bool[] kept)
        {
            int n = y.Length, start = 0;
            float upper = float.PositiveInfinity;
            for (int s = 0; s <= n; s++)
            {
                if (s < n && !kept[s]) continue;
                float lower = s < n ? y[s] : float.NegativeInfinity;
                Pool(y, start, s, upper, lower);
                if (s < n) upper = y[s];
                start = s + 1;
            }
        }

        static void Pool(float[] y, int from, int to, float upper, float lower)
        {
            if (to <= from) return;
            var values = new List<float>(); var weights = new List<int>();
            for (int s = from; s < to; s++)
            {
                values.Add(y[s]); weights.Add(1);
                while (values.Count > 1 && values[values.Count - 1] > values[values.Count - 2])
                {
                    int a = values.Count - 2, b = values.Count - 1;
                    values[a] = (values[a] * weights[a] + values[b] * weights[b]) / (weights[a] + weights[b]); weights[a] += weights[b];
                    values.RemoveAt(b); weights.RemoveAt(b);
                }
            }
            int k = from;
            for (int v = 0; v < values.Count; v++)
                for (int w = 0; w < weights[v]; w++) y[k++] = Mathf.Clamp(values[v], Mathf.Min(lower, upper), upper);
        }

        // The median of each sample's neighbours within half samples to each side (fewer at the ends).
        static float[] Median(float[] y, int half)
        {
            int n = y.Length;
            var result = new float[n]; var window = new List<float>(2 * half + 1);
            for (int s = 0; s < n; s++)
            {
                window.Clear();
                for (int j = Mathf.Max(0, s - half); j <= Mathf.Min(n - 1, s + half); j++) window.Add(y[j]);
                window.Sort();
                result[s] = window[window.Count / 2];
            }
            return result;
        }

        // The mean of each sample's neighbours, symmetric (fewer at the ends, so a slope stays a slope); kept samples as they are.
        static float[] Mean(float[] y, int half, bool[] kept)
        {
            int n = y.Length;
            var result = new float[n];
            for (int s = 0; s < n; s++)
            {
                if (kept != null && kept[s]) { result[s] = y[s]; continue; }
                int reach = Mathf.Min(half, Mathf.Min(s, n - 1 - s));
                float sum = 0;
                for (int j = s - reach; j <= s + reach; j++) sum += y[j];
                result[s] = sum / (2 * reach + 1);
            }
            return result;
        }

        // Whether the data climbs along the chain's points: the mean of its last EndMetres over that of its first.
        static bool Climbs(Chain chain, float step)
        {
            int n = chain.raw.Length, ends = Mathf.Clamp(Mathf.RoundToInt(EndMetres / step), 1, Mathf.Max(1, n / 3));
            float first = 0, last = 0;
            for (int s = 0; s < ends; s++) { first += chain.raw[s]; last += chain.raw[n - 1 - s]; }
            return (last - first) / ends > Mathf.Max(Climb, ClimbSlope * n * step);
        }

        // Never rising downstream, with kept values as they are: down to the level before, then up to a kept level that comes higher.
        static void Falling(float[] y, bool[] kept)
        {
            for (int s = 1; s < y.Length; s++) if (!kept[s] && y[s] > y[s - 1]) y[s] = y[s - 1];
            for (int s = y.Length - 2; s >= 0; s--) if (!kept[s] && y[s] < y[s + 1]) y[s] = y[s + 1];
        }

        // The same for a chain that has met the water it meets: kept levels and the levels of that water (pinned) as they are, and
        // each stretch of water on its own, so a stream that stops short of a lake higher than it is neither raised to it across
        // the ground between them nor brings the lake's level inside it down to its own.
        static void Falling(Chain chain)
        {
            var y = chain.level; int n = y.Length;
            for (int s = 1; s < n; s++)
                if (!chain.kept[s] && !chain.pinned[s] && !chain.dry[s] && !chain.dry[s - 1] && y[s] > y[s - 1]) y[s] = y[s - 1];
            for (int s = n - 2; s >= 0; s--)
                if (!chain.kept[s] && !chain.pinned[s] && !chain.dry[s] && !chain.dry[s + 1] && y[s] < y[s + 1]) y[s] = y[s + 1];
        }

        // The chain's samples (and its spans) the other way round.
        static void Turn(Chain chain)
        {
            System.Array.Reverse(chain.level); System.Array.Reverse(chain.raw); System.Array.Reverse(chain.half); System.Array.Reverse(chain.position);
            System.Array.Reverse(chain.kept); System.Array.Reverse(chain.tail); System.Array.Reverse(chain.dry); System.Array.Reverse(chain.inside);
            System.Array.Reverse(chain.pinned);
            if (chain.data != null) System.Array.Reverse(chain.data);
            chain.points.Reverse();
            int n = chain.level.Length;
            foreach (var span in chain.spans)
            {
                int first = n - 1 - span.last; span.last = n - 1 - span.first; span.first = first;
                if (span.at != null) System.Array.Reverse(span.at);
            }
        }

        void SetLake(int index, float level, IList<AreaFeature> water, IList<float> levels, Dictionary<string, float> lakeLevels)
        {
            levels[index] = level;
            string id = water[index].id;
            if (string.IsNullOrEmpty(id)) return;
            lakes[id] = level;
            if (lakeLevels != null) lakeLevels[id] = level;
        }

        // The samples of an earlier export's rivers that are this chain's water keep their levels.
        void Keep(Chain chain)
        {
            int n = chain.level.Length;
            for (int s = 0; s < n; s++)
            {
                var p = chain.position[s];
                Vector2 direction = (chain.position[Mathf.Min(n - 1, s + 1)] - chain.position[Mathf.Max(0, s - 1)]).normalized;
                float best = Same;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (!stations.TryGetValue(Key(Mathf.FloorToInt(p.x / CellSize) + dx, Mathf.FloorToInt(p.y / CellSize) + dz), out var near)) continue;
                        foreach (var station in near)
                        {
                            float d = (station.position - p).magnitude;
                            if (d >= best || Vector2.Dot(station.direction, direction) < 0.5f) continue;
                            best = d; chain.level[s] = station.level; chain.kept[s] = true;
                        }
                    }
            }
        }

        // The levels of the chain on the export's own ground, for later exports to keep.
        void Record(Chain chain, Rect? bounds)
        {
            int n = chain.level.Length;
            for (int s = 0; s < n; s++)
            {
                if (chain.kept[s]) continue;
                var p = chain.position[s];
                if (bounds.HasValue && !bounds.Value.Contains(p)) continue;
                var station = new Station { position = p, level = chain.level[s], direction = (chain.position[Mathf.Min(n - 1, s + 1)] - chain.position[Mathf.Max(0, s - 1)]).normalized };
                long key = Key(Mathf.FloorToInt(p.x / CellSize), Mathf.FloorToInt(p.y / CellSize));
                if (!stations.TryGetValue(key, out var list)) stations[key] = list = new List<Station>();
                list.Add(station);
            }
        }

        static Dictionary<long, List<(int line, int sample)>> SampleGrid(List<PathBuild> rivers)
        {
            var grid = new Dictionary<long, List<(int, int)>>();
            for (int i = 0; i < rivers.Count; i++)
                for (int s = 0; s < rivers[i].samples.Count; s++)
                {
                    var p = rivers[i].samples[s];
                    long key = Key(Mathf.FloorToInt(p.x / CellSize), Mathf.FloorToInt(p.z / CellSize));
                    if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<(int, int)>();
                    list.Add((i, s));
                }
            return grid;
        }

        static IEnumerable<(int line, int sample)> Near(Dictionary<long, List<(int line, int sample)>> grid, Vector2 p, float radius)
        {
            int r = Mathf.CeilToInt(radius / CellSize), cx = Mathf.FloorToInt(p.x / CellSize), cz = Mathf.FloorToInt(p.y / CellSize);
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                    if (grid.TryGetValue(Key(cx + dx, cz + dz), out var list))
                        foreach (var item in list) yield return item;
        }

        static float OutlineDistance(Vector2 p, float[][] rings)
        {
            float best = float.MaxValue;
            foreach (var ring in rings)
            {
                if (ring == null || ring.Length < 4) continue;
                int count = ring.Length / 2;
                for (int i = 0; i < count; i++)
                {
                    int j = (i + 1) % count;
                    float d = GeoData.SegmentDistance(p, new Vector2(ring[2 * i], ring[2 * i + 1]), new Vector2(ring[2 * j], ring[2 * j + 1]), out _);
                    if (d < best) best = d;
                }
            }
            return best;
        }

        static Rect Box(float[][] rings)
        {
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            foreach (var ring in rings)
            {
                if (ring == null) continue;
                for (int i = 0; i + 1 < ring.Length; i += 2) { minX = Mathf.Min(minX, ring[i]); maxX = Mathf.Max(maxX, ring[i]); minZ = Mathf.Min(minZ, ring[i + 1]); maxZ = Mathf.Max(maxZ, ring[i + 1]); }
            }
            return minX > maxX ? Rect.zero : Rect.MinMaxRect(minX, minZ, maxX, maxZ);
        }

        static Vector2 XZ(Vector3 p) => new Vector2(p.x, p.z);
        static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;
    }
}
