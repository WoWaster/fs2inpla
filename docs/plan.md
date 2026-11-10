# Plan: F# → Inpla translator

This is the plan the implementation follows, updated to match what was actually built. The reasoning behind each choice is in [decisions.md](decisions.md).

## Goal
Compile a typed subset of F# into Inpla interaction nets. The target programs are factorial, fibonacci and list functions. The subset is:
- lambdas and application;
- `let` and `let rec … and …`;
- `int` literals with arithmetic and comparisons;
- `bool` with `if`/`&&`/`||`/`not`;
- lists (`[]`, `::`, `[a;b]`, `match`).

## Pipeline
```
.fsx ──FCS ParseAndCheckProject──► FSharpExpr ──Frontend──► Core
     ──Closure.convert──► Core (LetRecC/FnRef) ──NetGen──► rules + net ──Emit──► .in
```

| File | Role |
|---|---|
| `src/FsToInpla/Checker.fs` | Type-checks a script with FCS (`keepAssemblyContents = true`) and returns the implementation files. |
| `src/FsToInpla/Dump.fs` | Debug printer for the typed tree (`--dump-typed`). |
| `src/FsToInpla/Ast.fs` | The `Core` IR and its pretty printer (`--dump-core`). |
| `src/FsToInpla/Frontend.fs` | Translates `FSharpExpr` to `Core`. Decision trees are inlined; `UnionCaseTest`/`UnionCaseGet` on lists become `CaseList`. Anything else is rejected with its source location. |
| `src/FsToInpla/Closure.fs` | Closure-converts recursive groups (`FnRef`), and computes free variables and per-region use counts. |
| `src/FsToInpla/Net.fs` | Inpla terms, rules, printing, port packing and the linearity checker. |
| `src/FsToInpla/NetGen.fs` | Compiles `Core` to rules and a net; holds the static prelude and the generic `LDup`/`Era` rules. |
| `src/FsToInpla/Emit.fs` | Checks linearity and renders the `.in` file. |
| `src/FsToInpla/Program.fs` | The CLI: `fs2inpla <in.fsx> [-o out.in] [--run] [--dump-core] [--dump-typed]`. |
| `tests/FsToInpla.Tests` | Expecto tests: front end, emission/linearity, and end-to-end runs through Inpla. |

## Compilation scheme
Each term is compiled to an Inpla term, which stands for the port carrying its value.

- **Variables.** A variable used *k* times in its region is wired as follows:
  - *k* = 0: `t ~ Era`.
  - *k* = 1: the term is used directly.
  - *k* ≥ 2: a right-nested tree of `LDup`s, each node with a fresh label.
- **`fun x -> M`** becomes `Lam(vx, ⟦M⟧)`.
- **`M N`** becomes `⟦M⟧ ~ App(⟦N⟧, w)`, and the result is `w`.
- **`let x = E in B`:** bind `x` to `⟦E⟧`, then compile `B`.
- **`a op b`** becomes `⟦a⟧ ~ IOp(w, ⟦b⟧)`, using the rules `IOp(r,y) >< (int x) => IOpR(r,x) ~ y` and `IOpR(r,int x) >< (int y) => r ~ (x op y)`. Comparisons pick `True`/`False` with guards.
- **`if c then t else f`** becomes `⟦c⟧ ~ If<k>(w, captured…)`. The two rules `If<k> >< True` and `If<k> >< False` hold the branches.
- **`match s with [] -> n | h :: t -> c`** becomes `⟦s⟧ ~ Case<k>(w, captured…)`, with rules against `[]` and `h:t`.
- **`let rec f … and g …`:**
  - Each function becomes an agent `Fn_f(captured…)` with the rule `Fn_f >< App(a, r)`, which instantiates the body with `a` and connects the result to `r`.
  - Each reference to `f` becomes a fresh `Fn_f(…)`.
- **Root:** the root is `r ~ ⟦main⟧`, followed by `r;` to print it and `exit;`.

## Implementation steps
1. ✅ Install Inpla (submodule) and check its syntax and limits from the docs and source: `MAX_PORT = 5`, reserved names, attributes and guards.
2. ✅ Spike on the FCS typed tree (`--dump-typed`) to confirm the shapes of `DecisionTree` and `UnionCaseTest`.
3. ✅ Checker, Frontend, Core.
4. ✅ Closure conversion and use analysis.
5. ✅ NetGen (λ core, ints, `if`, lists, `let rec`, packing) and Emit (with the linearity check).
6. ✅ CLI (`--run`, `--dump-core`, `--dump-typed`), examples, tests.
7. ✅ Build Inpla (`make -C third_party/inpla`) and get the end-to-end tests green. Generated files end with an explicit `exit;`, because Inpla exits with status 255 at end of input.

## Verification
- `dotnet test` (or `dotnet run --project tests/FsToInpla.Tests`) covers:
  - the front end: `CaseList` from `match`, `if` from `&&`/`not`, error locations, type errors;
  - linear names in every generated rule and in the net;
  - no agent wider than 5 ports;
  - end-to-end runs through Inpla, skipped when Inpla is missing.
- Expected end-to-end results, cross-checked with `dotnet fsi`:

  | Example | Result |
  |---|---|
  | `fact` | `3628800` |
  | `fib` | `6765` |
  | `lists` | `385` |
  | `lists2` | `[10,8,6,4,2,8,5]` |
  | `mutual` | `False` |
  | `lambda` | `15` |
  | `packing` | `21` |

## Known limitations
- **Strong reduction.** An unguarded recursive call diverges.
- **Labelled duplicators.** The `LDup` caveat from D3 applies.
- **Top-level values.** They are recomputed at each use (D9).
- **Supported subset only.** Anything outside it is rejected.
