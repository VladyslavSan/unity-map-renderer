# specs/ — normative specifications

A **spec** states the *contract* a mechanism must honour. It is the authoritative source of truth for
that contract: where the code and a spec disagree, **the code has a bug** — not the other way round. This
is the one thing that separates a spec from a design doc, and it is the whole reason the directory exists.

## spec vs. design doc

`docs/*-design.md` and `specs/*.md` are different artifacts with opposite authority. Do not merge them, and
do not let a spec decay into a summary of its design doc.

| | `docs/*-design.md` | `specs/*.md` |
|---|---|---|
| answers | *why is it this way?* | *what is the rule?* |
| authority | **follows the code** — code leads, the doc explains | **leads the code** — the spec is the contract, code conforms |
| a disagreement means | the **doc is stale** | the **code has a bug** |
| tense | past — a decision log + history | present — a standing contract |
| contains | symptom, decisions, *rejected alternatives*, stage history, provenance, review findings | numbered requirements, definitions, the property surface, conformance mapping |
| changes when | you make a decision or land a stage | the **contract itself** changes |
| tied to | commits, stages, reviews | nothing — version-independent |

The tell: a design doc says *"the five divisions were collapsed onto one conversion in Stage 3."* The spec
says *"there is exactly one device→logical conversion; all physical measurements route through it."* Same
fact — the spec drops the *when* and the *count-before*, because those are history.

## How to write one

- **Every line is a numbered requirement, a definition needed to state one, or a conformance mapping.**
  Anything that *argues* belongs in the design doc — cite it (`see …-design.md §X`), do not reproduce it.
- **Requirement language is RFC 2119** — `MUST` / `MUST NOT` / `SHOULD` / `MAY`. Each requirement is
  falsifiable and, where possible, carries a **conformance** entry naming the tooth that pins it.
- **Requirements are numbered `R-n`** and cited by number in reviews and commit messages
  (*"rejected — violates SPEC-DPR R-9"*). Numbers are stable; when a requirement is retired, leave the
  number tombstoned rather than renumbering.
- **Scope requirements to live production paths.** A spec that the current tree silently violates is worse
  than none. Where the tree deliberately deviates, enumerate it in a **Known non-conformance** section —
  that is what makes the spec authoritative rather than aspirational.
- **State what *is*, not what was rejected.** A rejected alternative is design-doc material.
- **Say what is unspecified.** An open decision (one still awaiting the maintainer) is `unspecified` /
  `implementation-defined`; the spec points at the design doc rather than enshrining a provisional value.

## Index

| spec | governs | design doc (the *why*) |
|---|---|---|
| [`device-pixel-ratio.md`](device-pixel-ratio.md) — **SPEC-DPR** | the one logical-pixel convention: how a style's `px` values map to physical pixels, given a device-pixel ratio | [`docs/device-pixel-ratio-design.md`](../docs/device-pixel-ratio-design.md) |
