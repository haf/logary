/// A scheduling actor that can call `Sample` on the metric/probe/health check.
module Logary.Internals.Scheduling
#nowarn "64"

// creds to Dave Thomas for his F# snippet
open System.Threading
  
open Cricket

open NodaTime

type ScheduleMsg =
  | Schedule of (obj -> unit) * obj * Duration * Duration
  | ScheduleOnce of (obj -> unit) * obj * Duration

module private Impl =

  let ms (d : Duration) =
    d.ToTimeSpan().TotalMilliseconds |> int

  let scheduleOnce (delay : Duration) msg receiver (cts: CancellationTokenSource) = async {
    do! Async.Sleep (ms delay)
    if cts.IsCancellationRequested then
      cts.Dispose ()
    else
      receiver msg
    }

  let scheduleMany initialDelay msg receiver delayBetween cts =
    let rec loop time (cts: CancellationTokenSource) = async {
      do! Async.Sleep time
      if cts.IsCancellationRequested then
        cts.Dispose ()
      else
        receiver msg
      return! loop delayBetween cts
    }
    loop initialDelay cts

  let loop =
    let rec loop () = messageHandler {
      let! msg = Message.receive ()
      let cts = new CancellationTokenSource()
      match msg with
      | Schedule (receiver, msg : 'a, initialDelay, delayBetween) ->
        Async.StartImmediate (scheduleMany (ms initialDelay) msg receiver (ms delayBetween) cts)
        do! Message.reply cts
        return! loop ()
      | ScheduleOnce (receiver, msg:'a, delay) ->
        Async.StartImmediate (scheduleOnce delay msg receiver cts)
        do! Message.reply cts
        return! loop ()
    }
    loop ()

/// Create a new scheduler actor
let create () =
  let scheduler = actor {
    name (Ns.create "scheduler")
    body Impl.loop
  }
  Actor.spawn scheduler

/// Schedules a message to be sent to the receiver after the initialDelay.
/// If delayBetween is specified then the message is sent reoccuringly at the
/// delay between interval.
let schedule scheduler (receiver : 'a -> unit) (msg : 'a) initialDelay (delayBetween : _ option) =
  // this is specific to scheduling/sending to actors:
  let swallowInvalidState f x =
    //try
      f x
    //with
    //| Actor.ActorInvalidStatus _ -> ()

  let buildMessage () =
    match delayBetween with
    | Some x ->
      Schedule (unbox >> swallowInvalidState(receiver), msg, initialDelay, x)
    | _ ->
      ScheduleOnce (unbox >> receiver, msg, initialDelay)
  Message.post scheduler buildMessage
