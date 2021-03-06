﻿module private TO_THINK_ABOUT

open Logary

// https://github.com/codahale/metrics/blob/master/metrics-core/src/main/java/com/codahale/metrics/ExponentiallyDecayingReservoir.java
// http://dimacs.rutgers.edu/~graham/pubs/papers/fwddecay.pdf
// https://www.youtube.com/watch?v=qURhXHbxbDU
// https://gist.github.com/haf/5e770676d8c007ca80c1
// https://github.com/codahale/metrics/blob/master/metrics-core/src/main/java/com/codahale/metrics/UniformReservoir.java
// http://www.cs.umd.edu/~samir/498/vitter.pdf
// http://researcher.watson.ibm.com/researcher/files/us-dpwoodru/tw11.pdf
// https://en.wikipedia.org/wiki/Reservoir_sampling

/// A meter measures the rate of events over time (e.g., "requests per second").
/// In addition to the mean rate, meters also track 1-, 5-, and 15-minute moving
/// averages.
type Meter =
  inherit Named
  abstract Mark : uint32 -> unit

module Time =
  open System.Diagnostics

  open Logary.Internals
  open Logary.Logger
  open Logary.Measure

  /// Capture a timer metric with a given metric-level and metric-path.
  [<CompiledName "TimeLevel">]
  let timelvl (logger : Logger) lvl path f =
    if lvl < logger.Level then f ()
    else
      let now = Date.now ()
      let sw = Stopwatch.StartNew()
      try
        f ()
      finally
        sw.Stop()
        { m_value     = L (sw.ElapsedTicks)
          m_path      = path
          m_timestamp = now
          m_level     = lvl
          m_unit      = Time Ticks
          m_tags      = []
          m_data      = Map.empty }
        |> logger.Measure

module internal Play =
  open System

  open Measure
  open HealthCheck
  open WinPerfCounter

  module Measure =
    let create' fValueTr fLevel rawValue =
      let m = Measure.create (DP []) (fValueTr rawValue)
      { m with m_level = fLevel (getValueFloat m) }

  module Categorisation =
    /// Finds the bucket that is less than or equal in value to the sample, yielding
    /// its corresponding label.
    let lteBucket (buckets : _ seq) (labels : _ seq) sample =
      Seq.take (Seq.length buckets) labels // all but last
      |> Seq.map2 (fun b l -> b, l, sample <= b) buckets // find first that is lte
      |> Seq.tryFind (fun (b, l, ok) -> ok) // any matches?
      |> Option.map (fun (_, l, _) -> l) // find its label then
      |> Option.fold (fun s t -> t) (Seq.last labels) // otherwise pick last label

    /// Divides a given value first by the divisor, then assigns it a bucket of
    /// `levels`
    let percentBucket divisor buckets labels =
      Measure.create'
        (fun (v : float) -> v / divisor)
        (lteBucket buckets labels)

    /// Divides a given value first by the divisor, then assigns it a bucket of
    /// Info, Warn or Error.
    let percentBucket' divisor =
      percentBucket divisor [0.8; 0.9] [Info; Warn; Error]

  let cpus =
    [ "% Processor Time"
      "% User Time"
      "% Interrupt Time"
      "% Processor Time" ]
    |> List.map (fun counter ->
      let wpc = { category = "Processor"
                  counter  = counter
                  instance = NotApplicable }
      toHealthCheck wpc (Categorisation.percentBucket' 100.))

  // TODO: what about CPU when percent >= .99 for over x seconds

  open System.Text

  let clr_proc =
    let cat = ".NET CLR Memory"
    let inst = pidInstance ()
    let wpc = { category = cat; counter  = "% Time in GC"; instance = inst }
    let MiB = 1024.f * 1024.f
    let toMiB = (fun v -> v / MiB)
    let descCounters =
      [ "Gen 0 Heap Size", toMiB, "MiB"
        "Gen 1 Heap Size", toMiB, "MiB"
        "Gen 2 Heap Size", toMiB, "MiB"
        "# Gen 0 Collections", id, ""
        "# Gen 1 Collections", id, ""
        "# Gen 2 Collections", id, "" ]
      |> List.map (fun (counter, fval, valUnit) -> toPC' cat counter inst, fval, valUnit)
      |> List.filter (fun (c, _, _) -> Option.isSome c)
      |> List.map (fun (c, fval, valUnit) -> c.Value, fval, valUnit)

    let tf =
      Categorisation.percentBucket 100. [0.05; 0.5] [Info; Warn; Error]
      >> fun measuree ->
        descCounters
        |> List.fold
            (fun (sb : StringBuilder) (counter, fval, valUnit) ->
              let line = String.Format("{0}: {1:0.###} {2}", counter.CounterName, fval(counter.NextValue()), valUnit)
              sb.AppendLine(line) |> ignore
              sb)
            (StringBuilder())
        |> sprintf "%O"
        |> (fun desc -> HealthCheck.setDesc desc measuree)

    toHealthCheck wpc tf |> hasResources (descCounters |> List.map (fun (c, _, _) -> c))

  let printAll checks =
    let printSingle (check : HealthCheck) =
      match check.GetValue() with
      | NoValue -> printfn "%s: -" check.Name
      | HasValue v ->
        let m = v.Measure
        printfn ">>> [%O] %s: %f\n%s" m.m_level check.Name (getValueFloat m) v.Description
    checks |> Seq.iter printSingle

  // printAll my_appdomain
  // printAll [clr_proc]

  // TODO: register new health checks as conf
  // TODO: register and unregister health checks at runtime

/// A map from the actor id to health check actor. It's implicit that each
/// of these actors are linked to the supervisor actor.
//        hcs        : Map<string, HealthCheckMessage IActor>