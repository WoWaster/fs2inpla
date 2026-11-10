module FsToInpla.Program

open System
open System.IO
open System.Diagnostics
open FsToInpla.Ast

let usage =
    """usage: fs2inpla <input.fsx> [-o <out.in>] [--run] [--dump-core] [--dump-typed]

  -o <file>      write the Inpla program to <file> (default: stdout)
  --run          run the program with inpla and print its result
                 (inpla is looked up in $INPLA, then PATH, then third_party/inpla/inpla)
  --dump-core    print the Core IR (after closure conversion) and stop
  --dump-typed   print the FCS typed tree and stop"""

/// Builds the whole program as Core: all top-level functions form one recursive group.
let toCore (p: Frontend.Program) : Core =
    let core = if p.Globals.IsEmpty then p.Main else LetRec(p.Globals, p.Main)
    Closure.convert core

let compile (path: string) : string =
    Checker.check path |> Frontend.translate |> toCore |> NetGen.generate |> Emit.render

let findInpla () =
    let env = Environment.GetEnvironmentVariable "INPLA"
    let onPath =
        (Environment.GetEnvironmentVariable "PATH" |> Option.ofObj |> Option.defaultValue "").Split ':'
        |> Array.map (fun d -> Path.Combine(d, "inpla"))
        |> Array.tryFind File.Exists
    let rec upwards (dir: DirectoryInfo) =
        if isNull dir then None
        else
            let p = Path.Combine(dir.FullName, "third_party", "inpla", "inpla")
            if File.Exists p then Some p else upwards dir.Parent
    if not (String.IsNullOrEmpty env) then Some env
    else onPath |> Option.orElse (upwards (DirectoryInfo(Directory.GetCurrentDirectory())))
           |> Option.orElse (upwards (DirectoryInfo(AppContext.BaseDirectory)))

/// Runs inpla on a program (given as text) and returns its stdout.
let runInpla (program: string) : Result<string, string> =
    match findInpla () with
    | None -> Error "inpla not found (set INPLA, put it on PATH, or build third_party/inpla)"
    | Some exe ->
        let psi = ProcessStartInfo(exe, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true)
        use p = Process.Start psi
        p.StandardInput.Write program
        p.StandardInput.Close()
        let out = p.StandardOutput.ReadToEnd()
        let err = p.StandardError.ReadToEnd()
        p.WaitForExit()
        if p.ExitCode = 0 then Ok out else Error(out + err)

[<EntryPoint>]
let main argv =
    let args = List.ofArray argv
    let flag f = List.contains f args
    let rec opt name =
        function
        | n :: v :: _ when n = name -> Some v
        | _ :: rest -> opt name rest
        | [] -> None
    let inputs =
        args
        |> List.indexed
        |> List.filter (fun (i, a) -> not (a.StartsWith "-") && (i = 0 || args[i - 1] <> "-o"))
        |> List.map snd
    match inputs with
    | [ path ] ->
        try
            if flag "--dump-typed" then
                for f in Checker.check path do
                    for d in f.Declarations do printf "%s" (Dump.decl "" d)
            elif flag "--dump-core" then
                Checker.check path |> Frontend.translate |> toCore |> pretty "" |> printfn "%s"
            else
                let program = compile path
                match opt "-o" args with
                | Some out -> File.WriteAllText(out, program)
                | None -> if not (flag "--run") then printf "%s" program
                if flag "--run" then
                    match runInpla program with
                    | Ok out -> printf "%s" out
                    | Error e -> eprintfn "%s" e; exit 1
            0
        with
        | Checker.CheckError msg | Frontend.Unsupported msg ->
            eprintfn "%s" msg
            1
    | _ ->
        eprintfn "%s" usage
        2
