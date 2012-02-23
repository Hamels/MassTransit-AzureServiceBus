﻿(* 
 Copyright 2012 Henrik Feldt
  
 Licensed under the Apache License, Version 2.0 (the "License"); you may not use
 this file except in compliance with the License. You may obtain a copy of the 
 License at 
 
     http://www.apache.org/licenses/LICENSE-2.0 
 
 Unless required by applicable law or agreed to in writing, software distributed
 under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR 
 CONDITIONS OF ANY KIND, either express or implied. See the License for the 
 specific language governing permissions and limitations under the License.
*)
namespace MassTransit.Async

open Microsoft.ServiceBus
open Microsoft.ServiceBus.Messaging

open FSharp.Control

open System
open System.Runtime.CompilerServices
open System.Collections.Concurrent

open MassTransit.AzureServiceBus
open MassTransit.Async.Queue
open MassTransit.Async.AsyncRetry

/// Create a new receiver, with a queue description,
/// a factory for messaging factories and some control flow data
type Receiver(desc   : QueueDescription,
              newMf  : (unit -> MessagingFactory),
              nm     : NamespaceManager,
              ?maxReceived : int,
              ?concurrency : int,
              ?mfEveryNthConcurrentAsync : int) =
  /// Field denoting whether the receiver is in the started state or, if false, in the stopped state.
  let mutable started = false
  let logger = MassTransit.Logging.Logger.Get(typeof<Receiver>)
  
  let queueSize = defaultArg maxReceived 50

  /// Field that contains the collection of messages
  let messages = new BlockingCollection<_>(queueSize)
  
  let timeout = TimeSpan.FromMilliseconds 50.0
  let concurrency = defaultArg concurrency 250
  let nthAsync = defaultArg mfEveryNthConcurrentAsync 100 // in initAsyncs

  let azQDesc = desc.Inner

  let worker client =
      async {
        let! cancelled = Async.CancellationToken
        while cancelled.IsCancellationRequested |> not do
          let! bmsg = timeout |> recv client
          if bmsg <> null then
            logger.Debug("received message!")
            messages.Add bmsg
          else
            logger.Debug("got null msg due to timeout receiving") }

  /// Starts (stop-1)/100 new clients and message factories, so for stop=501
  /// it loops 500 times and starts 5 new clients
  let rec initAsyncs desc newMf stop curr recvs =
    async {
      match curr with
      | _ when stop = curr ->
        return recvs
      | _ when curr % nthAsync = 0 || curr = 1 ->
        logger.Info("created a new messaging factory")
        let mf = newMf ()
        let! recv = desc |> newReceiver mf
        return! initAsyncs desc newMf stop (curr+1) ((mf, recv) :: recvs)
      | _ -> 
        return! initAsyncs desc newMf stop (curr+1) recvs }

  let mfAndRecvsColl = Async.RunSynchronously (initAsyncs desc newMf (concurrency+1) 1 [])

  /// Closes the receivers and message factories created
  let closeColl () =
    logger.InfoFormat("closing all ({0} of them) message factories and receivers", mfAndRecvsColl.Length)
    for (mf, recv) in mfAndRecvsColl do
      if not(mf.IsClosed) then
        try
          mf.Close()
        with
          | x -> logger.Error("could not close messaging factory", x)
      if not(recv.IsClosed) then
        try
          recv.Close()
        with
          | x -> logger.Error("could not close receiver", x)

  /// Starts the receiver which starts the consuming from the service bus
  /// and creates the queue if it doesn't exist
  member x.Start () =
    logger.InfoFormat("started for queue {0}", desc)
    if started then ()
    else
      started <- true
      desc |> create nm |> Async.RunSynchronously
      for (mf, client) in mfAndRecvsColl do
        worker client |> Async.Start

  /// Stops the receiver which closes all messaging factories and message receivers 
  /// that it holds onto.
  member x.Stop () =
    logger.Info("Stop called on Recveiver")
    if not(started) then ()
    else 
      started <- false
      Async.CancelDefaultToken ()

  member x.Get(timeout : TimeSpan) =
    let mutable item = null
    let _ = messages.TryTake(&item, timeout.Milliseconds)
    item

  member __.TryTake(?timeout) =
    let timeout = defaultArg timeout <| TimeSpan.FromMilliseconds 50.0
    let mutable item = null
    if messages.TryTake(&item, timeout.Milliseconds)
    then Some(item)
    else None

  member __.Consume() =
    asyncSeq {
      while true do
        yield messages.Take() }

  interface System.IDisposable with
    member x.Dispose () = 
      logger.DebugFormat("dispose called for receiver on '{0}'", desc.Path)
      x.Stop()
      closeColl ()
      // clear all message locks that we may have
      while messages.Count > 0 do
        async {
          let m = ref null
          if messages.TryTake(m, TimeSpan.FromMilliseconds(4.0)) then 
            try do! Async.FromBeginEnd((!m).BeginAbandon, (!m).EndAbandon)
            with 
            | x ->
              let entry = sprintf "could not abandon message#%s" <| (!m).MessageId
              logger.Error(entry, x) }
          |> Async.Start

      messages.Dispose()

[<Extension>]
type ReceiverModule =
  static member StartReceiver(address     : AzureServiceBusEndpointAddress,
                              maxReceived : int,
                              concurrency : int) =
    let r = new Receiver(address.QueueDescription, (fun () -> address.MessagingFactoryFactory.Invoke()), address.NamespaceManager, maxReceived, concurrency)
    r.Start ()
    r