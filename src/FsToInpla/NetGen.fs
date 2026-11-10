/// Compiles closure-converted Core into Inpla rules and a net.
module FsToInpla.NetGen

open System.Collections.Generic
open FsToInpla.Ast
open FsToInpla.Net
open FsToInpla.Closure

/// Physical shape of an agent, used to derive its generic Dup/Era rules.
type Shape =
    | Named of string * isAttr: bool list
    | ConsShape
    | NilShape
    | IntShape
    | TupShape of int

let private era = A("Era", [])
let private ldup (label: T) (a: T) (b: T) = A("LDup", [ label; a; b ])

let private arith = [ Add, "IAdd", "+"; Sub, "ISub", "-"; Mul, "IMul", "*"; Div, "IDiv", "/"; Mod, "IMod", "%" ]
let private cmps = [ Eq, "IEq", "=="; Ne, "INe", "!="; Lt, "ILt", "<"; Le, "ILe", "<="; Gt, "IGt", ">"; Ge, "IGe", ">=" ]
let private opAgent op =
    (arith @ cmps) |> List.find (fun (o, _, _) -> o = op) |> fun (_, a, _) -> a

let private staticShapes : Shape list =
    [ Named("Lam", [ false; false ]); Named("App", [ false; false ])
      Named("True", []); Named("False", [])
      ConsShape; NilShape; IntShape
      TupShape 2; TupShape 3; TupShape 4; TupShape 5
      Named("LDup", [ true; false; false ]) ]
    @ [ for (_, a, _) in arith @ cmps do
          yield Named(a, [ false; false ])
          yield Named(a + "R", [ false; true ]) ]

let private staticRules : Rule list =
    let r1 l r eqs = { Left = l; Right = r; Cases = [ "", eqs ] }
    [ yield r1 (PAgent("Lam", [ PName "v"; PName "b" ])) (PAgent("App", [ PName "a"; PName "r" ])) [ N "v", N "a"; N "b", N "r" ]
      yield
          { Left = PAgent("LDup", [ PInt "l"; PName "p"; PName "q" ])
            Right = PAgent("LDup", [ PInt "m"; PName "s"; PName "t" ])
            Cases =
              [ "l==m", [ N "p", N "s"; N "q", N "t" ]
                "", [ N "p", ldup (Expr "m") (N "a1") (N "a2"); N "q", ldup (Expr "m") (N "b1") (N "b2")
                      N "s", ldup (Expr "l") (N "a1") (N "b1"); N "t", ldup (Expr "l") (N "a2") (N "b2") ] ] }
      yield r1 (PAgent("Era", [])) (PAgent("Era", [])) []
      for (_, a, sym) in arith do
          yield r1 (PAgent(a, [ PName "r"; PName "y" ])) (PIntLit "x") [ A(a + "R", [ N "r"; Expr "x" ]), N "y" ]
          yield r1 (PAgent(a + "R", [ PName "r"; PInt "x" ])) (PIntLit "y") [ N "r", Expr(sprintf "(x%sy)" sym) ]
      for (_, a, sym) in cmps do
          yield r1 (PAgent(a, [ PName "r"; PName "y" ])) (PIntLit "x") [ A(a + "R", [ N "r"; Expr "x" ]), N "y" ]
          yield
              { Left = PAgent(a + "R", [ PName "r"; PInt "x" ])
                Right = PIntLit "y"
                Cases = [ sprintf "x%sy" sym, [ N "r", A("True", []) ]; "", [ N "r", A("False", []) ] ] } ]

/// Pattern of a shape (with port names x1.., attribute names n1..) and two copies of it.
let private shapeParts (s: Shape) : Pat * (string * bool) list * (T list -> T) =
    let ports isAttr = isAttr |> List.mapi (fun i a -> (if a then sprintf "n%d" (i + 1) else sprintf "x%d" (i + 1)), a)
    match s with
    | Named (name, attrs) ->
        let ps = ports attrs
        PAgent(name, ps |> List.map (fun (n, a) -> if a then PInt n else PName n)), ps, (fun args -> A(name, args))
    | ConsShape -> PCons("x1", "x2"), [ "x1", false; "x2", false ], (fun args -> ConsT(args[0], args[1]))
    | NilShape -> PNil, [], (fun _ -> NilT)
    | IntShape -> PIntLit "n1", [ "n1", true ], (fun args -> args[0])
    | TupShape k ->
        let ps = ports (List.replicate k false)
        PTup(List.map fst ps), ps, Tup

let private dupRule (s: Shape) : Rule =
    let pat, ports, mk = shapeParts s
    let copy prefix = ports |> List.map (fun (n, a) -> if a then Expr n else N(prefix + n))
    let eqs =
        [ yield N "p", mk (copy "a")
          yield N "q", mk (copy "b")
          for (n, a) in ports do
              if not a then yield N n, ldup (Expr "l") (N("a" + n)) (N("b" + n)) ]
    { Left = PAgent("LDup", [ PInt "l"; PName "p"; PName "q" ]); Right = pat; Cases = [ "", eqs ] }

let private eraRule (s: Shape) : Rule =
    let pat, ports, _ = shapeParts s
    { Left = PAgent("Era", []); Right = pat
      Cases = [ "", [ for (n, a) in ports do if not a then yield N n, era ] ] }

type Output =
    { Rules: Rule list
      Net: Eq list }

