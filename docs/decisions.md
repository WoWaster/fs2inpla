# Design decisions

A short decision log. Each entry records what was decided, why, and what it costs. The plan they come from is in [plan.md](plan.md).

## D1. The translator is written in F#
It gets direct access to the F# compiler's own parser and type checker through FSharp.Compiler.Service (FCS).

## D2. Typed front end only (FCS typed tree, `FSharpExpr`)
- **Decided:** the input must type-check. We translate the checked tree, not the syntax tree.
- **Why:**
  - Names and operators come out already resolved: `+` is `Operators.(+)` at `int`.
  - `match` is already compiled to decision trees (`UnionCaseTest`/`UnionCaseGet`).
  - `&&`/`||` are already lowered to `if`, and recursion groups are explicit.
  - Ill-typed programs are rejected with real F# errors instead of leaving a stuck net.
- **Cost:**
  - Untyped λ-terms such as `fun x -> x x`, Ω and Y are out of scope.
  - Each run type-checks the file, which takes about 1–3 s.
- **Possible later:** a raw untyped front end can target the same `Core` IR.

## D3. λ-calculus encoding: `Lam`/`App`/`Era` plus labelled duplicators (`LDup`)
- **Decided:** use the "abstract algorithm" (as in HVM). Every `LDup` node created by the compiler gets a fresh integer label. Two `LDup`s with equal labels annihilate; with different labels they commute.
- **Why:** it is simple and fast, and it gives the right answer for the programs we target.
- **Cost:** without Lamping's bracket/croissant oracle, some terms that duplicate themselves reduce incorrectly.

## D4. Recursion: each recursive function becomes an agent (`Fn_…`), not a Y combinator
- **Decided:** a recursive group is closure-converted. Each function becomes an agent whose ports are the captured variables of the group, plus a rule `Fn >< App` that builds the body.
- **Why:**
  - Inpla reduces every active pair, so Y would unfold forever.
  - Y is also exactly the self-duplication case where D3 breaks.

## D5. `if` and list `match` become case agents (`If<k>`, `Case<k>`)
- **Decided:**
  - The case agent's principal port faces the scrutinee.
  - Each branch exists only in the right-hand side of a rule, so it is built only when `True`/`False`/`[]`/`h:t` arrives.
  - A variable used in both branches is passed once, without a Dup.
- **Why:** this is what makes recursion terminate under Inpla's eager reduction.
- **Cost:** a recursive call that is not guarded by `if`/`match` still diverges, even under a λ.

## D6. Reuse Inpla's built-in integers and lists; prefix our own agents
- **Decided:**
  - Ints are Inpla attributes, and lists are Inpla's built-in `[]`/`:`. Results print natively, e.g. `[1,2,3]`.
  - Our agents use distinct names (`LDup`, `Era`, `IAdd`, …). `Dup`, `Eraser`, `Add`, `Cons`, `S`, `Z`, … are reserved by Inpla.
  - Arithmetic uses our own two-step agents (`IAdd` → `IAddR`), not the built-in `Add`. That way we can define `LDup`/`Era` rules for them, which are needed when a λ containing arithmetic is copied or erased.

## D7. Every agent gets generic `LDup` and `Era` rules
`LDup` and `Era` can meet *any* agent, including consumers such as `App`, `If<k>` and `IAdd`, when a λ is copied or erased. So NetGen generates a copy rule and an erase rule for every agent shape, both static and generated.

## D8. Port packing
- **Decided:** Inpla's `MAX_PORT` is 5. A logical agent with more ports keeps its first 4 and packs the rest into nested built-in tuples. Rule bodies unpack them with tuple annihilation.
- **Test:** `examples/packing.fsx` exercises this.

## D9. Top-level values are inlined, top-level functions form one recursive group
- **Decided:** top-level functions become global `Fn_…` agents. Top-level non-function values are substituted at each use. The program's result is `main`.
- **Cost:** a top-level value used *n* times is computed *n* times. The result is still correct because the code is pure.

## D10. Inpla is vendored as a git submodule (`third_party/inpla`)
It is pinned to a known commit so the end-to-end tests are reproducible. `fs2inpla --run` looks for the binary in this order: `$INPLA`, `PATH`, `third_party/inpla/inpla`.
