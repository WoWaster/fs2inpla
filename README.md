# fs2inpla — F# to Inpla translator

> [!CAUTION]
> This is pure Claude-slop code!

Compiles a typed subset of F# into [Inpla](https://github.com/inpla/inpla) interaction nets.

```
dotnet run --project src/FsToInpla -- examples/fact.fsx            # print the .in program
dotnet run --project src/FsToInpla -- examples/fact.fsx -o fact.in
dotnet run --project src/FsToInpla -- examples/fact.fsx --run      # needs inpla
dotnet run --project src/FsToInpla -- examples/fact.fsx --dump-core
dotnet test                                                        # tests
```

Inpla is a git submodule. Clone with `git clone --recursive`, or run `git submodule update --init` in an existing clone. Build it with `make -C third_party/inpla`. `--run` looks for Inpla in `$INPLA`, then on `PATH`, then at `third_party/inpla/inpla`.

The design is documented in [docs/plan.md](docs/plan.md) and [docs/decisions.md](docs/decisions.md).

## Examples
Each `examples/*.fsx` has its translation published in [`examples/inpla/`](examples/inpla). These files run directly in Inpla, e.g. `third_party/inpla/inpla < examples/inpla/fact.in`.

| Example | Inpla result |
|---|---|
| [fact](examples/fact.fsx) → [fact.in](examples/inpla/fact.in) | `3628800` |
| [fib](examples/fib.fsx) → [fib.in](examples/inpla/fib.in) | `6765` |
| [lists](examples/lists.fsx) → [lists.in](examples/inpla/lists.in) | `385` |
| [lists2](examples/lists2.fsx) → [lists2.in](examples/inpla/lists2.in) | `[10,8,6,4,2,8,5]` |
| [mutual](examples/mutual.fsx) → [mutual.in](examples/inpla/mutual.in) | `False` |
| [lambda](examples/lambda.fsx) → [lambda.in](examples/inpla/lambda.in) | `15` |
| [packing](examples/packing.fsx) → [packing.in](examples/inpla/packing.in) | `21` |

They are golden files: `dotnet test` fails if the translator's output differs from them. After an intentional change to code generation, regenerate them with `UPDATE_GOLDEN=1 dotnet test` and review the diff.

## Supported subset
The program must type-check, and its result is the top-level value `main`. The translator supports:
- lambdas and application;
- `let`, and `let rec … and …`, both local and top-level;
- `int` literals with `+ - * / %` and `= <> < <= > >=`;
- `bool` literals with `if`, `&&`, `||`, `not`, and `=`/`<>` on bools;
- lists: `[]`, `::`, `[a; b]`, and `match` with list patterns.

Everything else is rejected with a source location. That includes tuples, records, user unions, strings, library calls and side effects.

## How it works
The pipeline is: FCS typed tree (`FSharpExpr`) → `Core` IR (`Frontend.fs`) → closure conversion of recursive groups (`Closure.fs`) → nets and rules (`NetGen.fs`) → Inpla text (`Emit.fs`).

- **λ-calculus.** Functions use `Lam`/`App` agents. Shared variables are copied by labelled duplicators (`LDup`), and unused ones are erased with `Era`. This is the "abstract algorithm".
- **Laziness for branches.** Inpla reduces every active pair. So each `if` and list `match` becomes a case agent (`If<k>`, `Case<k>`) whose branches exist only in the right-hand sides of its rules. A branch is built only when the scrutinee's constructor arrives.
- **Recursion.** Each recursive function becomes an agent `Fn_…` that carries its captured variables. Its rule against `App` instantiates the body, so recursion unfolds only on application and only inside the branch that was taken.
- **Port packing.** Inpla agents have at most 5 ports. Extra ports are packed into nested built-in tuples.
- **Data.** Ints, `[]`/`:` and `True`/`False` use Inpla's built-in integers and lists, so results print natively.

## Known limitations
- **Strong reduction.** A recursive call that is not guarded by `if`/`match` diverges, even under a lambda. Lazy or infinite structures are unsupported.
- **Labelled duplicators.** Static labels without Lamping's oracle can go wrong when a duplicated term duplicates itself with the same label. Types rule out most of these cases, but not all higher-order Church-numeral tricks.
- **Top-level values are inlined.** Top-level non-function values are substituted at every use, so a value used twice is recomputed. The result is the same, since the code is pure.

## Development
Code is formatted with [Fantomas](https://fsprojects.github.io/fantomas/), pinned as a local tool in `dotnet-tools.json`. Default settings are used, plus the indentation rules in `.editorconfig`.

```
dotnet tool restore                                  # once per clone
dotnet fantomas src tests examples                   # format
dotnet fantomas --check src tests examples           # verify (non-zero exit if unformatted)
```

## License
MIT. See [LICENSE](LICENSE). Inpla (`third_party/inpla`) is a separate project with its own license.
