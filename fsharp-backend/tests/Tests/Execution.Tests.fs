module Tests.Execution

open System.Threading.Tasks
open FSharp.Control.Tasks

open Expecto

open Prelude
open Tablecloth
open TestUtils

open LibExecution.RuntimeTypes
open LibExecution.Shortcuts

module Exe = LibExecution.Execution
module Ast = LibExecution.Ast

module AT = LibExecution.AnalysisTypes
type Dictionary<'k, 'v> = System.Collections.Generic.Dictionary<'k, 'v>

let parse = FSharpToExpr.parseRTExpr

let executionStateForPreview
  (name : string)
  (dbs : Map<string, DB.T>)
  (fns : Map<string, UserFunction.T>)
  : Task<AT.AnalysisResults * ExecutionState> =
  task {
    let! state = executionStateFor name dbs fns
    let results, traceFn = Exe.traceDvals ()

    let state =
      Exe.updateTracing
        (fun t -> { t with traceDval = traceFn; realOrPreview = Preview })
        state

    return (results, state)
  }

let execSaveDvals
  (dbs : List<DB.T>)
  (userFns : List<UserFunction.T>)
  (ast : Expr)
  : Task<AT.AnalysisResults> =
  task {
    let fns = userFns |> List.map (fun fn -> fn.name, fn) |> Map.ofList
    let dbs = dbs |> List.map (fun db -> db.name, db) |> Map.ofList
    let! (results, state) = executionStateForPreview "test" dbs fns

    let inputVars = Map.empty
    let! _result = Exe.executeExpr state inputVars ast

    return results
  }


let testExecFunctionTLIDs : Test =
  testTask "test that exec function returns the right tlids in the trace" {
    let name = "testFunction"
    let fn = testUserFn name [] (eInt 5)
    let fns = Map.ofList [ (name, fn) ]
    let! state = executionStateFor "test" Map.empty fns

    let tlids, traceFn = Exe.traceTLIDs ()

    let state =
      Exe.updateTracing
        (fun t -> { t with traceTLID = traceFn; realOrPreview = Preview })
        state

    let! value = Exe.executeFunction state (gid ()) [] (FQFnName.User name)

    Expect.equal (HashSet.toList tlids) [ fn.tlid ] "tlid of function is traced"
    Expect.equal value (DInt 5I) "sanity check"
  }


let testErrorRailUsedInAnalysis : Test =
  testTask "When a function which isn't available on the client has analysis data, we need to make sure we process the errorrail functions correctly" {

    let! state = executionStateFor "test" Map.empty Map.empty

    let loadTraceResults _ _ = Some(DOption(Some(DInt 12345I)), System.DateTime.Now)

    let state =
      { state with
          tracing =
            { state.tracing with
                loadFnResult = loadTraceResults
                realOrPreview = Preview } }

    let inputVars = Map.empty
    let ast = eFnRail "" "fake_test_fn" 0 [ eInt 4; eInt 5 ]

    let! result = Exe.executeExpr state inputVars ast

    Expect.equal result (DInt 12345I) "is on the error rail"
  }

let testOtherDbQueryFunctionsHaveAnalysis : Test =
  testTask "The SQL compiler inserts analysis results, but I forgot to support DB:queryOne and friends." {
    let varID = gid ()

    let (db : DB.T) =
      { tlid = gid (); name = "MyDB"; version = 0; cols = [ "age", TInt ] }

    let ast =
      eFn
        "DB"
        "queryOne"
        4
        [ eVar "MyDB"
          eLambda [ "value" ] (eFieldAccess (EVariable(varID, "value")) "age") ]

    let! (results, state) =
      executionStateForPreview "test" (Map [ "MyDB", db ]) Map.empty

    let state =
      { state with libraries = { state.libraries with stdlib = Map.empty } }

    let! _value = Exe.executeExpr state Map.empty ast

    Expect.equal
      (Dictionary.get varID results)
      (Some(AT.ExecutedResult(DObj(Map.ofList [ "age", DIncomplete SourceNone ]))))
      "Has an age field"
  }


let testListLiterals : Test =
  testTask "Blank in a list evaluates to Incomplete" {
    let id = gid ()
    let ast = eList [ eInt 1; EBlank id ]
    let! (results : AT.AnalysisResults) = execSaveDvals [] [] ast

    return
      match Dictionary.get id results with
      | Some (AT.ExecutedResult (DIncomplete _)) -> Expect.isTrue true ""
      | _ -> Expect.isTrue false ""
  }


