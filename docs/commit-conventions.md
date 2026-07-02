# Commit conventions

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org): a
`type(scope): subject` header, an optional body, and optional trailers.

```
type(scope): subject

Optional body — what changed and why (wrap ~100 cols).

Trailers (see below)
```

## Rules

- **`type`** — one of: `feat` (new behaviour), `fix` (bug fix), `refactor` (behaviour-preserving
  restructuring), `perf`, `test`, `docs`, `chore` (build/infra/backlog/tooling).
- **`scope`** — a **code-area tag from the vocabulary below**, NOT a project-tracking reference. A commit
  scope should tell a reader *what part of the codebase* changed at a glance — never make them go look up a
  stage id to decode it. (Rationale: `feat(S89 D2):` forced a lookup; `feat(tessellation):` does not.)
- **`subject`** — imperative, lower-case, no trailing period; ≤ ~72 chars.
- Stage / backlog references (`S89`, its internal `D2`, etc.) do **not** go in the subject. If a commit
  ties to a stage, put it in a **body trailer** so it's traceable without cluttering the header:
  `Stage: S89 (render-layer unification)`.

## Scope vocabulary

| Scope | Area |
|---|---|
| `tessellation` | geometry → mesh: earcut, line expansion, MeshData writing, the Burst tessellation pipeline |
| `tile-pipeline` | `TileManager` lifecycle — fetch / kick / consume / release, scheduling, budgets |
| `render-layers` | `IRenderLayer` / layer set / draw-order / material indexing |
| `style` | expressions, paint/layout properties, filters, TileJSON/style parsing |
| `decode` | MVT decode |
| `backends` | Entities / BRG / GameObject render backends |
| `camera` | camera model + Unity camera binding, gestures, framing |
| `shaders` | HLSL / URP materials |
| `projection` | Web-Mercator / coordinate math |
| `backlog` | roadmap & planning docs (e.g. the MapLibre parity spec) |
| `docs` | documentation |

Add a scope here when a genuinely new area appears — don't stretch an existing one or invent an ad-hoc tag.
Split by layer type (`fill` / `line`) only if a change is truly layer-specific and `tessellation` is too broad.

## Project-specific trailers

Every commit ends with the identity/session trailers the environment mandates (Co-Authored-By +
Claude-Session). A stage-linked commit adds a `Stage:` trailer above those.

## Examples

```
feat(tessellation): fills tessellate via burst geometry in the live pipeline
refactor(tile-pipeline): dense per-source tessellation produce
feat(tessellation): burst line kernel + differential oracle
docs(backlog): native-tile-pipeline exploration spike
fix(shaders): line AA edge width no longer clobbered by line-blur binding
```
