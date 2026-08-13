namespace MapRenderer.Jobs
{
    /// <summary>
    /// Waist 1's producer seam: the one way a source format reaches the tile-geometry pipeline. An
    /// implementation captures its own format-specific payload (MVT command streams, sliced GeoJSON paths, …)
    /// at construction and turns it into a <see cref="TileGeometryBuffers"/>.
    ///
    /// <para><b>Managed-side polymorphism only.</b> <see cref="Materialize"/> is called once per (layer, tile),
    /// on the calling thread, <i>before</i> any job is scheduled; the implementation then runs whatever
    /// <b>concrete</b> Burst job its format needs. This interface never crosses into Burst — nothing but
    /// blittable <c>NativeArray</c>s does. That mirrors <c>ProjectionDispatch</c> in principle (Burst cannot
    /// dispatch through an interface, so the choice is made on the managed side); it differs in form because
    /// the formats' <i>inputs</i> do not share a shape, so there is no common generic job to instantiate and
    /// no payload parameter on this method.</para>
    ///
    /// <para><b>Ownership TRANSFERS to the caller.</b> Every call mints a fresh buffer. The implementation must
    /// not dispose it, must not cache it, and must not hand the same buffer out twice; the caller disposes it
    /// on every exit path. This is <b>transfer, not borrow</b>.</para>
    ///
    /// <para><b>How a two-owner mistake fails — restated per backing mode (IR C1 P3; measured, not argued).</b>
    /// This paragraph used to say flatly that a double dispose is a "silent no-op" that leaks quietly. That is
    /// true of only ONE of the two modes, and the blanket wording had been copied onto paths where it is
    /// false:
    /// <list type="bullet">
    /// <item><b>Array-backed</b> — what <see cref="Materialize"/> returns, and what every consumer borrows:
    /// <c>Dispose</c> frees three real <c>NativeArray</c>s, so a second owner is a <b>loud double free</b>.
    /// Measured in P2 (R6): 32 failures across 19 fixtures, including fixtures with no involvement in the
    /// change — the heap-corruption signature.</item>
    /// <item><b>List-backed</b> (<c>TileGeometryBuffers.AdoptDerivedLists</c>, fill's derived buffer): the
    /// public fields are <c>AsArray()</c> <i>views</i>, and disposing a view IS a <b>silent no-op</b> under
    /// Collections 6.5.0 — that mistake leaks quietly and every test stays green. No consumer borrows a
    /// list-backed buffer, so this is the mode that needs the warning and the one it applies to.</item>
    /// </list></para>
    ///
    /// <para><b>IR C1 P3 — who the caller now is.</b> The one production caller of the MVT implementation is
    /// <c>MvtDecoder</c> itself: the decoded LAYER owns the buffer it is handed, for the life of the decoded
    /// tile, and <b>lends</b> it to every consumer that names that source-layer — across both the mesh and
    /// symbol cadences of a kick. (B7's pass-scoped <c>TileGeometryStore</c>, which used to be that caller,
    /// is gone: it existed only because the geometry belonged to nothing.) Consumers <b>borrow</b> — never
    /// dispose, never retain past the decode scope, never mutate. The two callers that transfer-and-own
    /// directly are the ones that are themselves producers: the background quad's
    /// <c>PathGeometryMaterializer</c>, and fill's per-layer derived buffer (which
    /// <c>TileGeometryBuffers.AdoptDerivedLists</c> mints from lists rather than from a materializer). See
    /// <see cref="TileGeometryBuffers"/>'s "two-tier ownership" paragraph for the full contract.</para>
    ///
    /// <para><b>THE NAMED FENCE.</b> The returned buffer must satisfy <see cref="TileGeometryBuffers"/>'
    /// declared coordinate space: tile-local <c>double2</c> in <c>[0, Extent]</c>, Y-down, <b>never geodetic
    /// and never projected</b>.</para>
    ///
    /// <para><b>Self-describing.</b> The implementation is the sole authority for the buffer's
    /// <see cref="TileGeometryBuffers.Tile"/> and <see cref="TileGeometryBuffers.Extent"/>. Callers read those
    /// off the buffer and must not carry a second copy alongside it — a second copy is what lets a stage
    /// quietly substitute a constant.</para>
    ///
    /// <para><b>No ring filtering.</b> The buffer carries <b>all</b> rings the source expresses, unfiltered,
    /// including rings with fewer than 3 points. Fill's <c>rLen &lt; 3</c> filter belongs to
    /// <c>RingAssemblyJob</c>; line's <c>Count &lt; 2</c> filter belongs to line. A filter placed here
    /// starves the other consumer.</para>
    ///
    /// <para><b>Kind, not shape.</b> Every returned buffer must have
    /// <see cref="TileGeometryBuffers.FeatureGeometryType"/> filled for <b>every</b> feature, from the
    /// source's own declared geometry type — <b>never inferred from the coordinates</b>. Ring kind is not
    /// recoverable from a ring's points: a LineString and a polygon ring are the same shape of data, so an
    /// area-based classifier silently corrupts the triangulation instead of failing. An unfilled element
    /// reads <c>Unknown</c> and every consumer's kind gate rejects it, so forgetting renders nothing rather
    /// than something wrong.</para>
    ///
    /// <para><b>Empty input ⇒ <c>default(TileGeometryBuffers)</c></b> (<c>IsCreated == false</c>), allocating
    /// nothing.</para>
    /// </summary>
    public interface ITileGeometryMaterializer
    {
        /// <summary>Mints one tile's decoded rings. Ownership of the result transfers to the caller.</summary>
        TileGeometryBuffers Materialize();
    }
}