let testRecursionInEditor : Test =
  testTask "results in recursion" {
    let callerID = gid ()
    let skippedCallerID = gid ()

    let fnExpr =
      (eIf
        (eApply (eStdFnVal "" "<" 0) [ eVar "i"; eInt 1 ])
        (eInt 0)
        // infinite recursion
        (EApply(skippedCallerID, eUserFnVal "recurse", [ eInt 2 ], NotInPipe, NoRail)))

    let recurse = testUserFn "recurse" [ "i" ] fnExpr
    let ast = EApply(callerID, eUserFnVal "recurse", [ eInt 0 ], NotInPipe, NoRail)
    let! results = execSaveDvals [] [ recurse ] ast

    Expect.equal
      (Dictionary.get callerID results)
      (Some(AT.ExecutedResult(DInt 0I)))
      "result is there as expected"

    Expect.equal
      (Dictionary.get skippedCallerID results)
      (Some(
        AT.NonExecutedResult(DIncomplete(SourceID(recurse.tlid, skippedCallerID)))
      ))
      "result is incomplete for other path"
  }

let testIfPreview : Test =
  let f cond =
    task {
      let ifID = gid ()
      let thenID = gid ()
      let elseID = gid ()
      let ast = EIf(ifID, cond, EString(thenID, "then"), EString(elseID, "else"))
      let! results = execSaveDvals [] [] ast

      return
        (Dictionary.get ifID results |> Option.unwrapUnsafe,
         Dictionary.get thenID results |> Option.unwrapUnsafe,
         Dictionary.get elseID results |> Option.unwrapUnsafe)
    }
  testManyTask
    "if preview"
    f
    [ (eBool false,
       (AT.ExecutedResult(DStr "else"),
        AT.NonExecutedResult(DStr "then"),
        AT.ExecutedResult(DStr "else")))
      (eNull (),
       (AT.ExecutedResult(DStr "else"),
        AT.NonExecutedResult(DStr "then"),
        AT.ExecutedResult(DStr "else")))
      // fakevals
      (eFn "Test" "errorRailNothing" 0 [],
       (AT.ExecutedResult(DErrorRail(DOption None)),
        AT.NonExecutedResult(DStr "then"),
        AT.NonExecutedResult(DStr "else")))
      (EBlank 999UL,
       (AT.ExecutedResult(DIncomplete(SourceID(7UL, 999UL))),
        AT.NonExecutedResult(DStr "then"),
        AT.NonExecutedResult(DStr "else")))
      // others are true
      (eBool true,
       (AT.ExecutedResult(DStr "then"),
        AT.ExecutedResult(DStr "then"),
        AT.NonExecutedResult(DStr "else")))
      (eInt 5,
       (AT.ExecutedResult(DStr "then"),
        AT.ExecutedResult(DStr "then"),
        AT.NonExecutedResult(DStr "else")))
      (eStr "test",
       (AT.ExecutedResult(DStr "then"),
        AT.ExecutedResult(DStr "then"),
        AT.NonExecutedResult(DStr "else"))) ]



let testFeatureFlagPreview : Test =
  let f cond =
    task {
      let ffID = gid ()
      let oldID = gid ()
      let newID = gid ()
      let ast =
        EFeatureFlag(ffID, cond, EString(oldID, "old"), EString(newID, "new"))
      let! results = execSaveDvals [] [] ast

      return
        (Dictionary.get ffID results |> Option.unwrapUnsafe,
         Dictionary.get oldID results |> Option.unwrapUnsafe,
         Dictionary.get newID results |> Option.unwrapUnsafe)
    }
  testManyTask
    "feature flag preview"
    f
    [ (eBool true,
       (AT.ExecutedResult(DStr "new"),
        AT.NonExecutedResult(DStr "old"),
        AT.ExecutedResult(DStr "new")))
      // everything else should be old
      (eBool false,
       (AT.ExecutedResult(DStr "old"),
        AT.ExecutedResult(DStr "old"),
        AT.NonExecutedResult(DStr "new")))
      (eFn "Test" "errorRailNothing" 0 [],
       (AT.ExecutedResult(DStr "old"),
        AT.ExecutedResult(DStr "old"),
        AT.NonExecutedResult(DStr "new")))
      (eBlank (),
       (AT.ExecutedResult(DStr "old"),
        AT.ExecutedResult(DStr "old"),
        AT.NonExecutedResult(DStr "new")))
      (eInt 5,
       (AT.ExecutedResult(DStr "old"),
        AT.ExecutedResult(DStr "old"),
        AT.NonExecutedResult(DStr "new")))
      (eStr "test",
       (AT.ExecutedResult(DStr "old"),
        AT.ExecutedResult(DStr "old"),
        AT.NonExecutedResult(DStr "new")))
      (eNull (),
       (AT.ExecutedResult(DStr "old"),
        AT.ExecutedResult(DStr "old"),
        AT.NonExecutedResult(DStr "new"))) ]

