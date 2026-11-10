/// Inpla terms, rules, port packing and pretty printing.
module FsToInpla.Net

/// Inpla allows at most this many ports per agent (MAX_PORT in Inpla's config.h).
let maxPorts = 5

type T =
    | N of string // name
    | I of int // integer (anonymous attribute agent)
    | Expr of string // attribute expression in a rule RHS, e.g. "(x+y)" or "l"
    | A of string * T list // agent
    | ConsT of T * T // built-in list cell  h:t
    | NilT // built-in []
    | Tup of T list // built-in tuple (2..5)

type Eq = T * T

/// Port of an agent pattern on a rule's left-hand side.
type Port =
    | PName of string
    | PInt of string

/// Agent pattern on a rule's left-hand side.
type Pat =
    | PAgent of string * Port list
    | PCons of string * string
    | PNil
    | PIntLit of string // (int n)
    | PTup of string list

type Rule =
    {
        Left: Pat
        Right: Pat
        /// Guarded alternatives; a rule without guards has a single ("", body) entry.
        Cases: (string * Eq list) list
    }

let rec print (t: T) : string =
    match t with
    | N n -> n
    | I n -> if n < 0 then sprintf "(0-%d)" (-(int64 n)) else string n
    | Expr s -> s
    | A(name, []) -> name
    | A(name, args) -> sprintf "%s(%s)" name (args |> List.map print |> String.concat ",")
    | ConsT(h, t) -> sprintf "(%s:%s)" (print h) (print t)
    | NilT -> "[]"
    | Tup ts -> sprintf "(%s)" (ts |> List.map print |> String.concat ",")

let printPat (p: Pat) : string =
    let port =
        function
        | PName n -> n
        | PInt n -> "int " + n

    match p with
    | PAgent(name, []) -> name
    | PAgent(name, ps) -> sprintf "%s(%s)" name (ps |> List.map port |> String.concat ", ")
    | PCons(h, t) -> sprintf "%s:%s" h t
    | PNil -> "[]"
    | PIntLit n -> sprintf "(int %s)" n
    | PTup ns -> sprintf "(%s)" (String.concat ", " ns)

let printEq ((a, b): Eq) = sprintf "%s ~ %s" (print a) (print b)

let printRule (r: Rule) : string =
    let head = sprintf "%s >< %s" (printPat r.Left) (printPat r.Right)

    let body eqs =
        eqs |> List.map printEq |> String.concat ", "

    match r.Cases with
    | [ ("", eqs) ] -> sprintf "%s\n  => %s;" head (body eqs)
    | cases ->
        let alts =
            cases
            |> List.map (fun (g, eqs) -> sprintf "\n  | %s => %s" (if g = "" then "_" else g) (body eqs))

        sprintf "%s%s;" head (String.concat "" alts)

// ---------------------------------------------------------------------------
// Port packing: agents with more than maxPorts logical ports keep the first
// (maxPorts-1) ports and put the rest into nested built-in tuples.
// ---------------------------------------------------------------------------

let rec packTup (ts: T list) : T =
    match ts with
    | [ t ] -> t
    | _ when ts.Length <= maxPorts -> Tup ts
    | _ -> Tup(List.take (maxPorts - 1) ts @ [ packTup (List.skip (maxPorts - 1) ts) ])

/// Physical argument list for an agent term.
let packArgs (ts: T list) : T list =
    if ts.Length <= maxPorts then
        ts
    else
        List.take (maxPorts - 1) ts @ [ packTup (List.skip (maxPorts - 1) ts) ]

/// Physical pattern ports for an agent whose logical ports are all names, plus the
/// equations that unpack the tuple port in the rule body.
let packPorts (fresh: unit -> string) (names: string list) : Port list * Eq list =
    if names.Length <= maxPorts then
        names |> List.map PName, []
    else
        let k = fresh ()
        let ports = (List.take (maxPorts - 1) names |> List.map PName) @ [ PName k ]
        ports, [ N k, packTup (List.skip (maxPorts - 1) names |> List.map N) ]

// ---------------------------------------------------------------------------
// Linearity check: every name must occur exactly twice.
// ---------------------------------------------------------------------------

let rec private namesOf (t: T) : string list =
    match t with
    | N n -> [ n ]
    | I _
    | Expr _
    | NilT -> []
    | A(_, ts)
    | Tup ts -> List.collect namesOf ts
    | ConsT(h, t) -> namesOf h @ namesOf t

let private patNames (p: Pat) =
    match p with
    | PAgent(_, ps) ->
        ps
        |> List.choose (function
            | PName n -> Some n
            | PInt _ -> None)
    | PCons(h, t) -> [ h; t ]
    | PTup ns -> ns
    | PNil
    | PIntLit _ -> []

let private badCounts (names: string list) (allowedOnce: Set<string>) =
    names
    |> List.countBy id
    |> List.filter (fun (n, c) -> if allowedOnce.Contains n then c <> 1 else c <> 2)

let checkRule (r: Rule) : string list =
    let lhs = patNames r.Left @ patNames r.Right

    [
        for (g, eqs) in r.Cases do
            let names = lhs @ (eqs |> List.collect (fun (a, b) -> namesOf a @ namesOf b))

            for (n, c) in badCounts names Set.empty do
                yield
                    sprintf
                        "rule %s >< %s%s: name '%s' occurs %d times"
                        (printPat r.Left)
                        (printPat r.Right)
                        (if g = "" then "" else " | " + g)
                        n
                        c
    ]

let checkNet (eqs: Eq list) (roots: string list) : string list =
    let names = eqs |> List.collect (fun (a, b) -> namesOf a @ namesOf b)

    [
        for (n, c) in badCounts names (Set.ofList roots) -> sprintf "net: name '%s' occurs %d times" n c
    ]
