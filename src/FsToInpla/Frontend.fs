/// Translates the FCS typed tree (FSharpExpr) into Core.
module FsToInpla.Frontend

open System.Collections.Generic
open FSharp.Compiler.Symbols
open FSharp.Compiler.Symbols.FSharpExprPatterns
open FsToInpla.Ast

exception Unsupported of string

let private loc (r: FSharp.Compiler.Text.range) =
    sprintf "%s(%d,%d)" r.FileName r.StartLine (r.StartColumn + 1)

let private fail (e: FSharpExpr) (what: string) =
    raise (Unsupported (sprintf "%s: unsupported construct: %s" (loc e.Range) what))

let rec private strip (t: FSharpType) =
    if t.IsAbbreviation then strip t.AbbreviatedType else t

let private typeIs (name: string) (t: FSharpType) =
    let t = strip t
    t.HasTypeDefinition && t.TypeDefinition.TryFullName = Some name

let private isInt = typeIs "System.Int32"
let private isBool = typeIs "System.Boolean"

let private isListCase (uc: FSharpUnionCase) =
    match uc.Name with
    | "op_ColonColon" | "Cons" -> Some true
    | "op_Nil" | "Empty" -> Some false
    | _ -> None

let private isListType (t: FSharpType) =
    let t = strip t
    t.HasTypeDefinition
    && (match t.TypeDefinition.TryFullName with
        | Some "Microsoft.FSharp.Collections.FSharpList`1" -> true
        | _ -> false)

type Program =
    { /// Top-level functions (all treated as one recursive group).
      Globals: (string * Core) list
      Main: Core }

type private Ctx =
    { /// Access paths of list fields bound by an enclosing CaseList, e.g. "xs_3.Tail" -> "t_7".
      Gets: Map<string, string>
      /// Decision-tree targets of the innermost DecisionTree.
      Targets: (FSharpMemberOrFunctionOrValue list * FSharpExpr) list }