let testLambdaPreview : Test =
  let f body =
    task {
      let lID = gid ()
      let bodyID = Expr.toID body
      let ast = ELambda(lID, [], body)
      let! results = execSaveDvals [] [] ast

      return (Dictionary.get lID results, Dictionary.get bodyID results)
    }
  testManyTask
    "lambda preview"
    f
    [ (EString(65UL, "body"),
       (Some(
         AT.ExecutedResult(
           DFnVal(
             Lambda(
               { parameters = []
                 symtable = Map.empty
                 body = EString(65UL, "body") }
             )
           )
         )
        ),
        Some(AT.NonExecutedResult(DStr "body")))) ]



let testMatchPreview : Test =
  testTask "test match evaluation" {
    let mid = gid ()
    let pIntId = gid ()
    let pFloatId = gid ()
    let pBoolId = gid ()
    let pStrId = gid ()
    let pNullId = gid ()
    let pBlankId = gid ()
    let pOkVarOkId = gid ()
    let pOkVarVarId = gid ()
    let pOkBlankOkId = gid ()
    let pOkBlankBlankId = gid ()
    let pNothingId = gid ()
    let pVarId = gid ()
    let binopFnValId = gid ()
    let intRhsId = gid ()
    let floatRhsId = gid ()
    let boolRhsId = gid ()
    let strRhsId = gid ()
    let nullRhsId = gid ()
    let blankRhsId = gid ()
    let okVarRhsId = gid ()
    let okBlankRhsId = gid ()
    let nothingRhsId = gid ()
    let okVarRhsVarId = gid ()
    let okVarRhsStrId = gid ()
    let varRhsId = gid ()

    let astFor (arg : Expr) =
      EMatch(
        mid,
        arg,
        [ (PInteger(pIntId, 5I), EInteger(intRhsId, 17I))
          (PFloat(pFloatId, 5.6), EString(floatRhsId, "float"))
          (PBool(pBoolId, false), EString(boolRhsId, "bool"))
          (PString(pStrId, "myStr"), EString(strRhsId, "str"))
          (PNull(pNullId), EString(nullRhsId, "null"))
          (PBlank(pBlankId), EString(blankRhsId, "blank"))
          (PConstructor(pOkBlankOkId, "Ok", [ PBlank pOkBlankBlankId ]),
           EString(okBlankRhsId, "ok blank"))
          (PConstructor(pOkVarOkId, "Ok", [ PVariable(pOkVarVarId, "x") ]),
           EApply(
             okVarRhsId,
             EFQFnValue(binopFnValId, FQFnName.stdlibFqName "" "++" 0),
             [ EString(okVarRhsStrId, "ok: "); EVariable(okVarRhsVarId, "x") ],
             NotInPipe,
             NoRail
           ))
          (PConstructor(pNothingId, "Nothing", []),
           EString(nothingRhsId, "constructor nothing"))
          (PVariable(pVarId, "name"), EVariable(varRhsId, "name")) ]
      )

    let check
      (msg : string)
      (arg : Expr)
      (expected : List<id * string * AT.ExecutionResult>)
      =
      task {
        let ast = astFor arg
        let! results = execSaveDvals [] [] ast
        // check expected values are there
        List.iter
          (fun (id, name, value) ->
            Expect.equal
              (Dictionary.get id results)
              (Some value)
              $"{msg}: {id}, {name}")
          expected


        // Check all the other values are there and are NotExecutedResults
        let argIDs = ref []

        arg
        |> Ast.postTraversal
             (fun e ->
               argIDs := (Expr.toID e) :: !argIDs
               e)
        |> ignore<Expr>

        let expectedIDs = (mid :: !argIDs) @ List.map Tuple3.first expected |> Set
        Expect.isGreaterThan results.Count (Set.count expectedIDs) "sanity check"

        results
        |> Dictionary.keys
        |> Seq.toList
        |> List.iter
             (fun id ->
               if not (Set.contains id expectedIDs) then
                 match Dictionary.get id results with
                 | Some (AT.ExecutedResult dv) ->
                   Expect.isTrue
                     false
                     $"{msg}: found unexpected execution result ({id}: {dv})"
                 | None -> Expect.isTrue false "missing value"
                 | Some (AT.NonExecutedResult _) -> ())
      }

    let er x = AT.ExecutedResult x in

    let ner x = AT.NonExecutedResult x in
    let inc iid = DIncomplete(SourceID(id 7, iid)) in

    do!
      check
        "int match"
        (eInt 5)
        [ (pIntId, "matching pat", er (DInt 5I))
          (intRhsId, "matching rhs", er (DInt 17I))
          (pVarId, "2nd matching pat", ner (DInt 5I))
          (varRhsId, "2nd matching rhs", ner (DInt 5I)) ]

    do!
      check
        "non match"
        (eInt 6)
        [ (pIntId, "non matching pat", ner (DInt 5I))
          (intRhsId, "non matching rhs", ner (DInt 17I))
          (pFloatId, "float", ner (DFloat 5.6))
          (floatRhsId, "float rhs", ner (DStr "float"))
          (pBoolId, "bool", ner (DBool false))
          (boolRhsId, "bool rhs", ner (DStr "bool"))
          (pNullId, "null", ner DNull)
          (nullRhsId, "null rhs", ner (DStr "null"))
          (pOkVarOkId, "ok pat", ner (inc pOkVarOkId))
          (pOkVarVarId, "var pat", ner (inc pOkVarVarId))
          (okVarRhsId, "ok rhs", ner (inc okVarRhsVarId))
          (okVarRhsVarId, "ok rhs var", ner (inc okVarRhsVarId))
          (okVarRhsStrId, "ok rhs str", ner (DStr "ok: "))
          (pNothingId, "ok pat", ner (DOption None))
          (nothingRhsId, "rhs", ner (DStr "constructor nothing"))
          (pOkBlankOkId, "ok pat", ner (inc pOkBlankOkId))
          (pOkBlankBlankId, "blank pat", ner (inc pOkBlankBlankId))
          (okBlankRhsId, "rhs", ner (DStr "ok blank"))
          (pVarId, "catch all pat", er (DInt 6I))
          (varRhsId, "catch all rhs", er (DInt 6I)) ]

    do!
      check
        "float"
        (eFloat Positive 5I 6I)
        [ (pFloatId, "pat", er (DFloat 5.6))
          (floatRhsId, "rhs", er (DStr "float")) ]

    do!
      check
        "bool"
        (eBool false)
        [ (pBoolId, "pat", er (DBool false)); (boolRhsId, "rhs", er (DStr "bool")) ]

    do!
      check
        "null"
        (eNull ())
        [ (pNullId, "pat", er DNull); (nullRhsId, "rhs", er (DStr "null")) ]

    do!
      check
        "ok: y"
        (eConstructor "Ok" [ eStr "y" ])
        [ (pOkBlankOkId, "ok pat", ner (inc pOkBlankOkId))
          (pOkBlankBlankId, "blank pat", ner (inc pOkBlankBlankId))
          (okBlankRhsId, "rhs", ner (DStr "ok blank"))
          (pOkVarOkId, "ok pat", er (DResult(Ok(DStr "y"))))
          (binopFnValId,
           "fnval",
           er (DFnVal(FnName(FQFnName.stdlibFqName "" "++" 0))))
          (pOkVarVarId, "var pat", er (DStr "y"))
          (okVarRhsId, "rhs", er (DStr "ok: y"))
          (okVarRhsVarId, "rhs", er (DStr "y"))
          (okVarRhsStrId, "str", er (DStr "ok: ")) ]

    do!
      check
        "ok: blank"
        (eConstructor "Ok" [ EBlank(gid ()) ])
        [ (pOkBlankOkId, "blank pat", ner (inc pOkBlankOkId))
          (pOkBlankBlankId, "blank pat", ner (inc pOkBlankBlankId))
          (okBlankRhsId, "blank rhs", ner (DStr "ok blank"))
          (pOkVarOkId, "ok pat", ner (inc pOkVarOkId))
          (pOkVarVarId, "var pat", ner (inc pOkVarVarId))
          (okVarRhsId, "rhs", ner (inc okVarRhsVarId))
          (okVarRhsVarId, "rhs var", ner (inc okVarRhsVarId))
          (okVarRhsStrId, "str", ner (DStr "ok: ")) ]

    do!
      check
        "nothing"
        (eConstructor "Nothing" [])
        [ (pNothingId, "ok pat", er (DOption None))
          (nothingRhsId, "rhs", er (DStr "constructor nothing")) ]
  // TODO: test constructor around a literal
  // TODO: constructor around a variable
  // TODO: constructor around a constructor around a value
  }

let tests =
  testList
    "ExecutionUnitTests"
    [ testListLiterals
      testRecursionInEditor
      testIfPreview
      testLambdaPreview
      testFeatureFlagPreview
      testMatchPreview
      testExecFunctionTLIDs
      testErrorRailUsedInAnalysis
      testOtherDbQueryFunctionsHaveAnalysis ]
