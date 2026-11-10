module FsToInpla.Tests

open System
open System.IO
open Expecto
open FsToInpla

let repoRoot =
    let rec up (d: DirectoryInfo) =
        if File.Exists(Path.Combine(d.FullName, "FsToInpla.slnx")) then
            d.FullName
        else
            up d.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

let example name =
    Path.Combine(repoRoot, "examples", name + ".fsx")

/// Writes an F# snippet to a temporary .fsx file.
let snippet (src: string) =
    let dir =
        Path.Combine(Path.GetTempPath(), "fs2inpla-tests", Guid.NewGuid().ToString "N")

    Directory.CreateDirectory dir |> ignore
    let p = Path.Combine(dir, "Snippet.fsx")
    File.WriteAllText(p, src)
    p

let core path =
    Checker.check path |> Frontend.translate |> Program.toCore |> Ast.pretty ""

/// Expected results, cross-checked with `dotnet fsi` (printed in Inpla's syntax).
let examples =
    [
        "fact", "3628800"
        "fib", "6765"
        "lists", "385"
        "lists2", "[10,8,6,4,2,8,5]"
        "mutual", "False"
        "lambda", "15"
        "packing", "21"
    ]

let frontend =
    testList
        "frontend"
        [
            test "match on a list becomes CaseList with fields bound to h/t" {
                let c = core (example "lists")
                Expect.stringContains c "match xs" "CaseList"
                Expect.isFalse (c.Contains "Head") "no residual UnionCaseGet"
            }
            test "&& and not become if" {
                let c = core (snippet "let main = (1 < 2) && not (2 < 1)")
                Expect.stringContains c "(if (1 < 2) then (if (2 < 1) then false else true) else false)" "if-lowering"
            }
            test "let rec becomes an agent definition" {
                let c = core (example "fact")
                Expect.stringContains c "let rec Fn_fact" "agent"
            }
            test "unsupported construct message contains file and line" {
                let p = snippet "let main =\n    \"hello\""

                try
                    core p |> ignore
                    failtest "expected failure"
                with Frontend.Unsupported msg ->
                    Expect.stringContains msg "Snippet.fsx(2," "location"
            }
            test "type errors are reported" {
                let p = snippet "let main = 1 + true"
                Expect.throwsT<Checker.CheckError> (fun () -> core p |> ignore) "type error"
            }
            test "missing main is reported" {
                let p = snippet "let f x = x"
                Expect.throwsT<Frontend.Unsupported> (fun () -> core p |> ignore) "no main"
            }
        ]

let emission =
    testList
        "emission"
        [
            for (name, _) in examples do
                test (sprintf "%s renders with linear names" name) {
                    // Emit.render raises LinearityError if any name does not occur exactly twice.
                    let out = Program.compile (example name)
                    Expect.stringContains out "r;" "prints the root"
                    Expect.isTrue (out.TrimEnd().EndsWith "exit;") "ends with an explicit exit"
                }
            test "agents never exceed 5 ports" {
                let out = Program.compile (example "packing")

                let tooWide =
                    out.Split '\n'
                    |> Array.filter (fun l -> l.Contains "><")
                    |> Array.collect (fun l -> l.Split "><")
                    |> Array.filter (fun side ->
                        let s = side.Trim()
                        let i = s.IndexOf '('
                        i > 0 && s.Substring(i).Split(',').Length > 5)

                Expect.isEmpty tooWide "rule patterns with more than 5 ports"
            }
        ]

let endToEnd =
    let inpla = Program.findInpla ()

    testList
        "end-to-end (inpla)"
        [
            for (name, expected) in examples do
                test (sprintf "%s evaluates to %s" name expected) {
                    match inpla with
                    | None -> skiptest "inpla not found"
                    | Some _ ->
                        match Program.runInpla (Program.compile (example name)) with
                        | Error e -> failtestf "inpla failed: %s" e
                        | Ok out ->
                            let lines = out.Split('\n') |> Array.map (fun l -> l.Trim())
                            Expect.contains lines expected (sprintf "inpla output:\n%s" out)
                }
        ]

[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [] argv (testList "all" [ frontend; emission; endToEnd ])
