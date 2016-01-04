namespace Logary.Targets

/// A module implementing a text writer target. Useful for writing to the
/// console output, or writing to a custom text writer.
module TextWriter =

  open System.IO
  open Hopac
  open Hopac.Infixes
  open Logary
  open Logary.Internals
  open Logary.Target
  open Logary.Formatting

  /// Configuration for a text writer
  type TextWriterConf =
    { /// A string formatter to specify how to write the log lines
      formatter  : StringFormatter
      /// the non-error text writer to output to
      output     : TextWriter
      /// the error text writer to output to
      error      : TextWriter
      /// whether to flush text writer after each line
      flush      : bool
      /// the log level that is considered 'important' enough to write to the
      /// error text writer
      isErrorAt  : LogLevel }

    static member Create(output, error, ?formatter : StringFormatter) =
      { formatter    = defaultArg formatter JsonFormatter.Default
        output       = output
        error        = error
        flush        = false
        isErrorAt    = LogLevel.Error }

  module internal Impl =

    let loop (twConf : TextWriterConf) (ri : RuntimeInfo)
             (requests : BoundedMb<TargetMessage>)
             (shutdown : Ch<IVar<unit>>) =

      let writeLine (tw : TextWriter) = (tw.WriteLineAsync : string -> _)

      let rec loop () : Job<unit> =
        Alt.choose [
          shutdown ^=> fun ack ->
            printfn "textwriter: disposing"
            twConf.output.Dispose()

            if not (obj.ReferenceEquals(twConf.output, twConf.error)) then
              twConf.error.Dispose()

            (ack *<= () :> Job<_>)

          BoundedMb.take requests ^=> function
            | Log (logMsg, ack) ->
              job {
                let writer = if logMsg.level < twConf.isErrorAt then twConf.output else twConf.error
                do! writeLine writer (twConf.formatter.format logMsg)
                if twConf.flush then
                  do! writer.FlushAsync()

                do! ack *<= ()
                return! loop ()
              }

            | Flush (ack, nack) ->
              job {
                do! twConf.output.FlushAsync()
                do! twConf.error.FlushAsync()
                do! Ch.give ack () <|> nack
                return! loop ()
              }
        ] :> Job<_>

      loop ()

  let create (conf : TextWriterConf) =
    TargetUtils.stdNamedTarget (Impl.loop conf)

  /// Use with LogaryFactory.New( s => s.Target<TextWriter.Builder>() )
  type Builder(conf, callParent : FactoryApi.ParentCallback<Builder>) =
    member x.WriteTo(out : #TextWriter, err : #TextWriter) =
      ! (callParent <| Builder({ conf with output = out; error = err }, callParent))

    new(callParent : FactoryApi.ParentCallback<_>) =
      Builder(TextWriterConf.Create(System.Console.Out, System.Console.Error), callParent)

    interface Logary.Target.FactoryApi.SpecificTargetConf with
      member x.Build name = create conf name

/// The console Target for Logary
module Console =
  open System
  open Logary
  open Logary.Formatting
  open Logary.Target

  /// Colours in hex
  type ConsoleColours =
    { foregroundColor : int
      backgroundColor : int }

  /// Console configuration structure
  type ConsoleConf =
    { formatter : StringFormatter
      colorMap  : (ConsoleColours -> LogLevel -> ConsoleColours) option }
    static member Create formatter =
      { formatter = formatter
        colorMap  = None }

  /// Default console target configuration.
  let empty =
    { formatter = StringFormatter.levelDatetimeMessagePath
      colorMap  = None (* fun col line -> 0x000000 black *) }

  let create conf =
    // TODO: coloured console output
    TextWriter.create
      { formatter = conf.formatter
        output    = Console.Out
        error     = Console.Error
        flush     = false
        isErrorAt = Error }

  /// Use with LogaryFactory.New( s => s.Target<Console.Builder>() )
  type Builder(conf, callParent : FactoryApi.ParentCallback<Builder>) =

    /// Specify the formatting style to use when logging to the console
    member x.WithFormatter( sf : StringFormatter ) =
      ! (callParent <| Builder({ conf with formatter = sf }, callParent))

    /// TODO: implement!
    member x.Colourise() =
      ! (callParent <| Builder(conf, callParent))

    new(callParent : FactoryApi.ParentCallback<_>) =
      Builder(empty, callParent)

    interface Logary.Target.FactoryApi.SpecificTargetConf with
      member x.Build name = create conf name

// boolean IsLogging() method, correct by excluded middle
#nowarn "25"

/// The Debugger target is useful when running in Xamarin Studio or VS.
module Debugger =
  open System.Diagnostics

  open Hopac
  open Hopac.Infixes

  open Logary
  open Logary.Internals
  open Logary.Target
  open Logary.Formatting

  type DebuggerConf =
    { formatter : StringFormatter }
    /// Create a new Debugger configuration with a given formatter (which
    /// formats how the log line and metrics are printed)
    static member Create ?formatter =
      { formatter = defaultArg formatter (StringFormatter.levelDatetimeMessagePathNl) }

  /// Default debugger configuration
  let empty =
    { formatter = StringFormatter.levelDatetimeMessagePathNl }

  module private Impl =

    let loop conf metadata
             (requests : BoundedMb<_>)
             (shutdown : Ch<_>) =
      let formatter = conf.formatter
      let offLevel = 6

      let rec loop () : Job<unit> =
        Alt.choose [
          shutdown ^=> fun ack ->
            ack *<= () :> Job<_>

          BoundedMb.take requests ^=> function
            | Log (message, ack) when Debugger.IsLogging() ->
              job {
                let path = PointName.format message.name
                Debugger.Log(offLevel, path, formatter.format message)
                do! ack *<= ()
                return! loop ()
              }

            | Log _ ->
              loop ()

            | Flush (ackCh, nack) ->
              job {
                do! Ch.give ackCh () <|> nack
                return! loop ()
              }

        ] :> Job<_>

      loop ()

  let create conf =
    TargetUtils.stdNamedTarget (Impl.loop conf)

  /// Use with LogaryFactory.New( s => s.Target<Debugger.Builder>() )
  type Builder(conf, callParent : FactoryApi.ParentCallback<Builder>) =

    /// Specify the formatting style to use when logging to the debugger
    member x.WithFormatter( sf : StringFormatter ) =
      ! (callParent <| Builder({ conf with formatter = sf }, callParent))

    new(callParent : FactoryApi.ParentCallback<_>) =
      Builder(empty, callParent)

    interface Logary.Target.FactoryApi.SpecificTargetConf with
      member x.Build name = create conf name