type private Translator(globalFns: Dictionary<string, string>, globalVals: Dictionary<string, FSharpExpr>) =
    let names = Dictionary<FSharpMemberOrFunctionOrValue, string>()
    let mutable counter = 0

    member _.Fresh(baseName: string) =
        counter <- counter + 1
        let clean = baseName |> String.filter System.Char.IsLetterOrDigit
        sprintf "%s_%d" (if clean = "" then "x" else clean) counter

    member this.Local(v: FSharpMemberOrFunctionOrValue) =
        match names.TryGetValue v with
        | true, n -> n
        | _ ->
            let n = this.Fresh v.DisplayName
            names[v] <- n
            n

    /// Access path of an expression (for matching UnionCaseGet against an enclosing UnionCaseTest).
    member this.Path(e: FSharpExpr) : string option =
        match e with
        | Value v when not v.IsModuleValueOrMember -> Some(this.Local v)
        | UnionCaseGet (x, _, uc, f) -> this.Path x |> Option.map (fun p -> sprintf "%s.%s.%s" p uc.Name f.Name)
        | DebugPoint (_, x) | Coerce (_, x) -> this.Path x
        | _ -> None

    member this.Global(e: FSharpExpr, v: FSharpMemberOrFunctionOrValue, ctx: Ctx) : Core =
        match globalFns.TryGetValue v.FullName with
        | true, n -> Var n
        | _ ->
            match globalVals.TryGetValue v.FullName with
            | true, body -> this.Expr({ ctx with Gets = Map.empty; Targets = [] }, body) // inline top-level values
            | _ -> fail e (sprintf "reference to '%s' (only operators and functions defined in this file are supported)" v.FullName)

    member this.Prim2(e: FSharpExpr, ctx: Ctx, m: FSharpMemberOrFunctionOrValue, args: FSharpExpr list) : Core option =
        let arith =
            dict [ "op_Addition", Add; "op_Subtraction", Sub; "op_Multiply", Mul; "op_Division", Div; "op_Modulus", Mod ]
        let cmp =
            dict [ "op_Equality", Eq; "op_Inequality", Ne; "op_LessThan", Lt; "op_LessThanOrEqual", Le
                   "op_GreaterThan", Gt; "op_GreaterThanOrEqual", Ge ]
        let isOperators = m.FullName.StartsWith "Microsoft.FSharp.Core.Operators."
        if not isOperators then None
        else
            let tr x = this.Expr(ctx, x)
            match m.CompiledName, args with
            | name, [ a; b ] when arith.ContainsKey name ->
                if isInt a.Type && isInt b.Type then Some(Prim(arith[name], tr a, tr b))
                else fail e (sprintf "operator %s is only supported on int" m.DisplayName)
            | name, [ a; b ] when cmp.ContainsKey name ->
                if isInt a.Type && isInt b.Type then Some(Prim(cmp[name], tr a, tr b))
                elif isBool a.Type && (name = "op_Equality" || name = "op_Inequality") then
                    // a = b on bools  ==>  if a then b else not b
                    let ta, tb = tr a, tr b
                    let eq = If(ta, tb, If(tb, Bool false, Bool true))
                    Some(if name = "op_Equality" then eq else If(eq, Bool false, Bool true))
                else fail e (sprintf "operator %s is only supported on int (and =/<> on bool)" m.DisplayName)
            | "Not", [ a ] -> Some(If(tr a, Bool false, Bool true))
            | "op_UnaryNegation", [ a ] when isInt a.Type -> Some(Prim(Sub, Int 0, tr a))
            | _ -> fail e (sprintf "operator or library function '%s'" m.DisplayName)

    member this.Expr(ctx: Ctx, e: FSharpExpr) : Core =
        let tr x = this.Expr(ctx, x)
        let apps f args = args |> List.fold (fun acc a -> App(acc, tr a)) f
        match e with
        | DebugPoint (_, x) | Coerce (_, x) | TypeLambda (_, x) -> tr x
        | Value v when v.IsModuleValueOrMember -> this.Global(e, v, ctx)
        | Value v -> Var(this.Local v)
        | Const (o, t) ->
            match o with
            | :? int as n when isInt t -> Int n
            | :? bool as b -> Bool b
            | _ -> fail e (sprintf "constant %A (only int and bool literals are supported)" o)
        | Lambda (v, b) -> Lam(this.Local v, tr b)
        | Application (f, _, args) -> apps (tr f) args
        | FSharpExprPatterns.Let ((v, b, _), body) -> Let(this.Local v, tr b, tr body)
        | FSharpExprPatterns.LetRec (binds, body) ->
            let defs =
                binds
                |> List.map (fun (v, b, _) ->
                    match tr b with
                    | Lam _ as l -> this.Local v, l
                    | _ -> fail b "'let rec' of a non-function value")
            LetRec(defs, tr body)
        | Call (None, m, _, _, args) ->
            match this.Prim2(e, ctx, m, args) with
            | Some c -> c
            | None -> apps (this.Global(e, m, ctx)) args
        | Call (Some _, m, _, _, _) -> fail e (sprintf "method call '%s'" m.DisplayName)
        | IfThenElse (UnionCaseTest (scrut, t, uc), thenE, elseE) when isListType t && (isListCase uc).IsSome ->
            this.Case(ctx, e, scrut, (isListCase uc).Value, thenE, elseE)
        | IfThenElse (c, t, f) -> If(tr c, tr t, tr f)
        | UnionCaseTest (scrut, t, uc) when isListType t && (isListCase uc).IsSome ->
            this.Case(ctx, e, scrut, (isListCase uc).Value, e, e, asBool = true)
        | NewUnionCase (t, uc, args) when isListType t ->
            match isListCase uc, args with
            | Some true, [ h; tl ] -> Cons(tr h, tr tl)
            | Some false, [] -> Nil
            | _ -> fail e "list constructor"
        | UnionCaseGet (x, t, uc, f) when isListType t ->
            match this.Path x |> Option.bind (fun p -> ctx.Gets.TryFind(sprintf "%s.%s.%s" p uc.Name f.Name)) with
            | Some n -> Var n
            | None -> fail e "list field access outside of a pattern match"
        | DecisionTree (d, targets) -> this.Expr({ ctx with Targets = targets }, d)
        | DecisionTreeSuccess (k, args) ->
            let vs, body = ctx.Targets[k]
            List.foldBack2 (fun (v: FSharpMemberOrFunctionOrValue) a acc -> Let(this.Local v, tr a, acc)) vs args (tr body)
        | NewUnionCase (_, uc, _) -> fail e (sprintf "union case '%s' (only lists are supported)" uc.Name)
        | _ -> fail e (sprintf "%A" e)

    /// Translates `if (scrut is Cons/Empty) then thenE else elseE` into a CaseList.
    member this.Case(ctx, e, scrut, testIsCons, thenE, elseE, ?asBool) : Core =
        let asBool = defaultArg asBool false
        let s = this.Expr(ctx, scrut)
        let h, t = this.Fresh "h", this.Fresh "t"
        let consCtx =
            match this.Path scrut with
            | Some p ->
                { ctx with
                    Gets =
                        ctx.Gets
                            .Add(sprintf "%s.op_ColonColon.Head" p, h)
                            .Add(sprintf "%s.op_ColonColon.Tail" p, t) }
            | None -> ctx
        if asBool then CaseList(s, Bool(not testIsCons), h, t, Bool testIsCons)
        elif testIsCons then CaseList(s, this.Expr(ctx, elseE), h, t, this.Expr(consCtx, thenE))
        else CaseList(s, this.Expr(ctx, thenE), h, t, this.Expr(consCtx, elseE))

/// Translates the checked implementation files into a Program.
let translate (files: FSharpImplementationFileContents list) : Program =
    let fns = ResizeArray<FSharpMemberOrFunctionOrValue * FSharpMemberOrFunctionOrValue list * FSharpExpr>()
    let globalFns = Dictionary<string, string>()
    let globalVals = Dictionary<string, FSharpExpr>()
    let mutable mainExpr = None
    let rec walk (d: FSharpImplementationFileDeclaration) =
        match d with
        | FSharpImplementationFileDeclaration.Entity (_, ds) -> List.iter walk ds
        | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue (v, args, body) ->
            if args.IsEmpty then
                globalVals[v.FullName] <- body
                if v.DisplayName = "main" then mainExpr <- Some body
            else
                for group in args do
                    match group with
                    | [ a ] when not (typeIs "Microsoft.FSharp.Core.Unit" a.FullType) -> ()
                    | _ -> raise (Unsupported (sprintf "%s: unsupported construct: tupled or unit parameters in '%s'" (loc v.DeclarationLocation) v.DisplayName))
                fns.Add((v, List.concat args, body))
        | FSharpImplementationFileDeclaration.InitAction e -> fail e "top-level side effect"
    for f in files do List.iter walk f.Declarations
    let tr = Translator(globalFns, globalVals)
    for (v, _, _) in fns do globalFns[v.FullName] <- tr.Fresh v.DisplayName
    let ctx = { Gets = Map.empty; Targets = [] }
    let globals =
        [ for (v, ps, body) in fns ->
              let b = tr.Expr(ctx, body)
              globalFns[v.FullName], List.foldBack (fun p acc -> Lam(tr.Local p, acc)) ps b ]
    match mainExpr with
    | None -> raise (Unsupported "no top-level value named 'main' (e.g. `let main = fact 10`)")
    | Some m -> { Globals = globals; Main = tr.Expr(ctx, m) }
