﻿module Logary.Metrics.WinPerfCounters

open System.Diagnostics

open FSharp.Actor

open Logary
open Logary.Measure
open Logary.Metric
open Logary.Internals

open Logary.WinPerfCounter

/// Configuration for the WinPerfCounters probe
type WinPerfCounterConf =
  { initCounters : PerfCounter list }

type WinPerfCountersMsg =
  | Register of PerfCounter
  | Unregister of PerfCounter

module private Impl =

  type WPCState =
    { lastValues : Map<DP, PC * ``measure``> }
 
  let emptyState =
    { lastValues = Map.empty }

  /// A unified naming scheme for the names of performance counters
  module Naming =
    let calcDP (c : PerfCounter) =
      let fstr instance =
        match instance with
        | None -> sprintf "%s|%s" c.category c.counter
        | Some inst -> sprintf "%s|%s|%s" c.category c.counter inst
      fstr c.instance |> DP

    let toCounter (DP dp) =
      match dp.Split '|' with
      | ss when ss.Length = 2 -> { category = ss.[0]; counter = ss.[1]; instance = None }
      | ss -> { category = ss.[0]; counter = ss.[1]; instance = Some ss.[2] }

  let tryGetPc (lastValues : Map<DP, PC * ``measure``>) dp =
    lastValues
    |> Map.tryFind dp
    |> Option.map fst
    |> function
    | None -> mkPc (Naming.toCounter dp)
    | x -> x

  // in the first incarnation, Sample doesn't do a fan-out, so beware of slow
  // perf counters
  let loop (conf : WinPerfCounterConf) (inbox : IActor<_>) =
    let rec loop (state : WPCState) = async {
      let! msg, _ = inbox.Receive()
      match msg with
      | GetValue (datapoints, replChan) ->
        // what are the values for the requested data points?
        let ret =
          datapoints
          |> List.map (flip Map.tryFind state.lastValues)
          |> List.zip datapoints
          |> List.filter (Option.isSome << snd)
          |> List.map (function
            | dp, Some (pc, msr) -> dp, msr
            | dp, None -> failwith "isSome above says this won't happen")
        replChan.Reply ret
        return! loop state
      | GetDataPoints replChan ->
        // what are the DPs I support?
        let dps = state.lastValues |> Map.fold (fun acc key _ -> key :: acc) []
        replChan.Reply dps
        return! loop state
      | Update msr ->
        // update specific DP
        let key = DP msr.m_path
        match tryGetPc state.lastValues key with
        | None ->
          // perf counter key cannot be made a performance counter out of,
          // so continue with our business
          return! loop state
        | Some pc ->
          // otherwise update the state with the new measurement
          let state' = { state with lastValues = state.lastValues |> Map.put key (pc, msr) }
          return! loop state'
      | Sample ->
        // read data from external sources and update state

        return! loop state
      | Shutdown ->
        return! shutdown state
      | Reset ->
        return! loop state
      }

    and shutdown state = async.Return ()

    loop emptyState

let create conf = MetricUtils.stdNamedMetric Probe (Impl.loop conf)