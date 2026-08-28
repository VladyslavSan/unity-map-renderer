# Native filter VM

A Burst-compiled opcode virtual machine that evaluates a style layer's `filter` directly over a decoded
tile's **native feature columns** — no managed feature objects, no per-feature closures, no GC — off the
main thread. It is an *optimisation of*, never a replacement for, the managed filter path: it accepts only
the subset of filters it can prove evaluate **byte-identically** to the managed `CompiledFilter`, and any
filter outside that subset transparently falls back to the managed evaluator.

## Why it exists

Selecting which features a layer draws means evaluating its filter against every feature of every covered
tile, every time the style or the cover changes. The managed path walks a tree of boxed `Value`s and
delegate closures per feature — allocation on a hot, wide loop, and main-thread-bound. The native VM
compiles the filter **once** into a flat, blittable opcode program and evaluates it in a Burst job over the
tile's shared native columns, so the per-feature cost is a tight stack-machine loop with zero managed
allocation.

## Pipeline

```
  style layer "filter" JSON
          │
          │  NativeFilterCompiler.TryCompile      (once per filter; refuses anything unsound)
          ▼
  NativeFilterProgram  ── opcodes + key-name table + literal-string table
          │
          │  NativeFilterRebind.Rebind            (once per tile-layer)
          ▼
  binding: slot → this layer's key / value-string ids   (absent string → -1 never-match sentinel)
          │
          │  NativeFilterEvaluationJob.Execute     (once per feature, Burst)
          ▼
  matched (bool) + error code
```

`NativeFilterEvaluator` is the synchronous seam that runs the job for one feature and reports the result —
the entry point both the selection path and the dense-vs-managed parity oracle call.

## Components

| Type | Folder | Role |
|---|---|---|
| `NativeFilterCompiler` | `Expressions/` | JSON filter → `NativeFilterProgram`, or `false` (refuse → managed fallback). Recursive-descent emitter. |
| `NativeFilterProgram` | `Expressions/` | The compiled program: a bounded opcode list + the key-name and literal-string tables the binding resolves. |
| `NativeFilterOperation` / `NativeOperation` | `Expressions/` | One opcode (operation + operand + immediate) and the operation enum. |
| `NativeValue` | `Expressions/` | The VM's blittable tagged value: Number / Boolean / String (as an id) / Null. |
| `NativeFilterEvaluationJob` | `Mvt/` | The `[BurstCompile]` `IJob`: a post-order stack machine that runs one program against one feature's columns. |
| `NativeFilterEvaluator` | `Mvt/` | Runs the job synchronously and maps the result — the parity/selection seam. |
| `NativeFilterRebind` | `Mvt/` | Rebinds a program's key names + literal strings to a specific tile-layer's ids. |

**Why the split across two folders:** the job, evaluator and rebind name `MvtValueNative` — a
format-specific column type a general production type may not reference in its signatures — so they live in
the `Mvt/` decoder folder. Everything format-agnostic (the compiler, program, opcodes, value) lives here in
`Expressions/`.

## The opcode set (`NativeOperation`)

A post-order stack machine. Literals and `Get`/`Has` push; the operators pop their arguments and push a
result:

- `LiteralNumber` / `LiteralBoolean` / `LiteralString` — push a constant (strings as a bound id).
- `Get` / `Has` — read a feature tag by key: `Get` pushes its value (or `Null`), `Has` pushes presence.
- `GeometryEqual` — compare the feature's geometry kind to a constant.
- `Equal` — value equality (with an optional negate for `!=`).
- `Compare` — an ordered numeric comparison (`<`, `<=`, `>`, `>=`, encoded in the operand).
- `Not` — boolean negation.
- `AllStep` — one arm of `all`, with a real **short-circuit forward jump** to a shared landing site.
- `PushTrue` — the `all` identity, pushed after its last arm's landing site.
- `InStringSet` — a compact `match` membership test: is the popped string id in a contiguous label set?

## The accepted subset, and why it refuses

The compiler is deliberately conservative. It accepts a filter only when the native result is provably
byte-identical to the managed one, and **refuses** (returns `false`, falling back to managed) otherwise —
refusal is always safe, so the fence is drawn wide. The load-bearing refusals:

- **A statically-Boolean root** is required — the compiler works from the normalised expression-dialect
  JSON, whose `==` / `!=` / `!` parse to opaque managed closures, so it compiles from JSON shape, not the
  parsed expression tree.
- **String equality is by value-string *id*, not bytes.** Two shapes where that diverges from a managed
  byte compare are refused: `literal == literal` (two distinct absent literals both rebind to the `-1`
  sentinel and would read equal) and `get == get` (equal bytes at distinct ids read unequal). The safe
  `Equal` shape is exactly one dynamic operand against one literal.
- **Ordered comparisons** accept exactly one `get` against one numeric literal — never a string compare,
  whose ordinal bytes the id representation cannot reproduce.
- **`match`** compiles only its single-arm, complementary-output form; a `get`-input all-string-label match
  takes the compact `InStringSet` path, every other accepted shape rewrites to `!=`/`all`/`!` and recurses.

## Error model (`NativeFilterError`)

Burst forbids exceptions, so every erroring opcode **threads an error code** instead of throwing; a
non-`None` code halts evaluation immediately and maps the outcome to `matched = false` — exactly the
exclude a managed `try/catch` around `CompiledFilter.Matches` produces. `NonBoolean` and `NonComparable`
mirror specific managed throws; `StackOverflow`, `StepBudget` and `NoFeature` are **defensive** —
compile-time impossible for an accepted program (bounded stack depth, op count and step count), and even if
that guarantee were ever broken they map to *exclude*, never a silent *include*.

## Bounds

The program, operand stack and literal table are all bounded so nothing in the hot path is unbounded or
allocating: the op list and operand stack are fixed-capacity native lists (their capacity is read off the
container type, never re-hardcoded, so a size change can't drift from the compiler's refusal bound), and a
literal cap bounds the binding array and the per-feature `InStringSet` scan. A filter that would exceed any
bound is refused, not truncated.
