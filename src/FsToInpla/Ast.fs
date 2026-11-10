module FsToInpla.Ast

type PrimOp =
    | Add | Sub | Mul | Div | Mod
    | Eq | Ne | Lt | Le | Gt | Ge

/// Core IR. Variable names are unique strings minted by the frontend.
type Core =
    | Var of string
    | Lam of string * Core
    | App of Core * Core
    | Let of string * Core * Core
    /// Recursive group before closure conversion: (name, body) where body is a Lam.
    | LetRec of (string * Core) list * Core
    /// Recursive group after closure conversion (see Closure.fs).
    | LetRecC of RecDef list * Core
    /// Reference to a recursive function after closure conversion: agent name and captured variables.
    | FnRef of string * string list
    | Int of int
    | Bool of bool
    | Prim of PrimOp * Core * Core
    | If of Core * Core * Core
    | Nil
    | Cons of Core * Core
    /// match scrutinee with [] -> nilBranch | h :: t -> consBranch
    | CaseList of scrutinee: Core * nilBranch: Core * head: string * tail: string * consBranch: Core

and RecDef =
    { Agent: string
      Fvs: string list
      Param: string
      Body: Core }

let opName =
    function
    | Add -> "+" | Sub -> "-" | Mul -> "*" | Div -> "/" | Mod -> "%"
    | Eq -> "=" | Ne -> "<>" | Lt -> "<" | Le -> "<=" | Gt -> ">" | Ge -> ">="

let rec pretty (ind: string) (e: Core) : string =
    let i2 = ind + "  "
    match e with
    | Var x -> x
    | Lam (x, b) -> sprintf "(fun %s -> %s)" x (pretty ind b)
    | App (f, a) -> sprintf "(%s %s)" (pretty ind f) (pretty ind a)
    | Let (x, e1, b) -> sprintf "let %s = %s in\n%s%s" x (pretty i2 e1) ind (pretty ind b)
    | LetRec (ds, b) ->
        let defs = ds |> List.map (fun (n, d) -> sprintf "%s = %s" n (pretty i2 d)) |> String.concat (sprintf "\n%sand " ind)
        sprintf "let rec %s in\n%s%s" defs ind (pretty ind b)
    | LetRecC (ds, b) ->
        let defs =
            ds
            |> List.map (fun d -> sprintf "%s[%s] %s = %s" d.Agent (String.concat "," d.Fvs) d.Param (pretty i2 d.Body))
            |> String.concat (sprintf "\n%sand " ind)
        sprintf "let rec %s in\n%s%s" defs ind (pretty ind b)
    | FnRef (a, fvs) -> sprintf "%s[%s]" a (String.concat "," fvs)
    | Int n -> string n
    | Bool b -> if b then "true" else "false"
    | Prim (op, a, b) -> sprintf "(%s %s %s)" (pretty ind a) (opName op) (pretty ind b)
    | If (c, t, f) -> sprintf "(if %s then %s else %s)" (pretty ind c) (pretty i2 t) (pretty i2 f)
    | Nil -> "[]"
    | Cons (h, t) -> sprintf "(%s :: %s)" (pretty ind h) (pretty ind t)
    | CaseList (s, n, h, t, c) ->
        sprintf "(match %s with\n%s| [] -> %s\n%s| %s :: %s -> %s)" (pretty ind s) i2 (pretty i2 n) i2 h t (pretty i2 c)
