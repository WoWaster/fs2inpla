module FsToInpla.Dump

open FSharp.Compiler.Symbols
open FSharp.Compiler.Symbols.FSharpExprPatterns

/// Debug printer for the FCS typed tree (used by --dump-typed).
let rec expr (ind: string) (e: FSharpExpr) : string =
    let i2 = ind + "  "
    let sub (es: FSharpExpr list) = es |> List.map (fun x -> "\n" + i2 + expr i2 x) |> String.concat ""
    match e with
    | Value v -> sprintf "Value %s" v.DisplayName
    | Const (o, t) -> sprintf "Const %A : %s" o (t.Format(FSharp.Compiler.Symbols.FSharpDisplayContext.Empty))
    | Lambda (v, b) -> sprintf "Lambda %s ->%s" v.DisplayName (sub [b])
    | Application (f, _, args) -> sprintf "Application%s" (sub (f :: args))
    | Let ((v, b, _), body) -> sprintf "Let %s =%s" v.DisplayName (sub [b; body])
    | LetRec (bs, body) ->
        let names = bs |> List.map (fun (v, _, _) -> v.DisplayName) |> String.concat ", "
        sprintf "LetRec [%s]%s" names (sub ((bs |> List.map (fun (_, b, _) -> b)) @ [body]))
    | Call (obj, m, _, _, args) -> sprintf "Call %s (obj=%b)%s" m.FullName obj.IsSome (sub args)
    | IfThenElse (c, t, f) -> sprintf "IfThenElse%s" (sub [c; t; f])
    | NewUnionCase (_, uc, args) -> sprintf "NewUnionCase %s%s" uc.Name (sub args)
    | UnionCaseTest (x, _, uc) -> sprintf "UnionCaseTest %s%s" uc.Name (sub [x])
    | UnionCaseGet (x, _, uc, f) -> sprintf "UnionCaseGet %s.%s%s" uc.Name f.Name (sub [x])
    | DecisionTree (d, targets) ->
        let ts = targets |> List.mapi (fun k (vs, b) ->
                    sprintf "\n%starget %d (%s):%s" i2 k (vs |> List.map (fun v -> v.DisplayName) |> String.concat ",") (sub [b]))
        sprintf "DecisionTree%s%s" (sub [d]) (String.concat "" ts)
    | DecisionTreeSuccess (k, args) -> sprintf "DecisionTreeSuccess %d%s" k (sub args)
    | TypeLambda (_, b) -> sprintf "TypeLambda%s" (sub [b])
    | Coerce (_, x) -> sprintf "Coerce%s" (sub [x])
    | DebugPoint (_, x) -> sprintf "DebugPoint%s" (sub [x])
    | _ -> sprintf "<other: %A>" e

let rec decl (ind: string) (d: FSharpImplementationFileDeclaration) : string =
    match d with
    | FSharpImplementationFileDeclaration.Entity (ent, ds) ->
        sprintf "%sEntity %s\n%s" ind ent.FullName (ds |> List.map (decl (ind + "  ")) |> String.concat "")
    | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue (v, args, body) ->
        let argNames = args |> List.map (List.map (fun a -> a.DisplayName) >> String.concat ",") |> String.concat " | "
        sprintf "%sMFV %s [args: %s] (compilerGenerated=%b)\n%s%s\n" ind v.DisplayName argNames v.IsCompilerGenerated (ind + "  ") (expr (ind + "  ") body)
    | FSharpImplementationFileDeclaration.InitAction e -> sprintf "%sInitAction\n%s%s\n" ind (ind + "  ") (expr (ind + "  ") e)
