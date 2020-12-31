﻿namespace LowLevelDesign.WTrace.Tracing

open System
open System.Collections.Generic
open LowLevelDesign.WTrace
open LowLevelDesign.WTrace.Events.FieldValues

type ITraceData =
    abstract FindProcess: struct (int32 * DateTime) -> Process

type IMutableTraceData =
    inherit ITraceData
    abstract HandleAndFilterSystemEvent: TraceEventWithFields -> bool

type ProcessFilter =
| ProcessIdFilter of pid : int32 * includeChildren : bool
| NoFilter

module TraceData =

    [<AutoOpen>]
    module private H =
        let currentProcessId = Diagnostics.Process.GetCurrentProcess().Id

        type ProcessMap = Dictionary<int32, array<Process>>

        let (===) a b = String.Equals(a, b, StringComparison.Ordinal)

        let logger = Logger.Tracing

        let unknownProcess =
            { Pid = -1
              ParentPid = -1
              ProcessName = "??"
              ImageFileName = "??"
              CommandLine = "??"
              ExtraInfo = ""
              StartTime = DateTime.MinValue
              ExitTime = DateTime.MaxValue
              ExitStatus = -1 }

        let findProcess (processes: IDictionary<int32, array<Process>>) struct (pid, timeStamp) =
            let mutable procs = null
            if processes.TryGetValue(pid, &procs) then
                procs
                |> Array.find (fun p -> p.StartTime <= timeStamp) // FIXME: what if we can't find the process?
            else
                { unknownProcess with Pid = pid }

        let handleProcessStart (processes : ProcessMap) ev flds =
            let imageFileName = flds |> getFieldValue "ImageFileName" |> db2s
            let proc = {
                Pid = ev.ProcessId
                ProcessName = getProcessName imageFileName
                ParentPid = flds |> getFieldValue "ParentID" |> db2i32
                ImageFileName = imageFileName
                StartTime = if ev.EventName === "Process/Start" then ev.TimeStamp else DateTime.MinValue
                CommandLine = flds |> getFieldValue "CommandLine" |> db2s
                ExitTime = DateTime.MaxValue
                ExtraInfo = ""
                ExitStatus = 0
            }

            match processes.TryGetValue(proc.Pid) with
            | (true, procs) ->
                Debug.Assert(procs.Length > 0, "[SystemEvents] there should be always at least one process in the list")
                // It may happen that a session started after creating the main session and before the rundown 
                // session started. We can safely skip this process.
                if procs.[0].ExitTime < DateTime.MaxValue then
                    processes.[proc.Pid] <- procs |> Array.append [| proc |]
            | (false, _) ->
                processes.Add(proc.Pid, [| proc |])
            proc

        let handleProcessExit (processes : ProcessMap) ev =
            match processes.TryGetValue(ev.ProcessId) with
            | (true, procs) ->
                match procs with
                | [| |] -> Debug.Assert(false, "[SystemEvents] there should be always at least one process in the list")
                | arr -> arr.[0] <- { arr.[0] with ExitTime = ev.TimeStamp; ExitStatus = ev.Result } // the first one is always the running one
            | (false, _) -> logger.TraceWarning(sprintf "Trying to record exit of a non-existing process: %d" ev.ProcessId)

    let empty =
        { new ITraceData with
            member _.FindProcess _ = unknownProcess }

    let createMutable (traceFilter) =
        let processes = ProcessMap()
        // collection used for filtering
        let processIds = HashSet<int32>()
        processIds.Add(currentProcessId) |> ignore

        { new IMutableTraceData with
            member _.FindProcess struct (pid, timestamp) =
                lock processes (fun () -> findProcess processes struct (pid, timestamp))

            member _.HandleAndFilterSystemEvent ev =
                match ev with
                | TraceEventWithFields (ev, _) when ev.ProcessId = currentProcessId ->
                    false // we don't want to process events generated by the current process
                | TraceEventWithFields (ev, flds) when ev.EventName === "Process/Start" ||  ev.EventName === "Process/DCStart" ->
                    let proc = lock processes (fun () -> handleProcessStart processes ev flds)

                    if ev.EventName === "Process/Start" then // we don't want to save rundown events
                        match traceFilter with
                        | ProcessIdFilter (pid, children) ->
                            Debug.Assert((pid = proc.Pid), "[TraceData] inconsistent PIDs")
                            if processIds.Contains(pid) then true
                            elif (children && processIds.Contains(proc.ParentPid)) then
                                processIds.Add(pid) |> ignore
                                true
                            else false
                        | NoFilter -> true
                    else false
                | TraceEventWithFields (ev, flds) when ev.EventName === "Process/Stop" || ev.EventName === "Process/DCStop" ->
                    lock processes (fun () -> handleProcessExit processes ev)
                    ev.EventName === "Process/Stop" // we don't want to save rundown events
                | _ -> true
        }

