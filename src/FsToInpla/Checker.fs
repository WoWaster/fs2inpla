module FsToInpla.Checker

open System.IO
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Symbols
open FSharp.Compiler.Text

exception CheckError of string

let private fmt (d: FSharpDiagnostic) =
    sprintf "%s(%d,%d): error FS%04d: %s" d.FileName d.StartLine (d.StartColumn + 1) d.ErrorNumber d.Message

/// Type-checks a script and returns its implementation file(s).
let check (path: string) : FSharpImplementationFileContents list =
    let path = Path.GetFullPath path
    let source = File.ReadAllText path
    let checker = FSharpChecker.Create(keepAssemblyContents = true)
    let options, optDiags =
        checker.GetProjectOptionsFromScript(path, SourceText.ofString source, assumeDotNetFramework = false, useSdkRefs = true)
        |> Async.RunSynchronously
    let errors (ds: FSharpDiagnostic seq) =
        ds |> Seq.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error) |> Seq.map fmt |> List.ofSeq
    match errors optDiags with
    | [] -> ()
    | es -> raise (CheckError (String.concat "\n" es))
    let results = checker.ParseAndCheckProject options |> Async.RunSynchronously
    match errors results.Diagnostics with
    | [] -> results.AssemblyContents.ImplementationFiles
    | es -> raise (CheckError (String.concat "\n" es))