type private Gen() =
    let mutable counter = 0
    let mutable labels = 0
    let rules = ResizeArray<Rule>()
    let shapes = ResizeArray<Shape>(staticShapes)
    let defined = HashSet<string>()

    member _.Fresh(prefix: string) =
        counter <- counter + 1
        sprintf "%s%d" prefix counter

    member _.Label() =
        labels <- labels + 1
        I labels

    member _.Rules = rules
    member _.Shapes = shapes

    /// Makes `term` available to the k occurrences of x.
    member this.Bind(env: Map<string, Queue<T>>, eqs: ResizeArray<Eq>, x: string, term: T, k: int) =
        let q = Queue<T>()
        if k = 0 then eqs.Add(term, era)
        elif k = 1 then q.Enqueue term
        else
            let outs = List.init k (fun _ -> this.Fresh "o")
            let rec tree (names: string list) =
                match names with
                | [ a; b ] -> ldup (this.Label()) (N a) (N b)
                | a :: rest -> ldup (this.Label()) (N a) (tree rest)
                | [] -> failwith "unreachable"
            eqs.Add(term, tree outs)
            for o in outs do q.Enqueue(N o)
        env.Add(x, q)

    /// Compiles a closed region (net or rule body): binds the given variables and connects the result.
    member this.Region(bindings: (string * T) list, body: Core, result: T) : Eq list =
        let eqs = ResizeArray<Eq>()
        let env = bindings |> List.fold (fun env (x, t) -> this.Bind(env, eqs, x, t, uses x body)) Map.empty
        let t = this.Compile(env, eqs, body)
        eqs.Add(result, t)
        List.ofSeq eqs

    /// Defines a case agent whose rules get the captured variables `vs` as ports.
    member this.CaseAgent(prefix: string, vs: string list, alts: (Pat * (string * T) list * Core) list) =
        let agent = this.Fresh prefix
        let rr = this.Fresh "r"
        let portNames = vs |> List.map (fun _ -> this.Fresh "p")
        let ports, unpack = packPorts (fun () -> this.Fresh "k") (rr :: portNames)
        shapes.Add(Named(agent, List.replicate ports.Length false))
        for (pat, extra, body) in alts do
            let binds = List.zip vs (portNames |> List.map N) @ extra
            rules.Add { Left = PAgent(agent, ports); Right = pat; Cases = [ "", unpack @ this.Region(binds, body, N rr) ] }
        agent

    member this.Compile(env: Map<string, Queue<T>>, eqs: ResizeArray<Eq>, e: Core) : T =
        let comp x = this.Compile(env, eqs, x)
        let take x =
            match env.TryFind x with
            | Some q when q.Count > 0 -> q.Dequeue()
            | _ -> failwithf "internal error: no occurrence wire left for %s" x
        match e with
        | Var x -> take x
        | Lam (x, b) ->
            let v = this.Fresh "v"
            let env' = this.Bind(env, eqs, x, N v, uses x b)
            A("Lam", [ N v; this.Compile(env', eqs, b) ])
        | App (f, a) ->
            let r = this.Fresh "w"
            let tf = comp f
            let ta = comp a
            eqs.Add(tf, A("App", [ ta; N r ]))
            N r
        | Let (x, e1, b) ->
            let t1 = comp e1
            this.Compile(this.Bind(env, eqs, x, t1, uses x b), eqs, b)
        | Int n -> I n
        | Bool b -> A((if b then "True" else "False"), [])
        | Nil -> NilT
        | Cons (h, t) ->
            let th = comp h
            ConsT(th, comp t)
        | Prim (op, a, b) ->
            let r = this.Fresh "w"
            let ta = comp a
            let tb = comp b
            eqs.Add(ta, A(opAgent op, [ N r; tb ]))
            N r
        | If (c, t, f) ->
            let vs = Set.union (fv t) (fv f) |> List.ofSeq
            let agent =
                this.CaseAgent("If", vs, [ PAgent("True", []), [], t; PAgent("False", []), [], f ])
            let r = this.Fresh "w"
            let ports = vs |> List.map take
            eqs.Add(comp c, A(agent, packArgs (N r :: ports)))
            N r
        | CaseList (s, n, h, t, c) ->
            let vs = Set.union (fv n) (fv c |> Set.remove h |> Set.remove t) |> List.ofSeq
            let hn, tn = this.Fresh "h", this.Fresh "t"
            let agent =
                this.CaseAgent("Case", vs, [ PNil, [], n; PCons(hn, tn), [ h, N hn; t, N tn ], c ])
            let r = this.Fresh "w"
            let ports = vs |> List.map take
            eqs.Add(comp s, A(agent, packArgs (N r :: ports)))
            N r
        | LetRecC (defs, b) ->
            for d in defs do
                if defined.Add d.Agent then
                    let portNames = d.Fvs |> List.map (fun _ -> this.Fresh "p")
                    let ports, unpack = packPorts (fun () -> this.Fresh "k") portNames
                    shapes.Add(Named(d.Agent, List.replicate ports.Length false))
                    let a, rr = this.Fresh "a", this.Fresh "r"
                    let binds = List.zip d.Fvs (portNames |> List.map N) @ [ d.Param, N a ]
                    rules.Add
                        { Left = PAgent(d.Agent, ports)
                          Right = PAgent("App", [ PName a; PName rr ])
                          Cases = [ "", unpack @ this.Region(binds, d.Body, N rr) ] }
            comp b
        | FnRef (agent, fvs) -> A(agent, packArgs (fvs |> List.map take))
        | LetRec _ -> failwith "internal error: LetRec must be closure-converted first"

/// Name of the interface name that receives the program's result.
let root = "r"

let generate (program: Core) : Output =
    let g = Gen()
    let net = g.Region([], program, N root)
    let dupShapes = g.Shapes |> Seq.filter (function Named ("LDup", _) -> false | _ -> true) |> List.ofSeq
    let generic =
        (dupShapes |> List.map dupRule) @ (g.Shapes |> Seq.map eraRule |> List.ofSeq)
    { Rules = staticRules @ generic @ List.ofSeq g.Rules; Net = net }
