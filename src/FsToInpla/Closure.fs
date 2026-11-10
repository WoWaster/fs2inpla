/// Closure conversion of recursive groups and free-variable / use-count analysis.
module FsToInpla.Closure

open FsToInpla.Ast

/// Free variables (works both before and after closure conversion).
let rec fv (e: Core) : Set<string> =
    match e with
    | Var x -> Set.singleton x
    | Lam (x, b) -> Set.remove x (fv b)
    | App (a, b) | Prim (_, a, b) | Cons (a, b) -> Set.union (fv a) (fv b)
    | Let (x, e1, b) -> Set.union (fv e1) (Set.remove x (fv b))
    | LetRec (ds, b) ->
        let names = ds |> List.map fst |> Set.ofList
        Set.difference (Set.unionMany (fv b :: (ds |> List.map (snd >> fv)))) names
    // Definitions of a converted group are closed over their Fvs; only references (FnRef) consume them.
    | LetRecC (_, b) -> fv b
    | FnRef (_, fvs) -> Set.ofList fvs
    | Int _ | Bool _ | Nil -> Set.empty
    | If (c, t, f) -> Set.unionMany [ fv c; fv t; fv f ]
    | CaseList (s, n, h, t, c) -> Set.unionMany [ fv s; fv n; fv c |> Set.remove h |> Set.remove t ]

let private sanitize (s: string) = s |> String.filter System.Char.IsLetterOrDigit

/// Replaces every recursive group by LetRecC: each function becomes an agent carrying the
/// free variables of its group; references become FnRef(agent, fvs).
let convert (e: Core) : Core =
    let mutable counter = 0
    let rec go (subst: Map<string, string * string list>) (e: Core) : Core =
        let bind x = Map.remove x subst
        match e with
        | Var x ->
            match subst.TryFind x with
            | Some (agent, fvs) -> FnRef(agent, fvs)
            | None -> Var x
        | Lam (x, b) -> Lam(x, go (bind x) b)
        | App (a, b) -> App(go subst a, go subst b)
        | Prim (op, a, b) -> Prim(op, go subst a, go subst b)
        | Cons (a, b) -> Cons(go subst a, go subst b)
        | Let (x, e1, b) -> Let(x, go subst e1, go (bind x) b)
        | If (c, t, f) -> If(go subst c, go subst t, go subst f)
        | CaseList (s, n, h, t, c) -> CaseList(go subst s, go subst n, h, t, go (bind h |> Map.remove t) c)
        | Int _ | Bool _ | Nil | FnRef _ | LetRecC _ -> e
        | LetRec (ds, body) ->
            let names = ds |> List.map fst
            let raw = Set.difference (Set.unionMany (ds |> List.map (snd >> fv))) (Set.ofList names)
            // A reference to an enclosing recursive function really captures that function's fvs.
            let fvs =
                raw
                |> Seq.collect (fun x ->
                    match subst.TryFind x with
                    | Some (_, outer) -> outer
                    | None -> [ x ])
                |> Seq.distinct
                |> Seq.sort
                |> List.ofSeq
            let agents =
                names
                |> List.map (fun n ->
                    counter <- counter + 1
                    n, (sprintf "Fn_%s_%d" (sanitize n) counter, fvs))
            let subst' = agents |> List.fold (fun (m: Map<_, _>) (n, a) -> m.Add(n, a)) subst
            let defs =
                ds
                |> List.map (fun (n, d) ->
                    match go subst' d with
                    | Lam (p, b) -> { Agent = fst (snd (agents |> List.find (fst >> (=) n))); Fvs = fvs; Param = p; Body = b }
                    | _ -> failwithf "recursive binding %s is not a function" n)
            LetRecC(defs, go subst' body)
    go Map.empty e

/// Number of times `x` is consumed in the current net region. Branches of If/CaseList are
/// separate regions (rule right-hand sides): they consume each captured variable once.
let rec uses (x: string) (e: Core) : int =
    let u = uses x
    match e with
    | Var y -> if y = x then 1 else 0
    | Lam (y, b) -> if y = x then 0 else u b
    | App (a, b) | Prim (_, a, b) | Cons (a, b) -> u a + u b
    | Let (y, e1, b) -> u e1 + (if y = x then 0 else u b)
    | LetRec _ -> failwith "uses: LetRec must be closure-converted first"
    | LetRecC (_, b) -> u b
    | FnRef (_, fvs) -> fvs |> List.filter ((=) x) |> List.length
    | Int _ | Bool _ | Nil -> 0
    | If (c, t, f) -> u c + (if Set.contains x (Set.union (fv t) (fv f)) then 1 else 0)
    | CaseList (s, n, h, t, c) ->
        let captured = Set.union (fv n) (fv c |> Set.remove h |> Set.remove t)
        u s + (if Set.contains x captured then 1 else 0)
